import pg from 'pg';
import { pool as defaultPool } from '../db/index.js';
import { GatewayRequestOptions, GatewayResponse, ReservationResult } from './types.js';

const { Pool } = pg;

export class RequestGateway {
  static readonly HARD_DAILY_LIMIT = 100;
  static readonly OPERATIONAL_DAILY_LIMIT = 90;
  static readonly MINUTE_CEILING = 8;

  private readonly pool: pg.Pool;

  constructor(customPool?: pg.Pool) {
    this.pool = customPool ?? defaultPool;
  }

  /**
   * Sanitizes strings (URLs, error messages) to strip API keys or secrets.
   */
  static sanitizeSecret(text: string | null | undefined): string {
    if (!text) return '';
    return text
      .replace(/key=[^&]+/gi, 'key=[REDACTED]')
      .replace(/x-apisports-key:\s*[^\s,]+/gi, 'x-apisports-key: [REDACTED]')
      .replace(/apikey=[^&]+/gi, 'apikey=[REDACTED]');
  }

  /**
   * Transactionally reserves a single request quota slot against PostgreSQL.
   * Enforces daily operational/hard budget limits and the 8 requests/minute sliding window.
   * Releases all database locks immediately upon reservation completion.
   */
  async reserveSlot(
    provider: string,
    endpoint: string,
    operation: string,
    priority: 'NORMAL' | 'CRITICAL'
  ): Promise<ReservationResult> {
    const client = await this.pool.connect();
    try {
      await client.query('BEGIN');

      // 1. Ensure daily budget row exists for today
      const budgetId = `budget_${provider}_${new Date().toISOString().slice(0, 10)}`;
      await client.query(
        `INSERT INTO provider_daily_budget (
           id, provider, budget_date, allocated_calls, consumed_calls, remaining_calls, is_exhausted, updated_at
         ) VALUES ($1, $2, CURRENT_DATE, $3, 0, $3, false, NOW())
         ON CONFLICT (provider, budget_date) DO NOTHING`,
        [budgetId, provider, RequestGateway.HARD_DAILY_LIMIT]
      );

      // 2. Lock the budget row for update
      const budgetRes = await client.query(
        `SELECT id, allocated_calls, consumed_calls, remaining_calls, is_exhausted
         FROM provider_daily_budget
         WHERE provider = $1 AND budget_date = CURRENT_DATE
         FOR UPDATE`,
        [provider]
      );

      const budget = budgetRes.rows[0];
      const consumed = budget.consumed_calls;
      const remaining = budget.remaining_calls;

      // 3. Evaluate daily limits
      if (priority === 'NORMAL' && consumed >= RequestGateway.OPERATIONAL_DAILY_LIMIT) {
        await client.query('ROLLBACK');
        return {
          allowed: false,
          reason: 'OPERATIONAL_BUDGET_EXHAUSTED',
          consumedDaily: consumed,
          remainingDaily: remaining,
        };
      }

      if (consumed >= RequestGateway.HARD_DAILY_LIMIT || remaining <= 0) {
        await client.query('ROLLBACK');
        return {
          allowed: false,
          reason: 'HARD_BUDGET_EXHAUSTED',
          consumedDaily: consumed,
          remainingDaily: remaining,
        };
      }

      // 4. Evaluate sliding-window minute rate limiter (8 requests / 60s)
      const recentRes = await client.query(
        `SELECT COUNT(*) as recent_count, MIN(requested_at) as oldest_time
         FROM api_request_ledger
         WHERE provider = $1 AND requested_at >= NOW() - INTERVAL '1 minute'`,
        [provider]
      );

      const recentCount = parseInt(recentRes.rows[0]?.recent_count || '0', 10);
      if (recentCount >= RequestGateway.MINUTE_CEILING) {
        const oldestTime = new Date(recentRes.rows[0].oldest_time).getTime();
        const waitMs = Math.max(100, 60000 - (Date.now() - oldestTime) + 100);
        await client.query('ROLLBACK');
        return {
          allowed: false,
          reason: 'MINUTE_RATE_LIMIT',
          waitMs,
          consumedDaily: consumed,
          remainingDaily: remaining,
        };
      }

      // 5. Reserve slot by inserting in-flight ledger row
      const ledgerId = `ledg_${Date.now()}_${Math.random().toString(36).substring(2, 9)}`;
      const cleanEndpoint = RequestGateway.sanitizeSecret(endpoint);

      await client.query(
        `INSERT INTO api_request_ledger (
           id, provider, endpoint, operation, priority, success, http_status, response_time_ms, cost_units, requested_at
         ) VALUES ($1, $2, $3, $4, $5, false, 0, 0, 1, NOW())`,
        [ledgerId, provider, cleanEndpoint, operation, priority]
      );

      // 6. Update consumed / remaining calls in budget
      const newConsumed = consumed + 1;
      const newRemaining = Math.max(0, RequestGateway.HARD_DAILY_LIMIT - newConsumed);
      await client.query(
        `UPDATE provider_daily_budget
         SET consumed_calls = $1,
             remaining_calls = $2,
             is_exhausted = ($2 <= 0),
             updated_at = NOW()
         WHERE id = $3`,
        [newConsumed, newRemaining, budget.id]
      );

      await client.query('COMMIT');

      return {
        allowed: true,
        ledgerId,
        consumedDaily: newConsumed,
        remainingDaily: newRemaining,
      };
    } catch (err) {
      await client.query('ROLLBACK').catch(() => {});
      throw err;
    } finally {
      client.release();
    }
  }

