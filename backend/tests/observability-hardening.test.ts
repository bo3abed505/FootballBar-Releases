import { describe, it, expect, beforeEach, afterAll, vi } from 'vitest';
import { eq, and, sql } from 'drizzle-orm';
import { db, schema, pool } from '../src/db/index.js';
import { SchedulerCyclesRepository, SchedulerCycleOutcome } from '../src/repositories/scheduler-cycles.repository.js';
import { RequestGateway } from '../src/gateway/request-gateway.js';
import { AcquisitionScheduler } from '../src/acquisition/acquisition-scheduler.js';
import { SyncService } from '../src/acquisition/sync.service.js';
import { FixturesRepository } from '../src/repositories/fixtures.repository.js';
import { FootballProvider, ProviderFixture, ProviderEvent } from '../src/providers/football-provider.js';

describe('Milestone 16.6 — Production Observability & Recovery Hardening', () => {
  const cyclesRepo = new SchedulerCyclesRepository(db);
  const gateway = new RequestGateway();
  const fixturesRepo = new FixturesRepository();

  beforeEach(async () => {
    // Clean test tables
    await pool.query('DELETE FROM scheduler_cycles');
    await pool.query('DELETE FROM api_request_ledger');
    await pool.query('DELETE FROM provider_daily_budget');
  });

  afterAll(async () => {
    await pool.query('DELETE FROM scheduler_cycles');
    await pool.query('DELETE FROM api_request_ledger');
    await pool.query('DELETE FROM provider_daily_budget');
  });

  // ============================================================
  // Requirement 1: Cycle Creation and Finalization
  // ============================================================
  it('Req 1: creates and finalizes a cycle recording duration and timestamps', async () => {
    const cycle = await cyclesRepo.createCycle({
      action: 'DISCOVER_SCHEDULE',
      priority: 'NORMAL',
      reason: 'Schedule not known today',
      targetCompetitionIds: [39, 140],
    });

    expect(cycle.id).toMatch(/^cycle_\d+_[a-z0-9]+$/);
    expect(cycle.action).toBe('DISCOVER_SCHEDULE');
    expect(cycle.completedAt).toBeNull();
    expect(cycle.outcome).toBeNull();

    // Finalize
    const finalized = await cyclesRepo.finalizeCycle(cycle.id, {
      outcome: 'SUCCESS',
      fixturesDiscovered: 10,
      fixturesUpdated: 2,
    });

    expect(finalized).toBeDefined();
    expect(finalized?.completedAt).not.toBeNull();
    expect(finalized?.outcome).toBe('SUCCESS');
    expect(finalized?.fixturesDiscovered).toBe(10);
    expect(finalized?.fixturesUpdated).toBe(2);
    expect(finalized?.durationMs).toBeGreaterThanOrEqual(0);
  });

  // ============================================================
  // Requirement 2 & 4: All Six Outcomes & No IN_FLIGHT
  // ============================================================
  it('Req 2 & 4: supports exactly the 6 formal outcomes and never uses IN_FLIGHT', async () => {
    const validOutcomes: SchedulerCycleOutcome[] = [
      'IDLE',
      'SUCCESS',
      'NO_OP', // Provider action completed successfully but produced no meaningful domain/entity mutation
      'ERROR',
      'QUOTA_BLOCKED',
      'RATE_LIMITED',
    ];

    for (const outcome of validOutcomes) {
      const cycle = await cyclesRepo.createCycle({
        action: 'TEST_ACTION',
        reason: `Testing ${outcome}`,
      });

      // While in-flight, completedAt and outcome are strictly NULL
      expect(cycle.completedAt).toBeNull();
      expect(cycle.outcome).toBeNull();

      const finalized = await cyclesRepo.finalizeCycle(cycle.id, { outcome });
      expect(finalized?.outcome).toBe(outcome);
      expect(finalized?.completedAt).not.toBeNull();
    }
  });

  // ============================================================
  // Requirement 3: Interrupted Cycle Recovery via completed_at IS NULL
  // ============================================================
  it('Req 3: recovers interrupted cycles marking outcome=ERROR and error_type=CRASH_RECOVERED', async () => {
    // Simulate process crash leaving 2 cycles unfinished
    const c1 = await cyclesRepo.createCycle({ action: 'DISCOVER_SCHEDULE', reason: 'Crash 1' });
    const c2 = await cyclesRepo.createCycle({ action: 'POLL_LIVE', reason: 'Crash 2' });

    expect(c1.completedAt).toBeNull();
    expect(c2.completedAt).toBeNull();

    const recoveredCount = await cyclesRepo.recoverInterruptedCycles(new Date());
    expect(recoveredCount).toBe(2);

    const check1 = await cyclesRepo.getCycleById(c1.id);
    const check2 = await cyclesRepo.getCycleById(c2.id);

    expect(check1?.outcome).toBe('ERROR');
    expect(check1?.errorType).toBe('CRASH_RECOVERED');
    expect(check1?.completedAt).not.toBeNull();

    expect(check2?.outcome).toBe('ERROR');
    expect(check2?.errorType).toBe('CRASH_RECOVERED');
    expect(check2?.completedAt).not.toBeNull();
  });

  // ============================================================
  // Requirement 5 & 6: cycleId & competitionId Propagation
  // ============================================================
  it('Req 5 & 6: propagates cycleId and competitionId into api_request_ledger', async () => {
    const cycle = await cyclesRepo.createCycle({
      action: 'DISCOVER_SCHEDULE',
      reason: 'Propagation test',
      targetCompetitionIds: [39],
    });

    const res = await gateway.execute(
      {
        endpoint: '/fixtures?league=39',
        operation: 'get_fixtures',
        cycleId: cycle.id,
        competitionId: '39',
      },
      async () => ({
        status: 200,
        headers: {},
        data: { response: [] },
      })
    );

    expect(res.success).toBe(true);

    const ledgerRows = await db.query.apiRequestLedger.findMany({
      where: eq(schema.apiRequestLedger.cycleId, cycle.id),
    });

    expect(ledgerRows.length).toBe(1);
    expect(ledgerRows[0].cycleId).toBe(cycle.id);
    expect(ledgerRows[0].competitionId).toBe('39');
    expect(ledgerRows[0].costUnits).toBe(1);

    // Finalize cycle and verify correlated stats derivation
    const finalized = await cyclesRepo.finalizeCycle(cycle.id, { outcome: 'NO_OP' });
    expect(finalized?.providerRequestsCount).toBe(1);
    expect(finalized?.providerRequestsCost).toBe(1);
  });

  // ============================================================
  // Requirement 7: Ad-hoc Request with NULL cycleId
  // ============================================================
  it('Req 7: ad-hoc external requests without cycleId store NULL in cycle_id and remain valid', async () => {
    const res = await gateway.execute(
      {
        endpoint: '/status',
        operation: 'status_probe',
      },
      async () => ({
        status: 200,
        headers: {},
        data: { response: { requests: { current: 1, limit_day: 100 } } },
      })
    );

    expect(res.success).toBe(true);

    const row = await db.query.apiRequestLedger.findFirst({
      where: eq(schema.apiRequestLedger.id, res.ledgerId),
    });

    expect(row).toBeDefined();
    expect(row?.cycleId).toBeNull();
    expect(row?.competitionId).toBeNull();
    expect(row?.costUnits).toBe(1);
  });

  // ============================================================
  // Requirement 8: System Markers Remain Zero-Cost
  // ============================================================
  it('Req 8: system discovery and invalidation markers record cost_units = 0 and optional cycleId', async () => {
    const cycleId = 'cycle_test_system_marker';
    await fixturesRepo.markScheduleDiscovered('2026-09-04', new Date(), cycleId);
    await fixturesRepo.invalidateSchedule('2026-09-04', new Date(), cycleId);

    const markers = await db.query.apiRequestLedger.findMany({
      where: eq(schema.apiRequestLedger.provider, 'system'),
    });

    expect(markers.length).toBe(2);
    expect(markers.every((m) => m.costUnits === 0)).toBe(true);
    expect(markers.every((m) => m.cycleId === cycleId)).toBe(true);

    // Diagnostics verify system events do not inflate consumedDaily
    const diag = await gateway.getDiagnostics('api_football');
    expect(diag.consumedDaily).toBe(0);
    expect(diag.systemEvents.count).toBe(2);
    expect(diag.systemEvents.cost).toBe(0);
    expect(diag.systemEvents.byMarker.DISCOVER_SCHEDULE).toBe(1);
    expect(diag.systemEvents.byMarker.INVALIDATE_SCHEDULE).toBe(1);
  });

  // ============================================================
  // Requirement 9: Strict Provider vs System Diagnostics Separation
  // ============================================================
  it('Req 9: separates providerRequests and systemEvents in diagnostics with exact breakdowns', async () => {
    const cycleId = 'cycle_diag_test';

    // 2 provider requests
    await gateway.execute(
      {
        endpoint: '/fixtures?league=39',
        operation: 'get_fixtures',
        cycleId,
        competitionId: '39',
      },
      async () => ({ status: 200, headers: {}, data: {} })
    );

    await gateway.execute(
      {
        endpoint: '/fixtures?league=140',
        operation: 'get_fixtures',
        cycleId,
        competitionId: '140',
      },
      async () => ({ status: 200, headers: {}, data: {} })
    );

    // 1 system marker
    await fixturesRepo.markScheduleDiscovered('2026-09-04', new Date(), cycleId);

    const diag = await gateway.getDiagnostics('api_football');

    expect(diag.consumedDaily).toBe(2);
    expect(diag.remainingDaily).toBe(98);
    expect(diag.mode).toBe('NORMAL');

    // Provider audit
    expect(diag.providerRequests.count).toBe(2);
    expect(diag.providerRequests.cost).toBe(2);
    expect(diag.providerRequests.byOperation['get_fixtures']).toBe(2);
    expect(diag.providerRequests.byCompetition['39']).toBe(1);
    expect(diag.providerRequests.byCompetition['140']).toBe(1);

    // System audit
    expect(diag.systemEvents.count).toBe(1);
    expect(diag.systemEvents.cost).toBe(0);
    expect(diag.systemEvents.byMarker.DISCOVER_SCHEDULE).toBe(1);
  });

  // ============================================================
  // Requirement 10: Quota Authority Remains Exclusively api_request_ledger
  // ============================================================
  it('Req 10: scheduler_cycles totals do not override api_request_ledger as quota authority', async () => {
    // Insert a cycle claiming 50 requests
    await db.insert(schema.schedulerCycles).values({
      id: 'cycle_fake_quota',
      action: 'DISCOVER_SCHEDULE',
      priority: 'NORMAL',
      reason: 'Spoofed count test',
      startedAt: new Date(),
      completedAt: new Date(),
      outcome: 'SUCCESS',
      providerRequestsCount: 50,
      providerRequestsCost: 50,
    });

    // Ledger has 0 rows
    const diag = await gateway.getDiagnostics('api_football');
    expect(diag.consumedDaily).toBe(0);
    expect(diag.remainingDaily).toBe(100);
    expect(diag.mode).toBe('NORMAL');

    // reserveSlot respects ledger, not scheduler_cycles
    const reservation = await gateway.reserveSlot('api_football', '/test', 'test', 'NORMAL');
    expect(reservation.allowed).toBe(true);
    expect(reservation.consumedDaily).toBe(1);
  });

  // ============================================================
  // Requirement 11: Diagnostics Restart Hydration
  // ============================================================
  it('Req 11: hydrates lastCycle and milestones from database upon boot without running cycle', async () => {
    const historicalTime = new Date(Date.now() - 10000);
    await db.insert(schema.schedulerCycles).values({
      id: 'cycle_historical',
      action: 'DISCOVER_SCHEDULE',
      priority: 'NORMAL',
      reason: 'Boot hydration test',
      startedAt: historicalTime,
      completedAt: historicalTime,
      outcome: 'SUCCESS',
      providerRequestsCount: 6,
      providerRequestsCost: 6,
    });

    await fixturesRepo.markScheduleDiscovered('2026-09-04', historicalTime, 'cycle_historical');

    const mockSync: any = { syncWithPlan: vi.fn() };
    const scheduler = new AcquisitionScheduler({
      syncService: mockSync,
      fixturesRepository: fixturesRepo,
      schedulerCyclesRepository: cyclesRepo,
      getBudgetContext: async () => ({ consumedDaily: 6, remainingDaily: 94 }),
    });

    await scheduler.hydrateStateFromDatabase();
    const diag = scheduler.getDiagnostics();

    expect(diag.lastCycle).toBeDefined();
    expect(diag.lastCycle.id).toBe('cycle_historical');
    expect(diag.lastCycle.action).toBe('DISCOVER_SCHEDULE');
    expect(diag.lastCycleOutcome).toBe('SUCCESS');
    expect(diag.milestones.lastSuccessfulDiscovery.cycleId).toBe('cycle_historical');
  });

  // ============================================================
  // Requirement 12: IDLE Pruning Retention Policy
  // ============================================================
  it('Req 12: prunes IDLE cycles older than 7 days, preserves actionable cycles >= 90 days', async () => {
    const now = new Date();
    const eightDaysAgo = new Date(now.getTime() - 8 * 24 * 60 * 60 * 1000);
    const twoDaysAgo = new Date(now.getTime() - 2 * 24 * 60 * 60 * 1000);

    // 1. IDLE cycle 8 days old (should be pruned)
    await db.insert(schema.schedulerCycles).values({
      id: 'cycle_idle_old',
      action: 'IDLE',
      reason: 'Idle old',
      startedAt: eightDaysAgo,
      completedAt: eightDaysAgo,
      outcome: 'IDLE',
    });

    // 2. IDLE cycle 2 days old (should be kept)
    await db.insert(schema.schedulerCycles).values({
      id: 'cycle_idle_recent',
      action: 'IDLE',
      reason: 'Idle recent',
      startedAt: twoDaysAgo,
      completedAt: twoDaysAgo,
      outcome: 'IDLE',
    });

    // 3. Actionable SUCCESS cycle 8 days old (should be kept for at least 90 days)
    await db.insert(schema.schedulerCycles).values({
      id: 'cycle_success_old',
      action: 'DISCOVER_SCHEDULE',
      reason: 'Success old',
      startedAt: eightDaysAgo,
      completedAt: eightDaysAgo,
      outcome: 'SUCCESS',
    });

    // 4. Ledger row from 8 days ago (must NOT be deleted)
    await pool.query(
      `INSERT INTO api_request_ledger (id, provider, endpoint, operation, priority, success, http_status, response_time_ms, cost_units, requested_at, cycle_id)
       VALUES ($1, 'api_football', '/fixtures', 'get_fixtures', 'NORMAL', true, 200, 100, 1, $2, 'cycle_success_old')`,
      ['ledg_old_test', eightDaysAgo]
    );

    const prunedCount = await cyclesRepo.pruneIdleCycles(7, now);
    expect(prunedCount).toBe(1);

    // Verify remaining rows
    const oldIdle = await cyclesRepo.getCycleById('cycle_idle_old');
    const recentIdle = await cyclesRepo.getCycleById('cycle_idle_recent');
    const oldSuccess = await cyclesRepo.getCycleById('cycle_success_old');

    expect(oldIdle).toBeUndefined();
    expect(recentIdle).toBeDefined();
    expect(oldSuccess).toBeDefined();

    // Verify ledger row was NOT touched
    const ledgerRow = await db.query.apiRequestLedger.findFirst({
      where: eq(schema.apiRequestLedger.id, 'ledg_old_test'),
    });
    expect(ledgerRow).toBeDefined();
  });

  // ============================================================
  // Requirement 13: Full Integration of executeSingleCycle with Cycle Tracking
  // ============================================================
  it('Req 13: full cycle execution records start, correlation, and finalization', async () => {
    let capturedFilter: any = null;
    const mockProvider: FootballProvider = {
      providerName: 'mock',
      getCompetitions: async () => [],
      getFixtures: async (filter) => {
        capturedFilter = filter;
        return [];
      },
      getFixtureDetails: async () => null,
      getFixtureEvents: async () => [],
      getStandings: async () => [],
    };

    const sync = new SyncService(mockProvider);
    const mockRepo: any = {
      getActiveSummary: vi.fn().mockResolvedValue({
        liveCount: 0,
        recentlyFinishedCount: 0,
        stalePrematchCount: 0,
        scheduleKnownForToday: false, // forces DISCOVER_SCHEDULE
      }),
      markScheduleDiscovered: vi.fn(),
    };

    const scheduler = new AcquisitionScheduler({
      syncService: sync,
      fixturesRepository: mockRepo,
      schedulerCyclesRepository: cyclesRepo,
      getBudgetContext: async () => ({ consumedDaily: 10, remainingDaily: 90 }),
      targetCompetitionIds: [39, 140],
    });

    const { plan } = await scheduler.executeSingleCycle();

    expect(plan.action).toBe('DISCOVER_SCHEDULE');
    expect(capturedFilter).toBeDefined();
    expect(capturedFilter.cycleId).toMatch(/^cycle_\d+_[a-z0-9]+$/);

    const recordedCycle = await cyclesRepo.getLatestCycle();
    expect(recordedCycle).toBeDefined();
    expect(recordedCycle?.id).toBe(capturedFilter.cycleId);
    expect(recordedCycle?.action).toBe('DISCOVER_SCHEDULE');
    expect(recordedCycle?.outcome).toBe('NO_OP'); // Provider action completed successfully but produced no domain/entity mutation
    expect(recordedCycle?.completedAt).not.toBeNull();
  });
});
