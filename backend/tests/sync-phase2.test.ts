import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { db, schema, pool } from '../src/db/index.js';
import { FixturesRepository } from '../src/repositories/fixtures.repository.js';
import { SyncService } from '../src/acquisition/sync.service.js';
import { AcquisitionService } from '../src/acquisition/acquisition.service.js';
import { AcquisitionPolicyEngine, SyncPlan } from '../src/acquisition/acquisition-policy.js';
import { MockFootballProvider } from '../src/providers/mock.provider.js';
import { ApiFootballProvider } from '../src/providers/api-football/api-football.provider.js';
import { RequestGateway } from '../src/gateway/request-gateway.js';
import { inArray, eq } from 'drizzle-orm';

describe('Milestone 8 Work Package 2 (Phase 2) - Sync & Policy Integration', () => {
  const repo = new FixturesRepository();

  const testFixtureIds = [
    'fix_phase2_live_1',
    'fix_phase2_appr_1',
    'fix_phase2_fin_rec_1',
    'fix_phase2_fin_old_1',
    'fix_phase2_stale_1',
    'fix_phase2_39001',
    'fix_39001',
  ];
  const testCompIds = ['comp_phase2_39', 'comp_39'];
  const testSeasonIds = ['season_phase2_39_2025', 'season_39_2025'];
  const testTeamIds = ['team_phase2_1', 'team_phase2_2', 'team_1', 'team_2'];

  const cleanup = async () => {
    await db.delete(schema.events).where(inArray(schema.events.fixtureId, testFixtureIds)).catch(() => {});
    await db.delete(schema.fixtures).where(inArray(schema.fixtures.id, testFixtureIds)).catch(() => {});
    await db.delete(schema.teams).where(inArray(schema.teams.id, testTeamIds)).catch(() => {});
    await db.delete(schema.competitionSeasons).where(inArray(schema.competitionSeasons.id, testSeasonIds)).catch(() => {});
    await db.delete(schema.competitions).where(inArray(schema.competitions.id, testCompIds)).catch(() => {});
  };

  beforeEach(async () => {
    await cleanup();
  });

  afterEach(async () => {
    await cleanup();
  });

  // ============================================================
  // 1. FixturesRepository.getActiveSummary() Tests
  // ============================================================
  describe('FixturesRepository.getActiveSummary()', () => {
    it('returns accurate summary counts using AcquisitionPolicyEngine classification', async () => {
      const fixedNow = new Date('2026-03-01T15:00:00Z');

      // Seed isolated reference data
      await db.insert(schema.competitions).values({
        id: 'comp_phase2_39',
        canonicalId: 'ft_comp_phase2_39',
        name: 'Premier League Phase 2 Test',
        code: 'PLP2',
        country: 'England',
      }).onConflictDoNothing();

      await db.insert(schema.competitionSeasons).values({
        id: 'season_phase2_39_2025',
        competitionId: 'comp_phase2_39',
        year: 2025,
        displayYear: '2025/2026',
        startDate: new Date('2025-08-01'),
        endDate: new Date('2026-05-31'),
      }).onConflictDoNothing();

      await db.insert(schema.teams).values([
        { id: 'team_phase2_1', canonicalId: 'ft_team_phase2_1', name: 'Team A Test', shortName: 'TMA' },
        { id: 'team_phase2_2', canonicalId: 'ft_team_phase2_2', name: 'Team B Test', shortName: 'TMB' },
      ]).onConflictDoNothing();

      // 1. Live fixture
      await db.insert(schema.fixtures).values({
        id: 'fix_phase2_live_1',
        canonicalId: 'ft_fix_phase2_live_1',
        competitionId: 'comp_phase2_39',
        seasonId: 'season_phase2_39_2025',
        homeTeamId: 'team_phase2_1',
        awayTeamId: 'team_phase2_2',
        kickoffTime: new Date('2026-03-01T14:30:00Z'),
        state: 'LIVE',
        elapsedMinute: 30,
        homeScore: 1,
        awayScore: 0,
      });

      // 2. Approaching fixture (10m away)
      await db.insert(schema.fixtures).values({
        id: 'fix_phase2_appr_1',
        canonicalId: 'ft_fix_phase2_appr_1',
        competitionId: 'comp_phase2_39',
        seasonId: 'season_phase2_39_2025',
        homeTeamId: 'team_phase2_1',
        awayTeamId: 'team_phase2_2',
        kickoffTime: new Date('2026-03-01T15:10:00Z'),
        state: 'PREMATCH',
      });

      // 3. Recently finished fixture (polled 5m ago)
      await db.insert(schema.fixtures).values({
        id: 'fix_phase2_fin_rec_1',
        canonicalId: 'ft_fix_phase2_fin_rec_1',
        competitionId: 'comp_phase2_39',
        seasonId: 'season_phase2_39_2025',
        homeTeamId: 'team_phase2_1',
        awayTeamId: 'team_phase2_2',
        kickoffTime: new Date('2026-03-01T13:00:00Z'),
        state: 'FINISHED',
        lastPolledAt: new Date('2026-03-01T14:55:00Z'),
      });

      // 4. Old finished fixture (polled 1h ago) -> FINAL
      await db.insert(schema.fixtures).values({
        id: 'fix_phase2_fin_old_1',
        canonicalId: 'ft_fix_phase2_fin_old_1',
        competitionId: 'comp_phase2_39',
        seasonId: 'season_phase2_39_2025',
        homeTeamId: 'team_phase2_1',
        awayTeamId: 'team_phase2_2',
        kickoffTime: new Date('2026-03-01T12:00:00Z'),
        state: 'FINISHED',
        lastPolledAt: new Date('2026-03-01T14:00:00Z'),
      });

      // 5. Stale prematch fixture (4h past kickoff)
      await db.insert(schema.fixtures).values({
        id: 'fix_phase2_stale_1',
        canonicalId: 'ft_fix_phase2_stale_1',
        competitionId: 'comp_phase2_39',
        seasonId: 'season_phase2_39_2025',
        homeTeamId: 'team_phase2_1',
        awayTeamId: 'team_phase2_2',
        kickoffTime: new Date('2026-03-01T11:00:00Z'),
        state: 'PREMATCH',
      });

      const summary = await repo.getActiveSummary(fixedNow);

      expect(summary.liveCount).toBeGreaterThanOrEqual(1);
      expect(summary.approachingCount).toBeGreaterThanOrEqual(1);
      expect(summary.recentlyFinishedCount).toBeGreaterThanOrEqual(1);
      expect(summary.stalePrematchCount).toBeGreaterThanOrEqual(1);
      expect(summary.scheduleKnownForToday).toBe(true);
    });
  });

  // ============================================================
  // 2. Dedicated AcquisitionService Methods
  // ============================================================
  describe('AcquisitionService dedicated methods', () => {
    it('acquireLive() passes liveOnly: true and competitionIds to provider', async () => {
      let passedFilter: any = null;
      const dummyProvider: any = {
        providerName: 'mock_test',
        getFixtures: async (filter?: any) => {
          passedFilter = filter;
          return [];
        },
      };

      const acqService = new AcquisitionService(dummyProvider);
      await acqService.acquireLive([39, 140]);

      expect(passedFilter).toEqual({ liveOnly: true, competitionIds: [39, 140] });
    });

    it('acquireScheduled() passes date and competitionIds to provider', async () => {
      let passedFilter: any = null;
      const dummyProvider: any = {
        providerName: 'mock_test',
        getFixtures: async (filter?: any) => {
          passedFilter = filter;
          return [];
        },
      };

      const acqService = new AcquisitionService(dummyProvider);
      await acqService.acquireScheduled('2026-03-01', [39]);

      expect(passedFilter).toEqual({ date: '2026-03-01', competitionIds: [39] });
    });
  });

  // ============================================================
  // 3. Dedicated SyncService Methods & Plan Orchestration
  // ============================================================
  describe('SyncService orchestration', () => {
    const createIsolatedProvider = (name = 'mock_isolated_phase2') => ({
      providerName: name,
      getFixtures: async (filter?: any) => [
        {
          id: 'fix_phase2_39001',
          competitionId: 'comp_phase2_39',
          seasonId: 'season_phase2_39_2025',
          homeTeamId: 'team_phase2_1',
          awayTeamId: 'team_phase2_2',
          competition: {
            id: 'comp_phase2_39',
            name: 'Premier League Phase 2 Test',
            code: 'PLP2',
            country: 'England',
          },
          season: {
            id: 'season_phase2_39_2025',
            competitionId: 'comp_phase2_39',
            year: 2025,
            displayYear: '2025/2026',
            startDate: '2025-08-01',
            endDate: '2026-05-31',
          },
          homeTeam: { id: 'team_phase2_1', name: 'Team A Test', shortName: 'TMA' },
          awayTeam: { id: 'team_phase2_2', name: 'Team B Test', shortName: 'TMB' },
          round: 'Round 1',
          kickoffTime: '2026-03-01T15:00:00Z',
          state: 'LIVE',
          elapsedMinute: 20,
          extraMinute: 0,
          score: { home: 1, away: 0 },
          venue: 'Test Stadium',
          referee: 'Test Ref',
          events: [],
        },
      ],
      getLiveMatches: async () => [],
      getFixtureDetail: async () => null,
    });

    it('syncLive() synchronizes live matches', async () => {
      const provider = createIsolatedProvider();
      const syncService = new SyncService(provider as any);

      const summary = await syncService.syncLive();

      expect(summary.provider).toBe('mock_isolated_phase2');
      expect(summary.fixturesDiscovered).toBe(1);
    });

    it('syncSchedule() synchronizes scheduled fixtures', async () => {
      const provider = createIsolatedProvider();
      const syncService = new SyncService(provider as any);

      const summary = await syncService.syncSchedule('2026-09-01');

      expect(summary.provider).toBe('mock_isolated_phase2');
      expect(summary.fixturesDiscovered).toBe(1);
    });

    it('syncWithPlan() routes POLL_LIVE to syncLive()', async () => {
      const provider = createIsolatedProvider();
      const syncService = new SyncService(provider as any);

      const plan: SyncPlan = {
        action: 'POLL_LIVE',
        priority: 'NORMAL',
        recommendedNextPollMs: 120_000,
        reason: 'Test live plan',
      };

      const summary = await syncService.syncWithPlan(plan);
      expect(summary).not.toBeNull();
      expect(summary?.provider).toBe('mock_isolated_phase2');
    });

    it('syncWithPlan() returns null for IDLE action', async () => {
      const provider = createIsolatedProvider();
      const syncService = new SyncService(provider as any);

      const plan: SyncPlan = {
        action: 'IDLE',
        priority: 'NORMAL',
        recommendedNextPollMs: null,
        reason: 'System idle',
      };

      const summary = await syncService.syncWithPlan(plan);
      expect(summary).toBeNull();
    });
  });


  // ============================================================
  // 4. End-to-End Consolidation & Gateway Protection
  // ============================================================
  describe('Consolidated live polling via ApiFootballProvider (offline mocked)', () => {
    it('executes exactly 1 gateway request to live=all when syncLive() is called', async () => {
      const gateway = new RequestGateway(pool);
      const requestedUrls: string[] = [];

      const mockFetch = async (url: string) => {
        requestedUrls.push(url);
        return {
          status: 200,
          headers: new Headers({ 'x-ratelimit-requests-remaining': '85' }),
          json: async () => ({
            get: 'fixtures',
            parameters: { live: 'all' },
            errors: [],
            results: 1,
            paging: { current: 1, total: 1 },
            response: [
              {
                fixture: {
                  id: 39001,
                  referee: null,
                  timezone: 'UTC',
                  date: '2026-03-01T15:00:00Z',
                  timestamp: 1772463600,
                  periods: { first: null, second: null },
                  venue: { id: null, name: 'Emirates Stadium', city: 'London' },
                  status: { long: 'First Half', short: '1H', elapsed: 20, extra: 0 },
                },
                league: { id: 39, name: 'Premier League Phase 2 Test', country: 'England', logo: '', flag: null, season: 2025, round: 'Round 26' },
                teams: {
                  home: { id: 1, name: 'Arsenal Test', logo: '', winner: null },
                  away: { id: 2, name: 'Chelsea Test', logo: '', winner: null },
                },
                goals: { home: 1, away: 0 },
                score: {
                  halftime: { home: null, away: null },
                  fulltime: { home: null, away: null },
                  extratime: { home: null, away: null },
                  penalty: { home: null, away: null },
                },
                events: [],
              },
            ],
          }),
        };
      };

      const provider = new ApiFootballProvider({
        apiKey: 'test_key',
        gateway,
        fetchFn: mockFetch as any,
      });

      const syncService = new SyncService(provider);
      const summary = await syncService.syncLive([39]);

      // Exactly 1 external request made
      expect(requestedUrls.length).toBe(1);
      expect(requestedUrls[0]).toContain('live=all');

      // Summary captures the synced live match
      expect(summary.fixturesDiscovered).toBe(1);
      expect(summary.fixturesInserted).toBe(1);

      // Verify PostgreSQL has the live match
      const saved = await db.query.fixtures.findFirst({
        where: eq(schema.fixtures.id, 'fix_39001'),
      });
      expect(saved).not.toBeNull();
      expect(['LIVE', 'FIRST_HALF']).toContain(saved?.state);
      expect(saved?.homeScore).toBe(1);
    });
  });
});