  /**
   * Finalizes the ledger record with HTTP outcome and reconciles provider quota.
   */
  async finalizeLedgerAndReconcile(
    ledgerId: string,
    provider: string,
    httpStatus: number,
    responseTimeMs: number,
    success: boolean,
    providerDailyRemaining?: number,
    providerMinuteRemaining?: number,
    errorMessage?: string
  ): Promise<void> {
    const cleanError = RequestGateway.sanitizeSecret(errorMessage);

    // Update ledger entry
    await this.pool.query(
      `UPDATE api_request_ledger
       SET http_status = $1,
           response_time_ms = $2,
           success = $3,
           provider_daily_remaining = $4,
           provider_minute_remaining = $5,
           error_message = $6
       WHERE id = $7`,
      [
        httpStatus,
        responseTimeMs,
        success,
        providerDailyRemaining ?? null,
        providerMinuteRemaining ?? null,
        cleanError || null,
        ledgerId,
      ]
    );

    // Reconcile daily budget if provider reported lower remaining quota
    if (providerDailyRemaining !== undefined && !isNaN(providerDailyRemaining)) {
      await this.reconcileQuota(provider, providerDailyRemaining);
    }
  }

  /**
   * Reconciles provider quota downward if provider reported fewer remaining requests than local ledger.
   * Uses SQL-level monotonic LEAST to guarantee remaining_calls can ONLY decrease under concurrent/out-of-order responses.
   */
  private async reconcileQuota(provider: string, providerReportedRemaining: number): Promise<void> {
    const client = await this.pool.connect();
    try {
      await client.query('BEGIN');
      const safeReported = Math.max(0, Math.min(RequestGateway.HARD_DAILY_LIMIT, providerReportedRemaining));

      await client.query(
        `UPDATE provider_daily_budget
         SET remaining_calls = LEAST(remaining_calls, $1),
             consumed_calls = GREATEST(consumed_calls, $2 - LEAST(remaining_calls, $1)),
             is_exhausted = (LEAST(remaining_calls, $1) <= 0),
             updated_at = NOW()
         WHERE provider = $3 AND budget_date = CURRENT_DATE`,
        [safeReported, RequestGateway.HARD_DAILY_LIMIT, provider]
      );

      await client.query('COMMIT');
    } catch {
      await client.query('ROLLBACK').catch(() => {});
    } finally {
      client.release();
    }
  }

