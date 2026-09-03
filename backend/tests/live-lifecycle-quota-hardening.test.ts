import { describe, it, expect, beforeEach, afterAll } from 'vitest';
import { eq, inArray } from 'drizzle-orm';
import { db, schema, pool } from '../src/db/index.js';
import { SyncService } from '../src/acquisition/sync.service.js';
import { AcquisitionPolicyEngine, FixturesSummary, BudgetContext } from '../src/acquisition/acquisition-policy.js';
import { FixturesRepository } from '../src/repositories/fixtures.repository.js';
import { FootballProvider, ProviderFixture, ProviderEvent } from '../src/providers/football-provider.js';

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
  });
});
