import { and, desc, eq, gte, lt, or, sql } from 'drizzle-orm';
import { db as defaultDb, schema } from '../db/index.js';

/**
 * Formal cycle outcome states.
 * While a cycle is active/incomplete, completed_at IS NULL and outcome IS NULL.
 * Upon completion, outcome is strictly one of the 6 defined outcomes below.
 * No 'IN_FLIGHT' outcome exists.
 */
export type SchedulerCycleOutcome =
  | 'IDLE' // Scheduler evaluated conditions and determined no action was needed
  | 'SUCCESS' // Provider action completed successfully and resulted in meaningful domain/entity mutations (fixtures/events updated/inserted)
  | 'NO_OP' // Provider action completed successfully but produced no meaningful domain/entity mutation
  | 'ERROR' // Cycle encountered an unhandled exception or failed
  | 'QUOTA_BLOCKED' // Action was prevented due to daily quota exhaustion or critical reserve protection
  | 'RATE_LIMITED' // Action was prevented due to per-minute provider ceiling or rate limits
  | 'RECOVERABLE_FALLBACK' // Action completed with graceful fallback to cached state due to upstream disruption
  | 'CIRCUIT_OPEN'; // Action was prevented because upstream CircuitBreaker is tripped

export type SchedulerCycleRecord = typeof schema.schedulerCycles.$inferSelect;
export type NewSchedulerCycle = typeof schema.schedulerCycles.$inferInsert;

export type SchedulerErrorType =
  | 'PROVIDER_ERROR'
  | 'QUOTA_EXHAUSTED'
  | 'RATE_LIMITED'
  | 'DATABASE_ERROR'
  | 'CRASH_RECOVERED';

export interface CreateCycleParams {
  id?: string;
  action: string;
  priority?: 'NORMAL' | 'HIGH' | 'CRITICAL';
  reason: string;
  targetCompetitionIds?: number[];
  startedAt?: Date;
}

export interface FinalizeCycleParams {
  completedAt?: Date;
  durationMs?: number;
  outcome: SchedulerCycleOutcome;
  errorType?: SchedulerErrorType | null;
  errorMessage?: string | null;
  fixturesDiscovered?: number;
  fixturesUpdated?: number;
}

export interface CycleSummaryStats {
  requestsCount: number;
  requestsCost: number;
}

export class SchedulerCyclesRepository {
  constructor(private readonly dbInstance = defaultDb) {}

  /**
   * Generates a deterministic, unique cycle ID.
   */
  generateCycleId(now: Date = new Date()): string {
    const timestamp = now.getTime();
    const rand = Math.random().toString(36).substring(2, 9);
    return `cycle_${timestamp}_${rand}`;
  }

  /**
   * Records the initiation of an evaluation cycle.
   * completed_at and outcome remain NULL while in-flight.
   */
  async createCycle(params: CreateCycleParams) {
    const cycleId = params.id ?? this.generateCycleId(params.startedAt);
    const startedAt = params.startedAt ?? new Date();

    const [inserted] = await this.dbInstance
      .insert(schema.schedulerCycles)
      .values({
        id: cycleId,
        action: params.action,
        priority: params.priority ?? 'NORMAL',
        reason: params.reason,
        targetCompetitionIds: params.targetCompetitionIds ?? null,
        startedAt,
        completedAt: null,
        durationMs: null,
        outcome: null,
        providerRequestsCount: 0,
        providerRequestsCost: 0,
        errorType: null,
        errorMessage: null,
        fixturesDiscovered: 0,
        fixturesUpdated: 0,
      })
      .returning();

    return inserted;
  }

  /**
   * Derives correlated external request count and cost from authoritative api_request_ledger.
   * Cycle totals are strictly forensic summaries, NOT the quota authority.
   */
  async getCorrelatedRequestStats(cycleId: string): Promise<CycleSummaryStats> {
    const rows = await this.dbInstance.query.apiRequestLedger.findMany({
      where: and(
        eq(schema.apiRequestLedger.cycleId, cycleId),
        eq(schema.apiRequestLedger.provider, 'api_football')
      ),
      columns: {
        costUnits: true,
      },
    });

    // Authoritative quota counting: only positive costUnits count as billed provider requests
    const billableRows = rows.filter((r) => (r.costUnits ?? 1) > 0);
    const count = billableRows.length;
    const cost = billableRows.reduce((sum, r) => sum + (r.costUnits ?? 1), 0);

    return { requestsCount: count, requestsCost: cost };
  }