  /**
   * Executes an external provider call through the full gateway lifecycle:
   * 1. Reservation & rate limit enforcement
   * 2. HTTP execution
   * 3. Ledger audit recording
   * 4. Provider header reconciliation
   * 5. Retry management with fresh reservations
   */
  async execute<T>(
    options: GatewayRequestOptions,
    httpCaller: () => Promise<{ status: number; headers: Headers | Record<string, string>; data: T }>
  ): Promise<GatewayResponse<T>> {
    const provider = options.provider ?? 'api_football';
    const priority = options.priority ?? 'NORMAL';
    const maxRetries = options.maxRetries ?? 0;
    let attempt = 0;

    while (attempt <= maxRetries) {
      // 1. Acquire reservation slot
      let reservation = await this.reserveSlot(provider, options.endpoint, options.operation, priority);

      // If minute rate limited, wait outside transaction and retry
      if (!reservation.allowed && reservation.reason === 'MINUTE_RATE_LIMIT' && reservation.waitMs) {
        if (reservation.waitMs < 65000) {
          await new Promise((res) => setTimeout(res, reservation.waitMs));
          reservation = await this.reserveSlot(provider, options.endpoint, options.operation, priority);
        }
      }

      if (!reservation.allowed) {
        return {
          success: false,
          httpStatus: 429,
          data: null,
          responseTimeMs: 0,
          error: `[REQUEST_GATEWAY] Request denied: ${reservation.reason}`,
          ledgerId: reservation.ledgerId || '',
        };
      }

      const ledgerId = reservation.ledgerId!;
      const startTime = Date.now();

      try {
        const response = await httpCaller();
        const duration = Date.now() - startTime;
        const isSuccess = response.status >= 200 && response.status < 300;

        // Parse rate limit headers
        const { dailyRemaining, minuteRemaining } = this.extractRateLimitHeaders(response.headers, response.data);

        await this.finalizeLedgerAndReconcile(
          ledgerId,
          provider,
          response.status,
          duration,
          isSuccess,
          dailyRemaining,
          minuteRemaining
        );

        // Check if server error is retryable (500, 502, 503, 504)
        if (!isSuccess && [500, 502, 503, 504].includes(response.status) && attempt < maxRetries) {
          attempt++;
          const backoff = Math.min(2000 * Math.pow(2, attempt), 10000);
          await new Promise((res) => setTimeout(res, backoff));
          continue;
        }

        return {
          success: isSuccess,
          httpStatus: response.status,
          data: response.data,
          responseTimeMs: duration,
          providerDailyRemaining: dailyRemaining,
          providerMinuteRemaining: minuteRemaining,
          error: isSuccess ? undefined : `HTTP Error ${response.status}`,
          ledgerId,
        };
      } catch (err: any) {
        const duration = Date.now() - startTime;
        const cleanMessage = RequestGateway.sanitizeSecret(err.message || 'Unknown network error');

        await this.finalizeLedgerAndReconcile(
          ledgerId,
          provider,
          0,
          duration,
          false,
          undefined,
          undefined,
          cleanMessage
        );

        if (attempt < maxRetries) {
          attempt++;
          const backoff = Math.min(2000 * Math.pow(2, attempt), 10000);
          await new Promise((res) => setTimeout(res, backoff));
          continue;
        }

        return {
          success: false,
          httpStatus: 0,
          data: null,
          responseTimeMs: duration,
          error: cleanMessage,
          ledgerId,
        };
      }
    }

    return {
      success: false,
      httpStatus: 0,
      data: null,
      responseTimeMs: 0,
      error: 'Exceeded maximum retries',
      ledgerId: '',
    };
  }

