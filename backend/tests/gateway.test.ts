import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { RequestGateway } from '../src/gateway/request-gateway.js';
import { pool } from '../src/db/index.js';

describe('RequestGateway', () => {
  let gateway: RequestGateway;
  const testProvider = 'api_football_test';

  beforeEach(async () => {
    gateway = new RequestGateway(pool);
    // Clean up test provider records in footballtray_test
    await pool.query(`DELETE FROM api_request_ledger WHERE provider = $1`, [testProvider]);
    await pool.query(`DELETE FROM provider_daily_budget WHERE provider = $1`, [testProvider]);
  });

  afterEach(async () => {
    await pool.query(`DELETE FROM api_request_ledger WHERE provider = $1`, [testProvider]);
    await pool.query(`DELETE FROM provider_daily_budget WHERE provider = $1`, [testProvider]);
  });

  it('Case 1: consumed = 89, normal request -> allowed', async () => {
    // Seed budget with consumed = 89, remaining = 11
    await pool.query(
      `INSERT INTO provider_daily_budget (
         id, provider, budget_date, allocated_calls, consumed_calls, remaining_calls, is_exhausted, updated_at
       ) VALUES ($1, $2, CURRENT_DATE, 100, 89, 11, false, NOW())`,
      [`budget_${testProvider}_today`, testProvider]
    );

    const res = await gateway.reserveSlot(testProvider, '/status', 'status_probe', 'NORMAL');
    expect(res.allowed).toBe(true);
    expect(res.consumedDaily).toBe(90);
    expect(res.remainingDaily).toBe(10);
    expect(res.ledgerId).toBeDefined();
  });

  it('Case 2: consumed = 90, normal request -> denied (operational limit)', async () => {
    await pool.query(
      `INSERT INTO provider_daily_budget (
         id, provider, budget_date, allocated_calls, consumed_calls, remaining_calls, is_exhausted, updated_at
       ) VALUES ($1, $2, CURRENT_DATE, 100, 90, 10, false, NOW())`,
      [`budget_${testProvider}_today`, testProvider]
    );

    const res = await gateway.reserveSlot(testProvider, '/status', 'status_probe', 'NORMAL');
    expect(res.allowed).toBe(false);
    expect(res.reason).toBe('OPERATIONAL_BUDGET_EXHAUSTED');
  });

  it('Case 3: consumed = 90, critical request -> allowed (consumes safety reserve)', async () => {
    await pool.query(
      `INSERT INTO provider_daily_budget (
         id, provider, budget_date, allocated_calls, consumed_calls, remaining_calls, is_exhausted, updated_at
       ) VALUES ($1, $2, CURRENT_DATE, 100, 90, 10, false, NOW())`,
      [`budget_${testProvider}_today`, testProvider]
    );

    const res = await gateway.reserveSlot(testProvider, '/status', 'status_probe', 'CRITICAL');
    expect(res.allowed).toBe(true);
    expect(res.consumedDaily).toBe(91);
    expect(res.remainingDaily).toBe(9);
  });

  it('Case 4: consumed = 99, critical request -> allowed', async () => {
    await pool.query(
      `INSERT INTO provider_daily_budget (
         id, provider, budget_date, allocated_calls, consumed_calls, remaining_calls, is_exhausted, updated_at
       ) VALUES ($1, $2, CURRENT_DATE, 100, 99, 1, false, NOW())`,
      [`budget_${testProvider}_today`, testProvider]
    );

    const res = await gateway.reserveSlot(testProvider, '/status', 'status_probe', 'CRITICAL');
    expect(res.allowed).toBe(true);
    expect(res.consumedDaily).toBe(100);
    expect(res.remainingDaily).toBe(0);
  });

  it('Case 5: consumed = 100, any request -> denied (hard limit)', async () => {
    await pool.query(
      `INSERT INTO provider_daily_budget (
         id, provider, budget_date, allocated_calls, consumed_calls, remaining_calls, is_exhausted, updated_at
       ) VALUES ($1, $2, CURRENT_DATE, 100, 100, 0, true, NOW())`,
      [`budget_${testProvider}_today`, testProvider]
    );

    const resNormal = await gateway.reserveSlot(testProvider, '/status', 'status_probe', 'NORMAL');
    expect(resNormal.allowed).toBe(false);
    expect(resNormal.reason).toBe('OPERATIONAL_BUDGET_EXHAUSTED');

    const resCritical = await gateway.reserveSlot(testProvider, '/status', 'status_probe', 'CRITICAL');
    expect(resCritical.allowed).toBe(false);
    expect(resCritical.reason).toBe('HARD_BUDGET_EXHAUSTED');
  });

  it('Case 6: 8 requests in 60 seconds -> 9th request denied with MINUTE_RATE_LIMIT', async () => {
    // Insert 8 ledger rows in the last 10 seconds
    for (let i = 0; i < 8; i++) {
      await pool.query(
        `INSERT INTO api_request_ledger (
           id, provider, endpoint, operation, priority, success, http_status, response_time_ms, cost_units, requested_at
         ) VALUES ($1, $2, '/status', 'test', 'NORMAL', true, 200, 50, 1, NOW() - INTERVAL '10 seconds')`,
        [`ledg_test_min_${i}`, testProvider]
      );
    }

    const res = await gateway.reserveSlot(testProvider, '/status', 'status_probe', 'NORMAL');
    expect(res.allowed).toBe(false);
    expect(res.reason).toBe('MINUTE_RATE_LIMIT');
    expect(res.waitMs).toBeGreaterThan(0);
  });

  it('Case 6B: deterministic minute-window recovery after oldest reservation expires', async () => {
    // 1. Insert 8 ledger reservations within the active 60s window
    for (let i = 0; i < 8; i++) {
      await pool.query(
        `INSERT INTO api_request_ledger (
           id, provider, endpoint, operation, priority, success, http_status, response_time_ms, cost_units, requested_at
         ) VALUES ($1, $2, '/status', 'test', 'NORMAL', true, 200, 50, 1, NOW() - INTERVAL '20 seconds')`,
        [`ledg_test_rec_${i}`, testProvider]
      );
    }

    // 2. Verify 9th reservation is rejected
    const blockedRes = await gateway.reserveSlot(testProvider, '/status', 'status_probe', 'NORMAL');
    expect(blockedRes.allowed).toBe(false);
    expect(blockedRes.reason).toBe('MINUTE_RATE_LIMIT');

    // 3. Mock time advancement: shift the oldest reservation to 65 seconds ago (outside the 1-minute window)
    await pool.query(
      `UPDATE api_request_ledger
       SET requested_at = NOW() - INTERVAL '65 seconds'
       WHERE id = 'ledg_test_rec_0'`
    );

    // 4. Verify the next reservation is immediately allowed without real-time sleep
    const recoveredRes = await gateway.reserveSlot(testProvider, '/status', 'status_probe', 'NORMAL');
    expect(recoveredRes.allowed).toBe(true);
    expect(recoveredRes.ledgerId).toBeDefined();
  });

  it('Case 7: concurrent workers with 1 request remaining -> only 1 succeeds', async () => {
    // Budget has 99 consumed, 1 remaining
    await pool.query(
      `INSERT INTO provider_daily_budget (
         id, provider, budget_date, allocated_calls, consumed_calls, remaining_calls, is_exhausted, updated_at
       ) VALUES ($1, $2, CURRENT_DATE, 100, 99, 1, false, NOW())`,
      [`budget_${testProvider}_today`, testProvider]
    );

    // Run 3 concurrent reservations
    const results = await Promise.all([
      gateway.reserveSlot(testProvider, '/status', 'test_1', 'CRITICAL'),
      gateway.reserveSlot(testProvider, '/status', 'test_2', 'CRITICAL'),
      gateway.reserveSlot(testProvider, '/status', 'test_3', 'CRITICAL'),
    ]);

    const successful = results.filter((r) => r.allowed);
    const rejected = results.filter((r) => !r.allowed);

    expect(successful.length).toBe(1);
    expect(rejected.length).toBe(2);
  });

  it('Case 8: provider reports lower remaining quota -> effective budget is reconciled downward', async () => {
    // Initial local state: consumed = 1, remaining = 99
    await pool.query(
      `INSERT INTO provider_daily_budget (
         id, provider, budget_date, allocated_calls, consumed_calls, remaining_calls, is_exhausted, updated_at
       ) VALUES ($1, $2, CURRENT_DATE, 100, 1, 99, false, NOW())`,
      [`budget_${testProvider}_today`, testProvider]
    );

    // Execute request where provider reports daily remaining = 50
    const mockHttp = async () => ({
      status: 200,
      headers: {
        'x-ratelimit-requests-remaining': '50',
      },
      data: { response: { ok: true } },
    });

    const response = await gateway.execute(
      { provider: testProvider, endpoint: '/status', operation: 'probe' },
      mockHttp
    );

    expect(response.success).toBe(true);
    expect(response.providerDailyRemaining).toBe(50);

    // Verify database was reconciled to remaining = 50
    const budgetCheck = await pool.query(
      `SELECT consumed_calls, remaining_calls FROM provider_daily_budget WHERE provider = $1 AND budget_date = CURRENT_DATE`,
      [testProvider]
    );
    expect(budgetCheck.rows[0].remaining_calls).toBe(50);
  });

  it('Case 8B: out-of-order / concurrent responses with different quotas reconcile monotonically to lowest value', async () => {
    // Initial local state: consumed = 0, remaining = 100
    await pool.query(
      `INSERT INTO provider_daily_budget (
         id, provider, budget_date, allocated_calls, consumed_calls, remaining_calls, is_exhausted, updated_at
       ) VALUES ($1, $2, CURRENT_DATE, 100, 0, 100, false, NOW())`,
      [`budget_${testProvider}_today`, testProvider]
    );

    // Reconcile response A (remaining = 50)
    await (gateway as any).reconcileQuota(testProvider, 50);

    let budgetCheck = await pool.query(
      `SELECT remaining_calls, consumed_calls FROM provider_daily_budget WHERE provider = $1 AND budget_date = CURRENT_DATE`,
      [testProvider]
    );
    expect(budgetCheck.rows[0].remaining_calls).toBe(50);

    // Reconcile response B that completed out-of-order reporting higher quota (remaining = 75)
    await (gateway as any).reconcileQuota(testProvider, 75);

    budgetCheck = await pool.query(
      `SELECT remaining_calls, consumed_calls FROM provider_daily_budget WHERE provider = $1 AND budget_date = CURRENT_DATE`,
      [testProvider]
    );
    // Monotonic LEAST guarantees remaining_calls stays 50 (lowest reported), NOT 75
    expect(budgetCheck.rows[0].remaining_calls).toBe(50);
    expect(budgetCheck.rows[0].consumed_calls).toBe(50);
  });

  it('Case 9: provider reports higher remaining quota -> never artificially inflates local budget', async () => {
    // Initial local state: consumed = 10, remaining = 90
    await pool.query(
      `INSERT INTO provider_daily_budget (
         id, provider, budget_date, allocated_calls, consumed_calls, remaining_calls, is_exhausted, updated_at
       ) VALUES ($1, $2, CURRENT_DATE, 100, 10, 90, false, NOW())`,
      [`budget_${testProvider}_today`, testProvider]
    );

    // Provider reports remaining = 99 (higher than local 90)
    const mockHttp = async () => ({
      status: 200,
      headers: {
        'x-ratelimit-requests-remaining': '99',
      },
      data: { response: { ok: true } },
    });

    await gateway.execute({ provider: testProvider, endpoint: '/status', operation: 'probe' }, mockHttp);

    const budgetCheck = await pool.query(
      `SELECT remaining_calls FROM provider_daily_budget WHERE provider = $1 AND budget_date = CURRENT_DATE`,
      [testProvider]
    );
    expect(budgetCheck.rows[0].remaining_calls).toBe(89); // decremented locally, not inflated to 99
  });

  it('Case 10: retry behavior creates distinct ledger records for each attempt', async () => {
    let callCount = 0;
    const mockHttp = async () => {
      callCount++;
      if (callCount === 1) {
        return { status: 503, headers: {}, data: { error: 'Service Unavailable' } };
      }
      return { status: 200, headers: { 'x-ratelimit-requests-remaining': '95' }, data: { ok: true } };
    };

    const response = await gateway.execute(
      { provider: testProvider, endpoint: '/status', operation: 'retry_test', maxRetries: 1 },
      mockHttp
    );

    expect(response.success).toBe(true);
    expect(callCount).toBe(2);

    // Verify 2 separate ledger entries exist in database
    const ledgerCheck = await pool.query(
      `SELECT http_status, success FROM api_request_ledger WHERE provider = $1 ORDER BY requested_at ASC`,
      [testProvider]
    );
    expect(ledgerCheck.rows.length).toBe(2);
    expect(ledgerCheck.rows[0].http_status).toBe(503);
    expect(ledgerCheck.rows[0].success).toBe(false);
    expect(ledgerCheck.rows[1].http_status).toBe(200);
    expect(ledgerCheck.rows[1].success).toBe(true);
  });

  it('Case 11: non-retryable errors (401, 403, 404, 422, 429) do not retry', async () => {
    let callCount = 0;
    const mockHttp = async () => {
      callCount++;
      return { status: 401, headers: {}, data: { error: 'Invalid API Key' } };
    };

    const response = await gateway.execute(
      { provider: testProvider, endpoint: '/status', operation: 'auth_test', maxRetries: 2 },
      mockHttp
    );

    expect(response.success).toBe(false);
    expect(response.httpStatus).toBe(401);
    expect(callCount).toBe(1); // Never retried
  });

  it('Case 12: redacts secrets from endpoint and error messages in ledger', async () => {
    const mockHttp = async () => {
      throw new Error('Connection refused with key=secret_12345_token');
    };

    await gateway.execute(
      { provider: testProvider, endpoint: '/status?key=secret_12345_token', operation: 'redaction_test' },
      mockHttp
    );

    const ledgerCheck = await pool.query(
      `SELECT endpoint, error_message FROM api_request_ledger WHERE provider = $1`,
      [testProvider]
    );

    expect(ledgerCheck.rows[0].endpoint).not.toContain('secret_12345_token');
    expect(ledgerCheck.rows[0].endpoint).toContain('key=[REDACTED]');
    expect(ledgerCheck.rows[0].error_message).not.toContain('secret_12345_token');
    expect(ledgerCheck.rows[0].error_message).toContain('key=[REDACTED]');
  });
});