  /**
   * Finalizes a cycle with outcome, duration, and correlated request statistics.
   */
  async finalizeCycle(cycleId: string, params: FinalizeCycleParams) {
    const completedAt = params.completedAt ?? new Date();

    // 1. Fetch current cycle record to calculate duration if not explicitly passed
    const existing = await this.getCycleById(cycleId);
    let durationMs = params.durationMs;
    if (durationMs === undefined && existing) {
      durationMs = Math.max(0, completedAt.getTime() - new Date(existing.startedAt).getTime());
    }

    // 2. Derive forensic request count & cost from api_request_ledger
    const stats = await this.getCorrelatedRequestStats(cycleId);

    // 3. Update cycle
    const [updated] = await this.dbInstance
      .update(schema.schedulerCycles)
      .set({
        completedAt,
        durationMs: durationMs ?? 0,
        outcome: params.outcome,
        errorType: params.errorType ?? null,
        errorMessage: params.errorMessage ?? null,
        fixturesDiscovered: params.fixturesDiscovered ?? 0,
        fixturesUpdated: params.fixturesUpdated ?? 0,
        providerRequestsCount: stats.requestsCount,
        providerRequestsCost: stats.requestsCost,
      })
      .where(eq(schema.schedulerCycles.id, cycleId))
      .returning();

    return updated;
  }

  /**
   * Detects and heals any cycles left in-flight by a prior process crash or restart.
   * Marks them with outcome = 'ERROR' and error_type = 'CRASH_RECOVERED'.
   * Never introduces an IN_FLIGHT outcome.
   */
  async recoverInterruptedCycles(now: Date = new Date()): Promise<number> {
    const unfinishedCycles = await this.dbInstance.query.schedulerCycles.findMany({
      where: sql`${schema.schedulerCycles.completedAt} IS NULL`,
    });

    if (unfinishedCycles.length === 0) {
      return 0;
    }

    for (const cycle of unfinishedCycles) {
      const stats = await this.getCorrelatedRequestStats(cycle.id);
      const durationMs = Math.max(0, now.getTime() - new Date(cycle.startedAt).getTime());

      await this.dbInstance
        .update(schema.schedulerCycles)
        .set({
          completedAt: now,
          durationMs,
          outcome: 'ERROR',
          errorType: 'CRASH_RECOVERED',
          errorMessage: 'Interrupted by process crash or restart before cycle completion',
          providerRequestsCount: stats.requestsCount,
          providerRequestsCost: stats.requestsCost,
        })
        .where(eq(schema.schedulerCycles.id, cycle.id));
    }

    return unfinishedCycles.length;
  }

  /**
   * Retrieves the single latest scheduler cycle.
   */
  async getLatestCycle() {
    return await this.dbInstance.query.schedulerCycles.findFirst({
      orderBy: [desc(schema.schedulerCycles.startedAt)],
    });
  }

  /**
   * Retrieves a cycle by ID.
   */
  async getCycleById(id: string) {
    return await this.dbInstance.query.schedulerCycles.findFirst({
      where: eq(schema.schedulerCycles.id, id),
    });
  }

  /**
   * Explicitly prunes old IDLE cycles based on retention policy.
   * Default: deletes IDLE cycles older than 7 days.
   * Actionable cycles (outcome != 'IDLE') and request ledger rows are untouched.
   */
  async pruneIdleCycles(olderThanDays = 7, now: Date = new Date()): Promise<number> {
    const cutoff = new Date(now.getTime() - olderThanDays * 24 * 60 * 60 * 1000);

    const result = await this.dbInstance
      .delete(schema.schedulerCycles)
      .where(
        and(
          eq(schema.schedulerCycles.outcome, 'IDLE'),
          lt(schema.schedulerCycles.startedAt, cutoff)
        )
      );

    return (result as any).rowCount ?? (result as any).changes ?? 0;
  }

  /**
   * Retrieves the latest successful schedule discovery from api_request_ledger for today.
   */
  async getLatestSuccessfulDiscovery(now: Date = new Date()) {
    const todayStr = now.toISOString().slice(0, 10);
    const startOfDay = new Date(todayStr + 'T00:00:00.000Z');
    const nextDay = new Date(startOfDay.getTime() + 24 * 60 * 60 * 1000);

    return await this.dbInstance.query.apiRequestLedger.findFirst({
      where: and(
        eq(schema.apiRequestLedger.success, true),
        gte(schema.apiRequestLedger.requestedAt, startOfDay),
        lt(schema.apiRequestLedger.requestedAt, nextDay),
        or(
          and(
            eq(schema.apiRequestLedger.provider, 'system'),
            eq(schema.apiRequestLedger.operation, 'DISCOVER_SCHEDULE')
          ),
          and(
            eq(schema.apiRequestLedger.provider, 'api_football'),
            eq(schema.apiRequestLedger.operation, 'get_fixtures')
          )
        )
      ),
      orderBy: [desc(schema.apiRequestLedger.requestedAt)],
    });
  }

  /**
   * Retrieves the latest schedule invalidation event from api_request_ledger.
   */
  async getLatestScheduleInvalidation() {
    return await this.dbInstance.query.apiRequestLedger.findFirst({
      where: and(
        eq(schema.apiRequestLedger.provider, 'system'),
        eq(schema.apiRequestLedger.operation, 'INVALIDATE_SCHEDULE')
      ),
      orderBy: [desc(schema.apiRequestLedger.requestedAt)],
    });
  }
}