  private extractRateLimitHeaders(
    headers: Headers | Record<string, string>,
    body?: any
  ): { dailyRemaining?: number; minuteRemaining?: number } {
    let dailyRemaining: number | undefined;
    let minuteRemaining: number | undefined;

    const getHeader = (name: string): string | null => {
      if (!headers) return null;
      if (typeof (headers as any).get === 'function') {
        return (headers as any).get(name);
      }
      const lower = name.toLowerCase();
      for (const [k, v] of Object.entries(headers)) {
        if (k.toLowerCase() === lower) return v;
      }
      return null;
    };

    const dailyStr = getHeader('x-ratelimit-requests-remaining') ?? getHeader('x-ratelimit-remaining');
    if (dailyStr !== null) {
      const parsed = parseInt(dailyStr, 10);
      if (!isNaN(parsed)) dailyRemaining = parsed;
    }

    const minuteStr = getHeader('x-ratelimit-remaining-minute');
    if (minuteStr !== null) {
      const parsed = parseInt(minuteStr, 10);
      if (!isNaN(parsed)) minuteRemaining = parsed;
    }

    // On /status response, inspect body response.requests
    if (body?.response?.requests) {
      const current = body.response.requests.current;
      const limitDay = body.response.requests.limit_day;
      if (typeof current === 'number' && typeof limitDay === 'number') {
        const computed = Math.max(0, limitDay - current);
        if (dailyRemaining === undefined) {
          dailyRemaining = computed;
        }
      }
    }

    return { dailyRemaining, minuteRemaining };
  }

  /**
   * Returns safe operational diagnostic metrics for request quota and budget.
   * Guaranteed to contain zero keys or secrets.
   */
  async getDiagnostics(provider = 'api_football'): Promise<{
    provider: string;
    allocatedDaily: number;
    consumedDaily: number;
    remainingDaily: number;
    isExhausted: boolean;
    operationalLimit: number;
    hardLimit: number;
    minuteCeiling: number;
    lastRequestAt: string | null;
    minuteWindowReservations: number;
  }> {
    try {
      const budgetRes = await this.pool.query(
        `SELECT allocated_calls, consumed_calls, remaining_calls, is_exhausted, updated_at
         FROM provider_daily_budget
         WHERE provider = $1 AND budget_date = CURRENT_DATE`,
        [provider]
      );

      const countRes = await this.pool.query(
        `SELECT COUNT(*) as cnt
         FROM request_ledger
         WHERE provider = $1 AND requested_at >= NOW() - INTERVAL '60 seconds'`,
        [provider]
      );

      const lastReqRes = await this.pool.query(
        `SELECT requested_at
         FROM request_ledger
         WHERE provider = $1
         ORDER BY requested_at DESC
         LIMIT 1`,
        [provider]
      );

      const row = budgetRes.rows[0];
      const minuteCount = parseInt(countRes.rows[0]?.cnt || '0', 10);
      const lastReq = lastReqRes.rows[0]?.requested_at;

      return {
        provider,
        allocatedDaily: row ? row.allocated_calls : RequestGateway.HARD_DAILY_LIMIT,
        consumedDaily: row ? row.consumed_calls : 0,
        remainingDaily: row ? row.remaining_calls : RequestGateway.HARD_DAILY_LIMIT,
        isExhausted: row ? row.is_exhausted : false,
        operationalLimit: RequestGateway.OPERATIONAL_DAILY_LIMIT,
        hardLimit: RequestGateway.HARD_DAILY_LIMIT,
        minuteCeiling: RequestGateway.MINUTE_CEILING,
        lastRequestAt: lastReq ? new Date(lastReq).toISOString() : null,
        minuteWindowReservations: minuteCount,
      };
    } catch {
      return {
        provider,
        allocatedDaily: RequestGateway.HARD_DAILY_LIMIT,
        consumedDaily: 0,
        remainingDaily: RequestGateway.HARD_DAILY_LIMIT,
        isExhausted: false,
        operationalLimit: RequestGateway.OPERATIONAL_DAILY_LIMIT,
        hardLimit: RequestGateway.HARD_DAILY_LIMIT,
        minuteCeiling: RequestGateway.MINUTE_CEILING,
        lastRequestAt: null,
        minuteWindowReservations: 0,
      };
    }
  }
}
