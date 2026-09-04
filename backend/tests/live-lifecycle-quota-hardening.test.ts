import { describe, it, expect, beforeEach, afterAll } from 'vitest';
import { eq, inArray } from 'drizzle-orm';
import { db, schema, pool } from '../src/db/index.js';
import { SyncService } from '../src/acquisition/sync.service.js';
import { AcquisitionPolicyEngine, FixturesSummary, BudgetContext } from '../src/acquisition/acquisition-policy.js';
import { AcquisitionScheduler } from '../src/acquisition/acquisition-scheduler.js';
import { FixturesRepository } from '../src/repositories/fixtures.repository.js';
import { FootballProvider, ProviderFixture, ProviderEvent } from '../src/providers/football-provider.js';
import { RequestGateway } from '../src/gateway/request-gateway.js';

describe('Milestone 16.5A — Live-State Lifecycle & Quota Hardening', () => {
  const TEST_COMP_ID = 'comp_m16_5';
  const TEST_SEASON_ID = 'season_m16_5';
  const TEST_HOME_ID = 'team_m16_5_home';
  const TEST_AWAY_ID = 'team_m16_5_away';
  const FIXTURE_RECENT = 'fix_m16_5_recent';
  const FIXTURE_OLD = 'fix_m16_5_old';
  const FIXTURE_ANCIENT = 'fix_m16_5_ancient';

  const createFixtureDto = (
    id: string,
    state: string,
    kickoffTime: string,
    elapsedMinute: number = 0,
    homeScore: number = 0,
    awayScore: number = 0,
    events: ProviderEvent[] = []
  ): ProviderFixture => ({
    id,
    competitionId: TEST_COMP_ID,
    seasonId: TEST_SEASON_ID,
    homeTeamId: TEST_HOME_ID,
    awayTeamId: TEST_AWAY_ID,
    competition: {
      id: TEST_COMP_ID,
      name: 'Test League M16.5',
      code: 'TL',
      country: 'England',
      type: 'League',
    },
    season: {
      id: TEST_SEASON_ID,
      competitionId: TEST_COMP_ID,
      year: 2026,
      displayYear: '2025/2026',
      startDate: '2025-08-01',
      endDate: '2026-05-31',
      isCurrent: true,
    },
    homeTeam: {
      id: TEST_HOME_ID,
      name: 'Team Home',
      shortName: 'HOM',
      code: 'HOM',
      country: 'England',
    },
    awayTeam: {
      id: TEST_AWAY_ID,
      name: 'Team Away',
      shortName: 'AWY',
      code: 'AWY',
      country: 'England',
    },
    kickoffTime,
    state,
    elapsedMinute,
    extraMinute: 0,
    score: { home: homeScore, away: awayScore },
    events,
  });

  const createMockProvider = (
    liveFixtures: ProviderFixture[],
    fixtureDetailsMap: Record<string, any> = {}
  ): FootballProvider => ({
    providerName: 'mock_m16_5_provider',
    getCompetitions: async () => [],
    getFixtures: async (filter) => {
      if (filter?.liveOnly) {
        return liveFixtures;
      }
      return liveFixtures;
    },
    getFixtureDetails: async (fixtureId) => fixtureDetailsMap[fixtureId] ?? null,
    getFixtureEvents: async () => [],
    getStandings: async () => [],
  });

  beforeEach(async () => {
    // Clean up test records
    await db.delete(schema.events).where(inArray(schema.events.fixtureId, [FIXTURE_RECENT, FIXTURE_OLD, FIXTURE_ANCIENT]));
    await db.delete(schema.fixtures).where(inArray(schema.fixtures.id, [FIXTURE_RECENT, FIXTURE_OLD, FIXTURE_ANCIENT]));
    await db.delete(schema.teams).where(inArray(schema.teams.id, [TEST_HOME_ID, TEST_AWAY_ID]));
    await db.delete(schema.competitionSeasons).where(eq(schema.competitionSeasons.id, TEST_SEASON_ID));
    await db.delete(schema.competitions).where(eq(schema.competitions.id, TEST_COMP_ID));

    // Seed test competition & teams
    await db.insert(schema.competitions).values({
      id: TEST_COMP_ID,
      name: 'Test League M16.5',
      code: 'TL',
      country: 'England',
      type: 'League',
    });
    await db.insert(schema.competitionSeasons).values({
      id: TEST_SEASON_ID,
      competitionId: TEST_COMP_ID,
      year: 2026,
      displayYear: '2025/2026',
      startDate: new Date('2025-08-01T00:00:00Z'),
      endDate: new Date('2026-05-31T23:59:59Z'),
      isCurrent: true,
    });
    await db.insert(schema.teams).values([
      { id: TEST_HOME_ID, name: 'Team Home', shortName: 'HOM', code: 'HOM', country: 'England' },
      { id: TEST_AWAY_ID, name: 'Team Away', shortName: 'AWY', code: 'AWY', country: 'England' },
    ]);
  });

  afterAll(async () => {
    await db.delete(schema.events).where(inArray(schema.events.fixtureId, [FIXTURE_RECENT, FIXTURE_OLD, FIXTURE_ANCIENT]));
    await db.delete(schema.fixtures).where(inArray(schema.fixtures.id, [FIXTURE_RECENT, FIXTURE_OLD, FIXTURE_ANCIENT]));
    await db.delete(schema.teams).where(inArray(schema.teams.id, [TEST_HOME_ID, TEST_AWAY_ID]));
    await db.delete(schema.competitionSeasons).where(eq(schema.competitionSeasons.id, TEST_SEASON_ID));
    await db.delete(schema.competitions).where(eq(schema.competitions.id, TEST_COMP_ID));
    await pool.end();
  });

  // ============================================================
  // Requirements 1 & 2: Maximum Live Age Classification
  // ============================================================
  describe('Requirements 1 & 2: Live State Expiration & Max Match Age', () => {
    const fixedNow = new Date('2026-03-01T15:00:00Z');

    it('1. LIVE fixture within valid match age (< 3.5h) remains LIVE_WINDOW', () => {
      // Kickoff 60m ago
      const fixture60m = {
        state: 'LIVE',
        kickoffTime: new Date('2026-03-01T14:00:00Z'),
      };
      expect(AcquisitionPolicyEngine.classifyFixtureState(fixture60m, fixedNow)).toBe('LIVE_WINDOW');

      // Kickoff 120m ago (still within 210m max age for extra time / stoppage)
      const fixture120m = {
        state: 'LIVE',
        kickoffTime: new Date('2026-03-01T13:00:00Z'),
      };
      expect(AcquisitionPolicyEngine.classifyFixtureState(fixture120m, fixedNow)).toBe('LIVE_WINDOW');
    });

    it('2. LIVE fixture beyond maximum live age (> 3.5h) becomes stale/finalizable, NEVER LIVE_WINDOW', () => {
      // Kickoff 4 hours ago (240 min > 210 min max live duration)
      const expiredFixture = {
        state: 'LIVE',
        kickoffTime: new Date('2026-03-01T11:00:00Z'),
      };
      const classification = AcquisitionPolicyEngine.classifyFixtureState(expiredFixture, fixedNow);
      expect(classification).not.toBe('LIVE_WINDOW');
      expect(['FINAL', 'RECENTLY_FINISHED']).toContain(classification);
    });

    it('1b. FixturesRepository active-LIVE window excludes fixtures beyond MAX_LIVE_MATCH_AGE_MS from liveCount', async () => {
      const now = new Date('2026-03-01T16:00:00Z');
      const repo = new FixturesRepository();

      // Capture baseline count in case previous test files left fixtures in test DB
      const baseline = await repo.getActiveSummary(now);
      const baselineLive = baseline.liveCount;

      // Ancient live fixture: kickoff was 4 hours ago (exceeds MAX_LIVE_MATCH_AGE_MS 3.5h)
      await db.insert(schema.fixtures).values({
        id: FIXTURE_ANCIENT,
        competitionId: TEST_COMP_ID,
        seasonId: TEST_SEASON_ID,
        homeTeamId: TEST_HOME_ID,
        awayTeamId: TEST_AWAY_ID,
        kickoffTime: new Date(now.getTime() - 4 * 60 * 60 * 1000), // 4h ago
        state: 'LIVE',
        homeScore: 1,
        awayScore: 0,
      });

      // Valid current live fixture: kickoff was 45 minutes ago (< 3.5h)
      await db.insert(schema.fixtures).values({
        id: FIXTURE_RECENT,
        competitionId: TEST_COMP_ID,
        seasonId: TEST_SEASON_ID,
        homeTeamId: TEST_HOME_ID,
        awayTeamId: TEST_AWAY_ID,
        kickoffTime: new Date(now.getTime() - 45 * 60 * 1000), // 45m ago
        state: 'LIVE',
        homeScore: 0,
        awayScore: 0,
      });

      const summary = await repo.getActiveSummary(now);

      // FIXTURE_ANCIENT is excluded by the candidate window (adds 0)
      // FIXTURE_RECENT is included within 3.5h (adds 1)
      expect(summary.liveCount).toBe(baselineLive + 1);
    });
  });

  // ============================================================
  // Requirements 3, 4 & 5: Vanished-Live Reconciliation in SyncService
  // ============================================================
  describe('Requirements 3, 4 & 5: Live Polling & Vanished Reconciliation', () => {
    it('3. Provider live response contains the fixture -> normal live update', async () => {
      const kickoff = new Date(Date.now() - 30 * 60 * 1000).toISOString(); // 30m ago
      // Seed initially as 0-0
      await db.insert(schema.fixtures).values({
        id: FIXTURE_RECENT,
        competitionId: TEST_COMP_ID,
        seasonId: TEST_SEASON_ID,
        homeTeamId: TEST_HOME_ID,
        awayTeamId: TEST_AWAY_ID,
        kickoffTime: new Date(kickoff),
        state: 'LIVE',
        elapsedMinute: 20,
        homeScore: 0,
        awayScore: 0,
      });

      // Provider returns 1-0 at 30th minute
      const provider = createMockProvider([
        createFixtureDto(FIXTURE_RECENT, 'LIVE', kickoff, 30, 1, 0),
      ]);
      const syncService = new SyncService(provider);
      const summary = await syncService.syncLive();

      expect(summary.fixturesUpdated).toBeGreaterThanOrEqual(1);

      const updated = await db.query.fixtures.findFirst({
        where: eq(schema.fixtures.id, FIXTURE_RECENT),
      });
      expect(updated?.state).toBe('LIVE');
      expect(updated?.homeScore).toBe(1);
      expect(updated?.elapsedMinute).toBe(30);
    });

    it('4. Provider live response omits a recently started live fixture -> does NOT immediately mark FINISHED', async () => {
      // Kickoff was only 25 minutes ago (< 105m safe elapsed threshold)
      const kickoff = new Date(Date.now() - 25 * 60 * 1000).toISOString();
      await db.insert(schema.fixtures).values({
        id: FIXTURE_RECENT,
        competitionId: TEST_COMP_ID,
        seasonId: TEST_SEASON_ID,
        homeTeamId: TEST_HOME_ID,
        awayTeamId: TEST_AWAY_ID,
        kickoffTime: new Date(kickoff),
        state: 'LIVE',
        elapsedMinute: 25,
        homeScore: 0,
        awayScore: 0,
      });

      // Provider returns empty live response (e.g. temporary omission or network glitch)
      const provider = createMockProvider([]);
      const syncService = new SyncService(provider);
      await syncService.syncLive();

      const fixtureInDb = await db.query.fixtures.findFirst({
        where: eq(schema.fixtures.id, FIXTURE_RECENT),
      });

      // Must remain LIVE because elapsed time < 105 minutes
      expect(fixtureInDb?.state).toBe('LIVE');
    });

    it('5a. Provider live response omits an old LIVE fixture (>= 105m) and details confirm FINISHED -> transitions to FINISHED with confirmed details', async () => {
      const kickoff = new Date(Date.now() - 115 * 60 * 1000).toISOString();
      await db.insert(schema.fixtures).values({
        id: FIXTURE_OLD,
        competitionId: TEST_COMP_ID,
        seasonId: TEST_SEASON_ID,
        homeTeamId: TEST_HOME_ID,
        awayTeamId: TEST_AWAY_ID,
        kickoffTime: new Date(kickoff),
        state: 'LIVE',
        elapsedMinute: 90,
        homeScore: 1,
        awayScore: 1,
      });

      // Provider live response is empty, but detail lookup returns confirmed FINISHED with 2-1 final score
      const confirmedDetail = createFixtureDto(FIXTURE_OLD, 'FINISHED', kickoff, 90, 2, 1);
      const provider = createMockProvider([], { [FIXTURE_OLD]: confirmedDetail });
      const syncService = new SyncService(provider);
      const summary = await syncService.syncLive();

      expect(summary.fixturesUpdated).toBeGreaterThanOrEqual(1);

      const fixtureInDb = await db.query.fixtures.findFirst({
        where: eq(schema.fixtures.id, FIXTURE_OLD),
      });

      expect(fixtureInDb?.state).toBe('FINISHED');
      expect(fixtureInDb?.homeScore).toBe(2);
      expect(fixtureInDb?.awayScore).toBe(1);
    });

    it('5b. Provider live response omits a match and details confirm POSTPONED -> transitions to POSTPONED (not blindly FINISHED)', async () => {
      const kickoff = new Date(Date.now() - 115 * 60 * 1000).toISOString();
      await db.insert(schema.fixtures).values({
        id: FIXTURE_OLD,
        competitionId: TEST_COMP_ID,
        seasonId: TEST_SEASON_ID,
        homeTeamId: TEST_HOME_ID,
        awayTeamId: TEST_AWAY_ID,
        kickoffTime: new Date(kickoff),
        state: 'LIVE',
        elapsedMinute: 15,
        homeScore: 0,
        awayScore: 0,
      });

      // Provider detail lookup confirms match was POSTPONED due to weather/lighting
      const postponedDetail = createFixtureDto(FIXTURE_OLD, 'POSTPONED', kickoff, 15, 0, 0);
      const provider = createMockProvider([], { [FIXTURE_OLD]: postponedDetail });
      const syncService = new SyncService(provider);
      await syncService.syncLive();

      const fixtureInDb = await db.query.fixtures.findFirst({
        where: eq(schema.fixtures.id, FIXTURE_OLD),
      });

      expect(fixtureInDb?.state).toBe('POSTPONED');
    });

    it('5c. Provider live response omits a match (110m) and details are unavailable -> remains LIVE to protect delayed/omitted match', async () => {
      const kickoff = new Date(Date.now() - 110 * 60 * 1000).toISOString(); // 110m ago (< 210m max age)
      await db.insert(schema.fixtures).values({
        id: FIXTURE_OLD,
        competitionId: TEST_COMP_ID,
        seasonId: TEST_SEASON_ID,
        homeTeamId: TEST_HOME_ID,
        awayTeamId: TEST_AWAY_ID,
        kickoffTime: new Date(kickoff),
        state: 'LIVE',
        elapsedMinute: 90,
        homeScore: 1,
        awayScore: 1,
      });

      // Details return null (e.g. temporary provider omission or ongoing extra time)
      const provider = createMockProvider([], { [FIXTURE_OLD]: null });
      const syncService = new SyncService(provider);
      await syncService.syncLive();

      const fixtureInDb = await db.query.fixtures.findFirst({
        where: eq(schema.fixtures.id, FIXTURE_OLD),
      });

      // Protected from premature finalization: remains LIVE because elapsed < 210m
      expect(fixtureInDb?.state).toBe('LIVE');
      expect(fixtureInDb?.lastPolledAt).not.toBeNull();
    });

    it('5d. Provider live response omits an expired LIVE fixture (> 3.5h) with unconfirmed details -> hard max age cutoff transitions to FINISHED', async () => {
      const kickoff = new Date(Date.now() - 220 * 60 * 1000).toISOString(); // 220m ago (> 210m max age)
      await db.insert(schema.fixtures).values({
        id: FIXTURE_OLD,
        competitionId: TEST_COMP_ID,
        seasonId: TEST_SEASON_ID,
        homeTeamId: TEST_HOME_ID,
        awayTeamId: TEST_AWAY_ID,
        kickoffTime: new Date(kickoff),
        state: 'LIVE',
        elapsedMinute: 90,
        homeScore: 3,
        awayScore: 2,
      });

      // Details return null, but match is past 3.5h
      const provider = createMockProvider([], { [FIXTURE_OLD]: null });
      const syncService = new SyncService(provider);
      await syncService.syncLive();

      const fixtureInDb = await db.query.fixtures.findFirst({
        where: eq(schema.fixtures.id, FIXTURE_OLD),
      });

      // Hard cutoff reached: must not remain LIVE
      expect(fixtureInDb?.state).toBe('FINISHED');
      expect(fixtureInDb?.homeScore).toBe(3);
    });
  });

  // ============================================================
  // Requirement 6: Bounded Finalization
  // ============================================================
  describe('Requirement 6: Bounded Finalization', () => {
    it('6. Finalization failure or missing details is bounded and does not create infinite loop', async () => {
      const now = new Date();
      await db.insert(schema.fixtures).values({
        id: FIXTURE_OLD,
        competitionId: TEST_COMP_ID,
        seasonId: TEST_SEASON_ID,
        homeTeamId: TEST_HOME_ID,
        awayTeamId: TEST_AWAY_ID,
        kickoffTime: new Date(now.getTime() - 120 * 60 * 1000),
        state: 'FINISHED',
        homeScore: 1,
        awayScore: 0,
        updatedAt: now,
      });

      // Provider returns null for fixture details (e.g. 404 / unrecognized fixture)
      const provider = createMockProvider([], { [FIXTURE_OLD]: null });
      const syncService = new SyncService(provider);

      // Finalize should touch lastPolledAt without throwing
      await syncService.finalizeRecentlyFinished([TEST_COMP_ID]);

      const after = await db.query.fixtures.findFirst({
        where: eq(schema.fixtures.id, FIXTURE_OLD),
      });

      expect(after?.state).toBe('FINISHED');
      expect(after?.lastPolledAt).not.toBeNull();
    });
  });

  // ============================================================
  // Requirements 7 & 8: Starvation Prevention
  // ============================================================
  describe('Requirements 7 & 8: Scheduler Starvation Prevention', () => {
    const abundantBudget: BudgetContext = { consumedDaily: 20, remainingDaily: 80 };

    it('7. One stale LIVE fixture cannot starve DISCOVER_SCHEDULE', () => {
      const summary: FixturesSummary = {
        liveCount: 1, // Stale live match present
        recentlyFinishedCount: 0,
        stalePrematchCount: 0,
        scheduleKnownForToday: false, // Daily schedule unknown
      };

      const plan = AcquisitionPolicyEngine.evaluateSyncPlan(abundantBudget, summary);
      // DISCOVER_SCHEDULE must execute first so the scheduler learns today's match calendar
      expect(plan.action).toBe('DISCOVER_SCHEDULE');
    });

    it('8. One stale LIVE fixture cannot starve FINALIZE once reconciled', () => {
      // Once the stale live match is reconciled to FINISHED, liveCount is 0 and recentlyFinishedCount is 1
      const summary: FixturesSummary = {
        liveCount: 0,
        recentlyFinishedCount: 1,
        stalePrematchCount: 0,
        scheduleKnownForToday: true,
      };

      const plan = AcquisitionPolicyEngine.evaluateSyncPlan(abundantBudget, summary);
      expect(plan.action).toBe('FINALIZE');
    });
  });

  // ============================================================
  // Requirements 9, 10 & 11: Quota Safety & Idle Behavior
  // ============================================================
  describe('Requirements 9, 10 & 11: Quota Safety and Exiting POLL_LIVE', () => {
    it('9. Scheduler exits POLL_LIVE when no valid live fixtures remain', () => {
      const summaryNoLive: FixturesSummary = {
        liveCount: 0,
        recentlyFinishedCount: 0,
        stalePrematchCount: 0,
        scheduleKnownForToday: true,
      };

      const budget: BudgetContext = { consumedDaily: 20, remainingDaily: 80 };
      const plan = AcquisitionPolicyEngine.evaluateSyncPlan(budget, summaryNoLive);
      expect(plan.action).toBe('IDLE');
      expect(plan.recommendedNextPollMs).toBeNull();
    });

    it('10. Daily budget protections remain intact (reserve <= 10 stops autonomous polling)', () => {
      const summaryWithLive: FixturesSummary = {
        liveCount: 2,
        recentlyFinishedCount: 0,
        stalePrematchCount: 0,
        scheduleKnownForToday: true,
      };

      // At critical reserve ceiling (10)
      const reserveBudget: BudgetContext = { consumedDaily: 90, remainingDaily: 10 };
      const plan = AcquisitionPolicyEngine.evaluateSyncPlan(reserveBudget, summaryWithLive);
      expect(plan.action).toBe('IDLE');
      expect(plan.reason).toContain('Critical reserve protected');
    });

    it('11. Ancient fixture (> 3.5h) in database does not increment liveCount and triggers no live calls', () => {
      const ancientKickoff = new Date(Date.now() - 48 * 60 * 60 * 1000); // 2 days ago
      const classification = AcquisitionPolicyEngine.classifyFixtureState({
        state: 'LIVE',
        kickoffTime: ancientKickoff,
      });

      expect(classification).toBe('FINAL');
    });

    it('12. Existing behavior for genuine live matches remains intact with 180s fast cadence', () => {
      const budget: BudgetContext = { consumedDaily: 10, remainingDaily: 90 };
      const interval = AcquisitionPolicyEngine.calculateLivePollingInterval(budget.remainingDaily);
      expect(interval).toBe(180_000); // 180s (3m)
    });

    it('13. reconcileExpiredLiveFixtures transitions ancient LIVE fixtures to FINISHED locally without external calls', async () => {
      const ancientKickoff = new Date(Date.now() - 5 * 60 * 60 * 1000); // 5 hours ago
      await db.insert(schema.fixtures).values({
        id: FIXTURE_ANCIENT,
        competitionId: TEST_COMP_ID,
        seasonId: TEST_SEASON_ID,
        homeTeamId: TEST_HOME_ID,
        awayTeamId: TEST_AWAY_ID,
        kickoffTime: ancientKickoff,
        state: 'LIVE',
      });

      const repo = new FixturesRepository();
      const sweptCount = await repo.reconcileExpiredLiveFixtures();
      expect(sweptCount).toBeGreaterThanOrEqual(1);

      const check = await db.query.fixtures.findFirst({
        where: eq(schema.fixtures.id, FIXTURE_ANCIENT),
      });
      expect(check?.state).toBe('FINISHED');
    });

    it('14. getActiveSummary sweeps stale LIVE rows so liveCount is 0 and idle is evaluated', async () => {
      const ancientKickoff = new Date(Date.now() - 10 * 60 * 60 * 1000); // 10 hours ago
      await db.insert(schema.fixtures).values({
        id: FIXTURE_OLD,
        competitionId: TEST_COMP_ID,
        seasonId: TEST_SEASON_ID,
        homeTeamId: TEST_HOME_ID,
        awayTeamId: TEST_AWAY_ID,
        kickoffTime: ancientKickoff,
        state: 'LIVE',
      });

      const repo = new FixturesRepository();
      const summary = await repo.getActiveSummary();
      expect(summary.liveCount).toBe(0);

      // Verify row in DB is now FINISHED
      const check = await db.query.fixtures.findFirst({
        where: eq(schema.fixtures.id, FIXTURE_OLD),
      });
      expect(check?.state).toBe('FINISHED');
    });

    it('15. Genuinely live match (within 3.5h) is not swept and increments liveCount', async () => {
      const recentKickoff = new Date(Date.now() - 45 * 60 * 1000); // 45 minutes ago
      await db.insert(schema.fixtures).values({
        id: FIXTURE_RECENT,
        competitionId: TEST_COMP_ID,
        seasonId: TEST_SEASON_ID,
        homeTeamId: TEST_HOME_ID,
        awayTeamId: TEST_AWAY_ID,
        kickoffTime: recentKickoff,
        state: 'LIVE',
      });

      const repo = new FixturesRepository();
      const summary = await repo.getActiveSummary();
      expect(summary.liveCount).toBe(1);

      const check = await db.query.fixtures.findFirst({
        where: eq(schema.fixtures.id, FIXTURE_RECENT),
      });
      expect(check?.state).toBe('LIVE');
    });

    it('16. RequestGateway ignores lagging pre-reset headers across midnight rollover', async () => {
      const gateway = new RequestGateway(pool);
      const testProvider = 'test_rollover_lag_provider';

      // Clean up test provider records
      await pool.query(`DELETE FROM api_request_ledger WHERE provider = $1`, [testProvider]);
      await pool.query(`DELETE FROM provider_daily_budget WHERE provider = $1`, [testProvider]);

      // Seed yesterday's ledger with 77 requests
      for (let i = 0; i < 5; i++) {
        await pool.query(
          `INSERT INTO api_request_ledger (
             id, provider, endpoint, operation, priority, http_status, success, cost_units, response_time_ms, requested_at
           ) VALUES ($1, $2, '/fixtures', 'test', 'NORMAL', 200, true, 15, 120, (NOW() AT TIME ZONE 'UTC')::date - INTERVAL '2 hours')`,
          [`ledg_yesterday_${i}`, testProvider]
        );
      }
      // Plus 2 single requests = 77 total cost units yesterday
      await pool.query(
        `INSERT INTO api_request_ledger (
           id, provider, endpoint, operation, priority, http_status, success, cost_units, response_time_ms, requested_at
         ) VALUES ('ledg_yesterday_5', $1, '/fixtures', 'test', 'NORMAL', 200, true, 2, 120, (NOW() AT TIME ZONE 'UTC')::date - INTERVAL '2 hours')`,
        [testProvider]
      );

      // Seed today's ledger with 6 requests
      for (let i = 0; i < 6; i++) {
        await pool.query(
          `INSERT INTO api_request_ledger (
             id, provider, endpoint, operation, priority, http_status, success, cost_units, response_time_ms, requested_at
           ) VALUES ($1, $2, '/fixtures', 'test', 'NORMAL', 200, true, 1, 120, NOW())`,
          [`ledg_today_${i}`, testProvider]
        );
      }

      // Initialize today's budget with 6 consumed, 94 remaining
      await pool.query(
        `INSERT INTO provider_daily_budget (
           id, provider, budget_date, allocated_calls, consumed_calls, remaining_calls, is_exhausted, updated_at
         ) VALUES ($1, $2, (NOW() AT TIME ZONE 'UTC')::date, 100, 6, 94, false, NOW())`,
        [`budget_${testProvider}_today`, testProvider]
      );

      // Provider returns 17 remaining (reflecting 77 yesterday + 6 today = 83 consumed, lag before reset)
      await (gateway as any).reconcileQuota(testProvider, 17);

      // Budget should IGNORE the lagging header and remain at 6 consumed, 94 remaining
      const check = await pool.query(
        `SELECT consumed_calls, remaining_calls FROM provider_daily_budget WHERE provider = $1 AND budget_date = (NOW() AT TIME ZONE 'UTC')::date`,
        [testProvider]
      );
      expect(check.rows[0].consumed_calls).toBe(6);
      expect(check.rows[0].remaining_calls).toBe(94);

      // Cleanup
      await pool.query(`DELETE FROM api_request_ledger WHERE provider = $1`, [testProvider]);
      await pool.query(`DELETE FROM provider_daily_budget WHERE provider = $1`, [testProvider]);
    });

    it('17. RequestGateway self-heals budget row polluted by rollover lag in diagnostics and reservation', async () => {
      const gateway = new RequestGateway(pool);
      const testProvider = 'test_self_heal_provider';

      await pool.query(`DELETE FROM api_request_ledger WHERE provider = $1`, [testProvider]);
      await pool.query(`DELETE FROM provider_daily_budget WHERE provider = $1`, [testProvider]);

      // Seed yesterday's ledger with 77 requests
      await pool.query(
        `INSERT INTO api_request_ledger (
           id, provider, endpoint, operation, priority, http_status, success, cost_units, response_time_ms, requested_at
         ) VALUES ('ledg_heal_yest', $1, '/fixtures', 'test', 'NORMAL', 200, true, 77, 120, (NOW() AT TIME ZONE 'UTC')::date - INTERVAL '2 hours')`,
        [testProvider]
      );

      // Seed today's ledger with 6 requests
      for (let i = 0; i < 6; i++) {
        await pool.query(
          `INSERT INTO api_request_ledger (
             id, provider, endpoint, operation, priority, http_status, success, cost_units, response_time_ms, requested_at
           ) VALUES ($1, $2, '/fixtures', 'test', 'NORMAL', 200, true, 1, 120, NOW())`,
          [`ledg_heal_today_${i}`, testProvider]
        );
      }

      // Seed polluted budget: 83 consumed, 17 remaining
      await pool.query(
        `INSERT INTO provider_daily_budget (
           id, provider, budget_date, allocated_calls, consumed_calls, remaining_calls, is_exhausted, updated_at
         ) VALUES ($1, $2, (NOW() AT TIME ZONE 'UTC')::date, 100, 83, 17, false, NOW())`,
        [`budget_${testProvider}_today`, testProvider]
      );

      // getDiagnostics should self-heal and return 6 consumed, 94 remaining
      const diag = await gateway.getDiagnostics(testProvider);
      expect(diag.consumedDaily).toBe(6);
      expect(diag.remainingDaily).toBe(94);

      // Verify the row in PostgreSQL is also healed
      const check = await pool.query(
        `SELECT consumed_calls, remaining_calls FROM provider_daily_budget WHERE provider = $1 AND budget_date = (NOW() AT TIME ZONE 'UTC')::date`,
        [testProvider]
      );
      expect(check.rows[0].consumed_calls).toBe(6);
      expect(check.rows[0].remaining_calls).toBe(94);

      // Cleanup
      await pool.query(`DELETE FROM api_request_ledger WHERE provider = $1`, [testProvider]);
      await pool.query(`DELETE FROM provider_daily_budget WHERE provider = $1`, [testProvider]);
    });

    it('18. Exact production sequence: 77 requests yesterday -> Batch 1 headers 22..17 -> Batch 2 headers 93..88 -> ledger authoritative 12 consumed, 88 remaining', async () => {
      const gateway = new RequestGateway(pool);
      const testProvider = 'test_prod_sequence_provider';

      await pool.query(`DELETE FROM api_request_ledger WHERE provider = $1`, [testProvider]);
      await pool.query(`DELETE FROM provider_daily_budget WHERE provider = $1`, [testProvider]);

      // Seed yesterday's ledger with 77 requests
      for (let i = 0; i < 7; i++) {
        await pool.query(
          `INSERT INTO api_request_ledger (
             id, provider, endpoint, operation, priority, http_status, success, cost_units, response_time_ms, requested_at
           ) VALUES ($1, $2, '/fixtures', 'DISCOVER_SCHEDULE', 'NORMAL', 200, true, 11, 100, (NOW() AT TIME ZONE 'UTC')::date - INTERVAL '2 hours')`,
          [`ledg_yest_${i}`, testProvider]
        );
      }

      // New UTC day starts: today ledger = 0
      // First 6 requests: provider remaining headers = 22, 21, 20, 19, 18, 17
      const batch1Headers = [22, 21, 20, 19, 18, 17];
      for (let i = 0; i < batch1Headers.length; i++) {
        const headerRemaining = batch1Headers[i];
        const res = await gateway.execute(
          { provider: testProvider, endpoint: `/fixtures?league=${i + 1}`, operation: 'DISCOVER_SCHEDULE' },
          async () => ({
            status: 200,
            headers: { 'x-ratelimit-requests-remaining': String(headerRemaining) },
            data: { response: [] },
          })
        );
        expect(res.success).toBe(true);
      }

      // After first batch:
      // today ledger = 6
      const ledgerRes1 = await pool.query(
        `SELECT COUNT(*)::int as cnt FROM api_request_ledger WHERE provider = $1 AND requested_at >= (NOW() AT TIME ZONE 'UTC')::date`,
        [testProvider]
      );
      expect(ledgerRes1.rows[0].cnt).toBe(6);

      // budget must be: consumed = 6, remaining = 94
      const budget1 = await pool.query(
        `SELECT consumed_calls, remaining_calls FROM provider_daily_budget WHERE provider = $1 AND budget_date = (NOW() AT TIME ZONE 'UTC')::date`,
        [testProvider]
      );
      expect(budget1.rows[0].consumed_calls).toBe(6);
      expect(budget1.rows[0].remaining_calls).toBe(94);

      // In production, Batch 2 occurred ~57 minutes after Batch 1 (00:00 UTC vs 00:57 UTC).
      // Age Batch 1 ledger timestamps to 57 minutes ago so the 8/min rate limiter window is clear.
      await pool.query(
        `UPDATE api_request_ledger
         SET requested_at = NOW() - INTERVAL '57 minutes'
         WHERE provider = $1 AND requested_at >= (NOW() AT TIME ZONE 'UTC')::date`,
        [testProvider]
      );

      // Second 6 requests: provider remaining headers = 93, 92, 91, 90, 89, 88
      const batch2Headers = [93, 92, 91, 90, 89, 88];
      for (let i = 0; i < batch2Headers.length; i++) {
        const headerRemaining = batch2Headers[i];
        const res = await gateway.execute(
          { provider: testProvider, endpoint: `/fixtures?league=${i + 1}`, operation: 'DISCOVER_SCHEDULE' },
          async () => ({
            status: 200,
            headers: { 'x-ratelimit-requests-remaining': String(headerRemaining) },
            data: { response: [] },
          })
        );
        expect(res.success).toBe(true);
      }

      // After second batch:
      // today ledger = 12
      const ledgerRes2 = await pool.query(
        `SELECT COUNT(*)::int as cnt FROM api_request_ledger WHERE provider = $1 AND requested_at >= (NOW() AT TIME ZONE 'UTC')::date`,
        [testProvider]
      );
      expect(ledgerRes2.rows[0].cnt).toBe(12);

      // budget must be: consumed = 12, remaining = 88
      const budget2 = await pool.query(
        `SELECT consumed_calls, remaining_calls FROM provider_daily_budget WHERE provider = $1 AND budget_date = (NOW() AT TIME ZONE 'UTC')::date`,
        [testProvider]
      );
      expect(budget2.rows[0].consumed_calls).toBe(12);
      expect(budget2.rows[0].remaining_calls).toBe(88);

      // Verify diagnostics reports the ledger-derived current-day usage
      const diag = await gateway.getDiagnostics(testProvider);
      expect(diag.consumedDaily).toBe(12);
      expect(diag.remainingDaily).toBe(88);

      // Verify reserveSlot() never allows consumed > today's ledger + current request
      const nextReservation = await gateway.reserveSlot(testProvider, '/fixtures', 'DISCOVER_SCHEDULE', 'NORMAL');
      expect(nextReservation.allowed).toBe(true);
      expect(nextReservation.consumedDaily).toBe(13);
      expect(nextReservation.remainingDaily).toBe(87);

      // Cleanup
      await pool.query(`DELETE FROM api_request_ledger WHERE provider = $1`, [testProvider]);
      await pool.query(`DELETE FROM provider_daily_budget WHERE provider = $1`, [testProvider]);
    });

    it('19. Part E Regression: UTC date = 2026-09-04 empty schedule discovery persists across cycles and backend restarts', async () => {
      await db.delete(schema.events);
      await db.delete(schema.fixtures);
      await pool.query(`DELETE FROM api_request_ledger WHERE endpoint LIKE '%2026-09-04%'`);

      const testDate = new Date('2026-09-04T12:00:00.000Z');
      const todayStr = '2026-09-04';

      let discoverCallCount = 0;
      const mockProvider: FootballProvider = {
        providerName: 'mock_discovery_provider',
        getCompetitions: async () => [],
        getFixtures: async (filter) => {
          const count = filter?.competitionIds?.length ?? 1;
          discoverCallCount += count;
          return []; // Empty schedule today: returns ZERO fixtures for all 6 competitions
        },
        getFixtureDetails: async () => null,
        getFixtureEvents: async () => [],
        getStandings: async () => [],
      };

      const syncService = new SyncService(mockProvider);
      const repo = new FixturesRepository();
      // Ensure clean initial state for today
      await repo.invalidateSchedule(todayStr);

      const scheduler = new AcquisitionScheduler({
        syncService,
        fixturesRepository: repo,
        providerName: 'mock_discovery_provider',
        targetCompetitionIds: [1, 2, 3, 4, 5, 6],
        nowFn: () => testDate,
        getBudgetContext: async () => ({
          allocatedDaily: 100,
          consumedDaily: 0,
          remainingDaily: 100,
          isExhausted: false,
        }),
      });

      // Cycle 1:
      // * DISCOVER_SCHEDULE
      // * six provider requests
      // * successful
      // * marks date discovered
      const cycle1 = await scheduler.executeSingleCycle();
      expect(cycle1.plan.action).toBe('DISCOVER_SCHEDULE');
      expect(discoverCallCount).toBe(6); // exactly 6 provider requests
      expect(repo.isScheduleDiscovered(todayStr)).toBe(true);

      // Cycle 2:
      // * must NOT DISCOVER_SCHEDULE
      // * zero provider calls
      const cycle2 = await scheduler.executeSingleCycle();
      expect(cycle2.plan.action).toBe('IDLE');
      expect(discoverCallCount).toBe(6); // zero new calls

      // Cycle 3:
      // * must NOT DISCOVER_SCHEDULE
      // * zero provider calls
      const cycle3 = await scheduler.executeSingleCycle();
      expect(cycle3.plan.action).toBe('IDLE');
      expect(discoverCallCount).toBe(6); // zero new calls

      // Simulated backend restart / new repository instance:
      // * date must still be known
      // * zero discovery calls
      const restartRepo = new FixturesRepository();
      const restartScheduler = new AcquisitionScheduler({
        syncService,
        fixturesRepository: restartRepo,
        providerName: 'mock_discovery_provider',
        targetCompetitionIds: [1, 2, 3, 4, 5, 6],
        nowFn: () => testDate,
        getBudgetContext: async () => ({
          allocatedDaily: 100,
          consumedDaily: 6,
          remainingDaily: 94,
          isExhausted: false,
        }),
      });

      const restartCycle = await restartScheduler.executeSingleCycle();
      expect(restartCycle.plan.action).toBe('IDLE');
      expect(discoverCallCount).toBe(6); // zero discovery calls on restart!
      expect(restartRepo.isScheduleDiscovered(todayStr)).toBe(true);

      // Explicit invalidation:
      // * discovery may run again
      // * six calls maximum
      await restartRepo.invalidateSchedule(todayStr);
      expect(restartRepo.isScheduleDiscovered(todayStr)).toBe(false);

      const invalidationCycle = await restartScheduler.executeSingleCycle();
      expect(invalidationCycle.plan.action).toBe('DISCOVER_SCHEDULE');
      expect(discoverCallCount).toBe(12); // exactly six calls on rediscovery (6 + 6 = 12)
      expect(restartRepo.isScheduleDiscovered(todayStr)).toBe(true);
    });

    it('20. Explicit UTC Boundary & Cairo UTC+3 Rollover: verifies strict UTC midnight boundaries and exact two-batch rollover sequence', async () => {
      const testProvider = 'api_football';
      const gateway = new RequestGateway(pool);

      // 1. Configure session timezone to Africa/Cairo (UTC+3)
      await pool.query("SET TIME ZONE 'Africa/Cairo'");

      // Clean test ledger and budget
      await pool.query(`DELETE FROM api_request_ledger WHERE provider = $1`, [testProvider]);
      await pool.query(`DELETE FROM provider_daily_budget WHERE provider = $1`, [testProvider]);

      // 2. Insert edge timestamp: 2026-09-03T23:59:59Z (Sept 3 in UTC, but 02:59:59 Sept 4 in Cairo!)
      await pool.query(
        `INSERT INTO api_request_ledger (
           id, provider, endpoint, operation, priority, success, http_status, response_time_ms, cost_units, requested_at
         ) VALUES ($1, $2, '/fixtures?date=2026-09-03', 'get_fixtures', 'NORMAL', true, 200, 50, 1, '2026-09-03 23:59:59+00')`,
        ['ledg_edge_sept3', testProvider]
      );

      // Seed 76 more yesterday entries so yesterday total = 77
      for (let i = 1; i <= 76; i++) {
        await pool.query(
          `INSERT INTO api_request_ledger (
             id, provider, endpoint, operation, priority, success, http_status, response_time_ms, cost_units, requested_at
           ) VALUES ($1, $2, '/fixtures?date=2026-09-03', 'get_fixtures', 'NORMAL', true, 200, 50, 1, '2026-09-03 20:00:00+00')`,
          [`ledg_yesterday_${i}`, testProvider]
        );
      }

      // 3. Insert first timestamp of today: 2026-09-04T00:00:01Z (Sept 4 in UTC)
      await pool.query(
        `INSERT INTO api_request_ledger (
           id, provider, endpoint, operation, priority, success, http_status, response_time_ms, cost_units, requested_at
         ) VALUES ($1, $2, '/fixtures?date=2026-09-04', 'get_fixtures', 'NORMAL', true, 200, 50, 1, '2026-09-04 00:00:01+00')`,
        ['ledg_edge_sept4', testProvider]
      );

      // Verify UTC boundary query in RequestGateway
      const { startOfTodayUtc, startOfTomorrowUtc, startOfYesterdayUtc } = RequestGateway.getUtcDayBoundaries(new Date('2026-09-04T00:30:00Z'));

      const yesterdayRes = await pool.query(
        `SELECT COUNT(*)::int as cnt FROM api_request_ledger WHERE provider = $1 AND requested_at >= $2 AND requested_at < $3`,
        [testProvider, startOfYesterdayUtc, startOfTodayUtc]
      );
      expect(yesterdayRes.rows[0].cnt).toBe(77); // 76 + 1 edge timestamp at 23:59:59Z

      const todayRes = await pool.query(
        `SELECT COUNT(*)::int as cnt FROM api_request_ledger WHERE provider = $1 AND requested_at >= $2 AND requested_at < $3`,
        [testProvider, startOfTodayUtc, startOfTomorrowUtc]
      );
      expect(todayRes.rows[0].cnt).toBe(1); // strictly 1 row for today so far

      // 4. Now execute the exact production scenario:
      // Clean ledger to test the two batches cleanly
      await pool.query(`DELETE FROM api_request_ledger WHERE provider = $1`, [testProvider]);
      await pool.query(`DELETE FROM provider_daily_budget WHERE provider = $1`, [testProvider]);

      // Seed exactly 77 rows for yesterday
      for (let i = 1; i <= 77; i++) {
        await pool.query(
          `INSERT INTO api_request_ledger (
             id, provider, endpoint, operation, priority, success, http_status, response_time_ms, cost_units, requested_at
           ) VALUES ($1, $2, '/fixtures?date=2026-09-03', 'get_fixtures', 'NORMAL', true, 200, 50, 1, '2026-09-03 21:00:00+00')`,
          [`ledg_prod_y_${i}`, testProvider]
        );
      }

      // Batch 1 on 2026-09-04: 6 calls with stale provider headers 22..17
      const staleHeaders = [22, 21, 20, 19, 18, 17];
      for (let i = 0; i < 6; i++) {
        const slot = await gateway.reserveSlot(testProvider, `/fixtures?date=2026-09-04&league=${i+1}`, 'get_fixtures', 'NORMAL');
        expect(slot.allowed).toBe(true);

        await gateway.finalizeLedgerAndReconcile(
          slot.ledgerId!,
          testProvider,
          200,
          100,
          true,
          staleHeaders[i], // stale remaining header from yesterday
          10
        );
      }

      // After Batch 1: today consumed must be 6, remaining must be 94 (stale headers 22..17 rejected!)
      const diagAfterBatch1 = await gateway.getDiagnostics(testProvider);
      expect(diagAfterBatch1.consumedDaily).toBe(6);
      expect(diagAfterBatch1.remainingDaily).toBe(94);

      // In production, Batch 2 occurred ~57 minutes after Batch 1 (00:00 UTC vs 00:57 UTC).
      // Age Batch 1 ledger timestamps to 57 minutes ago so the 8/min rate limiter window is clear.
      await pool.query(
        `UPDATE api_request_ledger
         SET requested_at = NOW() - INTERVAL '57 minutes'
         WHERE provider = $1 AND requested_at >= $2`,
        [testProvider, startOfTodayUtc]
      );

      // Batch 2 on 2026-09-04: 6 calls with correct provider headers 93..88
      const correctHeaders = [93, 92, 91, 90, 89, 88];
      for (let i = 0; i < 6; i++) {
        const slot = await gateway.reserveSlot(testProvider, `/fixtures?date=2026-09-04&league=${i+10}`, 'get_fixtures', 'NORMAL');
        expect(slot.allowed).toBe(true);

        await gateway.finalizeLedgerAndReconcile(
          slot.ledgerId!,
          testProvider,
          200,
          100,
          true,
          correctHeaders[i], // correct new-day remaining header
          10
        );
      }

      // After Batch 2: today consumed must be exactly 12, remaining must be exactly 88!
      const diagAfterBatch2 = await gateway.getDiagnostics(testProvider);
      expect(diagAfterBatch2.consumedDaily).toBe(12);
      expect(diagAfterBatch2.remainingDaily).toBe(88);

      // Cleanup
      await pool.query(`DELETE FROM api_request_ledger WHERE provider = $1`, [testProvider]);
      await pool.query(`DELETE FROM provider_daily_budget WHERE provider = $1`, [testProvider]);
      await pool.query("SET TIME ZONE 'UTC'");
    });

    it('21. Integrated Combined Regression: UTC+3, prior-day usage, empty schedule, stale headers, hourly wake, and stale LIVE cleanup', async () => {
      const testProvider = 'api_football';
      const gateway = new RequestGateway(pool);
      const testDate = new Date('2026-09-04T00:30:00.000Z');

      // 1. UTC+3 Environment
      await pool.query("SET TIME ZONE 'Africa/Cairo'");

      // Reset state
      await db.delete(schema.events);
      await db.delete(schema.fixtures);
      await pool.query(`DELETE FROM api_request_ledger WHERE provider = $1`, [testProvider]);
      await pool.query(`DELETE FROM provider_daily_budget WHERE provider = $1`, [testProvider]);

      const repo = new FixturesRepository();
      const todayStr = '2026-09-04';
      await repo.invalidateSchedule(todayStr);

      // 2. Previous-day provider activity (77 calls on Sept 3)
      for (let i = 1; i <= 77; i++) {
        await pool.query(
          `INSERT INTO api_request_ledger (
             id, provider, endpoint, operation, priority, success, http_status, response_time_ms, cost_units, requested_at
           ) VALUES ($1, $2, '/fixtures?date=2026-09-03', 'get_fixtures', 'NORMAL', true, 200, 50, 1, '2026-09-03 21:00:00+00')`,
          [`ledg_comb_prev_${i}`, testProvider]
        );
      }

      // 3. Stale LIVE fixture (kickoff 6 hours ago on Sept 3)
      await db.insert(schema.competitions).values({
        id: 'comp_comb_1',
        name: 'Combined Comp',
        code: 'CC1',
        country: 'England',
      }).onConflictDoNothing();

      await db.insert(schema.competitionSeasons).values({
        id: 'season_comb_1',
        competitionId: 'comp_comb_1',
        year: 2026,
        displayYear: '2026',
        startDate: new Date('2025-08-01'),
        endDate: new Date('2026-05-31'),
      }).onConflictDoNothing();

      await db.insert(schema.teams).values([
        { id: 'team_comb_h', name: 'Home Comb', shortName: 'HC' },
        { id: 'team_comb_a', name: 'Away Comb', shortName: 'AC' },
      ]).onConflictDoNothing();

      await db.insert(schema.fixtures).values({
        id: 'fix_ancient_live_comb',
        competitionId: 'comp_comb_1',
        seasonId: 'season_comb_1',
        homeTeamId: 'team_comb_h',
        awayTeamId: 'team_comb_a',
        kickoffTime: new Date('2026-09-03T18:30:00.000Z'),
        state: 'LIVE',
        elapsedMinute: 90,
      });

      // 4. Provider setup: returns 0 fixtures (empty day), routes through RequestGateway
      let actualProviderCalls = 0;
      const staleHeaders = [22, 21, 20, 19, 18, 17];
      const mockProvider: FootballProvider = {
        providerName: testProvider,
        getCompetitions: async () => [],
        getFixtures: async (filter) => {
          const comps = filter?.competitionIds ?? [1, 2, 3, 4, 5, 6];
          for (let i = 0; i < comps.length; i++) {
            actualProviderCalls++;
            const slot = await gateway.reserveSlot(testProvider, `/fixtures?date=2026-09-04&league=${comps[i]}`, 'get_fixtures', 'NORMAL');
            await gateway.finalizeLedgerAndReconcile(
              slot.ledgerId!,
              testProvider,
              200,
              50,
              true,
              staleHeaders[i % staleHeaders.length],
              10
            );
          }
          return []; // Empty schedule today
        },
        getFixtureDetails: async () => null,
        getFixtureEvents: async () => [],
        getStandings: async () => [],
      };

      const syncService = new SyncService(mockProvider);

      const scheduler = new AcquisitionScheduler({
        syncService,
        fixturesRepository: repo,
        providerName: testProvider,
        targetCompetitionIds: [1, 2, 3, 4, 5, 6],
        nowFn: () => testDate,
        getBudgetContext: async () => {
          const diag = await gateway.getDiagnostics(testProvider);
          return {
            allocatedDaily: diag.allocatedDaily,
            consumedDaily: diag.consumedDaily,
            remainingDaily: diag.remainingDaily,
            isExhausted: diag.isExhausted,
          };
        },
      });

      // Cycle 1: Schedule discovery runs (6 calls)
      const cycle1 = await scheduler.executeSingleCycle();
      expect(cycle1.plan.action).toBe('DISCOVER_SCHEDULE');
      expect(actualProviderCalls).toBe(6);

      // Invariant Check 1 & 2:
      // Today's ledger count = 6, provider budget consumed = 6, remaining = 94 (stale headers 22..17 ignored!)
      const diag1 = await gateway.getDiagnostics(testProvider);
      expect(diag1.consumedDaily).toBe(6);
      expect(diag1.remainingDaily).toBe(94);

      // Invariant Check 3:
      // Stale LIVE fixture was automatically swept to FINISHED without external call
      const ancientFix = await db.query.fixtures.findFirst({
        where: eq(schema.fixtures.id, 'fix_ancient_live_comb'),
      });
      expect(ancientFix?.state).toBe('FINISHED');

      // Cycle 2 (second hourly wake): must be IDLE, ZERO provider calls generated!
      const cycle2 = await scheduler.executeSingleCycle();
      expect(cycle2.plan.action).toBe('IDLE');
      expect(actualProviderCalls).toBe(6); // strictly zero additional provider calls!

      // Simulated process restart / cold instance
      const restartRepo = new FixturesRepository();
      const restartScheduler = new AcquisitionScheduler({
        syncService,
        fixturesRepository: restartRepo,
        providerName: testProvider,
        targetCompetitionIds: [1, 2, 3, 4, 5, 6],
        nowFn: () => testDate,
        getBudgetContext: async () => {
          const diag = await gateway.getDiagnostics(testProvider);
          return {
            allocatedDaily: diag.allocatedDaily,
            consumedDaily: diag.consumedDaily,
            remainingDaily: diag.remainingDaily,
            isExhausted: diag.isExhausted,
          };
        },
      });

      const restartCycle = await restartScheduler.executeSingleCycle();
      expect(restartCycle.plan.action).toBe('IDLE');
      expect(actualProviderCalls).toBe(6); // strictly zero calls on restart!

      // Cleanup
      await pool.query(`DELETE FROM api_request_ledger WHERE provider = $1`, [testProvider]);
      await pool.query(`DELETE FROM provider_daily_budget WHERE provider = $1`, [testProvider]);
      await db.delete(schema.events);
      await db.delete(schema.fixtures);
      await pool.query("SET TIME ZONE 'UTC'");
    });
  });
});
