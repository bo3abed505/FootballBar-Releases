import { describe, it, expect, beforeAll, afterAll, beforeEach, afterEach, vi } from 'vitest';
import { buildServer } from '../src/server.js';
import { FastifyInstance } from 'fastify';
import { db, schema } from '../src/db/index.js';
import { inArray, eq, or } from 'drizzle-orm';
import { SyncService } from '../src/acquisition/sync.service.js';
import { MatchRepository } from '../src/repositories/match.repository.js';
import { FixturesRepository } from '../src/repositories/fixtures.repository.js';
import { FotmobProvider } from '../src/providers/fotmob/fotmob.provider.js';
import { CircuitBreaker, CircuitState } from '../src/resilience/circuit-breaker.js';
import { IDataProvider, NormalizedMatch, MatchStatus } from '../src/providers/types.js';
import { ProviderChallengeError, ProviderNetworkError } from '../src/providers/errors.js';

describe('Phase 9 — Real-Time Live Testing & Daemon Hardening (E2E Matchday Simulation)', () => {
  let app: FastifyInstance;

  const UCL_COMP_ID = 'comp_ucl_sim';
  const UCL_SEASON_ID = 'season_ucl_2026';
  const TEAM_RMA = 'team_sim_rma';
  const TEAM_MCI = 'team_sim_mci';
  const MATCH_ID = 'fix_sim_ucl_final';

  const allFixtureIds = [MATCH_ID];
  const allTeamIds = [TEAM_RMA, TEAM_MCI];

  const cleanupDatabase = async () => {
    try {
      await db.delete(schema.events);
      await db.delete(schema.fixtures);
      await db.delete(schema.fixtureProviderAliases).where(inArray(schema.fixtureProviderAliases.providerId, allFixtureIds));
      await db.delete(schema.competitionSeasons).where(eq(schema.competitionSeasons.competitionId, UCL_COMP_ID));
      await db.delete(schema.competitionProviderAliases).where(eq(schema.competitionProviderAliases.canonicalCompetitionId, `ft_${UCL_COMP_ID}`));
      await db.delete(schema.competitionProviderAliases).where(eq(schema.competitionProviderAliases.providerId, UCL_COMP_ID));
      await db.delete(schema.competitions).where(eq(schema.competitions.id, UCL_COMP_ID));
      await db.delete(schema.teamProviderAliases).where(inArray(schema.teamProviderAliases.providerId, allTeamIds));
      await db.delete(schema.teams).where(inArray(schema.teams.id, allTeamIds));
    } catch {
      // Ignore cleanup error if tables are already clear
    }
  };

  beforeAll(async () => {
    app = await buildServer();
    await app.ready();
    await cleanupDatabase();
  });

  afterAll(async () => {
    await cleanupDatabase();
    await app.close();
  });

  beforeEach(async () => {
    await cleanupDatabase();

    // 1. Seed Champions League competition & active season
    await db.insert(schema.competitions).values({
      id: UCL_COMP_ID,
      canonicalId: `ft_${UCL_COMP_ID}`,
      name: 'UEFA Champions League',
      code: 'UCL',
      type: 'CUP',
      country: 'Europe',
    }).onConflictDoNothing();

    await db.insert(schema.competitionSeasons).values({
      id: UCL_SEASON_ID,
      competitionId: UCL_COMP_ID,
      year: 2026,
      displayYear: '2025/2026',
      startDate: new Date('2025-09-01'),
      endDate: new Date('2026-06-01'),
      isCurrent: true,
    }).onConflictDoNothing();

    // 2. Seed Real Madrid & Manchester City teams
    await db.insert(schema.teams).values({
      id: TEAM_RMA,
      canonicalId: `ft_${TEAM_RMA}`,
      name: 'Real Madrid',
      shortName: 'RMA',
    }).onConflictDoNothing();

    await db.insert(schema.teams).values({
      id: TEAM_MCI,
      canonicalId: `ft_${TEAM_MCI}`,
      name: 'Manchester City',
      shortName: 'MCI',
    }).onConflictDoNothing();
  });

  afterEach(async () => {
    await cleanupDatabase();
    vi.restoreAllMocks();
  });

  // =========================================================================
  // 1. Matchday Score Evolution: 90-Minute Matchday Progression
  // =========================================================================
  describe('Constraint 1: Matchday Score Evolution', () => {
    it('accurately upserts match milestones and transitions ETags at kickoff, goals, halftime, and full-time', async () => {
      const matchRepo = new MatchRepository();
      let currentMatchState: NormalizedMatch;

      const dynamicProvider: IDataProvider = {
        name: 'ucl_live_provider',
        getLiveMatches: async () => [currentMatchState],
        getDailyMatches: async () => [currentMatchState],
        getMatchDetails: async () => ({} as any),
      };

      const syncService = new SyncService(dynamicProvider, matchRepo);
      (app as any).syncService = syncService;

      const baseKickoff = new Date(Date.now() - 30 * 60 * 1000); // Kicked off 30m ago

      // --- Milestone 1: Pre-match (15 mins before kickoff) ---
      currentMatchState = {
        providerMatchId: MATCH_ID,
        competitionId: UCL_COMP_ID,
        competitionName: 'UEFA Champions League',
        homeTeam: { providerId: TEAM_RMA, name: 'Real Madrid', score: null },
        awayTeam: { providerId: TEAM_MCI, name: 'Manchester City', score: null },
        time: {
          scheduledKickoff: new Date(Date.now() + 15 * 60 * 1000).toISOString(),
          status: MatchStatus.SCHEDULED,
          minute: null,
          addedTime: null,
          displayClock: '',
        },
      };

      const syncResult1 = await syncService.ingestNormalizedMatches([currentMatchState], true);
      expect(syncResult1.fixturesInserted + syncResult1.fixturesUpdated).toBeGreaterThan(0);

      const res1 = await app.inject({ method: 'GET', url: '/api/v2/tray/live' });
      expect(res1.statusCode).toBe(200);
      const body1 = JSON.parse(res1.payload);
      expect(body1.data.matches.length).toBe(1);
      expect(['PREMATCH', 'SCHEDULED']).toContain(body1.data.matches[0].state);
      expect(body1.data.matches[0].score.home).toBe(0);
      expect(body1.data.matches[0].score.away).toBe(0);
      const etag1 = res1.headers['etag'];
      expect(etag1).toBeDefined();

      // Verify ETag 304 on re-poll with etag1
      const poll304_1 = await app.inject({
        method: 'GET',
        url: '/api/v2/tray/live',
        headers: { 'if-none-match': etag1 },
      });
      expect(poll304_1.statusCode).toBe(304);
      expect(poll304_1.payload).toBe('');

      // --- Milestone 2: Kickoff & First Half (15th Minute, 0-0) ---
      await new Promise((r) => setTimeout(r, 10)); // Ensure millisecond timestamp advance
      currentMatchState = {
        ...currentMatchState,
        time: {
          scheduledKickoff: baseKickoff.toISOString(),
          status: MatchStatus.FIRST_HALF,
          minute: 15,
          addedTime: 0,
          displayClock: "15'",
        },
      };

      await syncService.ingestNormalizedMatches([currentMatchState], true);

      const res2 = await app.inject({ method: 'GET', url: '/api/v2/tray/live' });
      expect(res2.statusCode).toBe(200);
      const body2 = JSON.parse(res2.payload);
      expect(['LIVE', 'FIRST_HALF']).toContain(body2.data.matches[0].state);
      expect(body2.data.matches[0].elapsedMinute).toBe(15);
      const etag2 = res2.headers['etag'];
      expect(etag2).toBeDefined();
      expect(etag2).not.toBe(etag1); // ETag must transition on match kickoff / minute update

      // --- Milestone 3: Goal 1 (34th Minute: Real Madrid 1 - 0 Manchester City) ---
      await new Promise((r) => setTimeout(r, 10));
      currentMatchState = {
        ...currentMatchState,
        homeTeam: { providerId: TEAM_RMA, name: 'Real Madrid', score: 1 },
        awayTeam: { providerId: TEAM_MCI, name: 'Manchester City', score: 0 },
        time: {
          scheduledKickoff: baseKickoff.toISOString(),
          status: MatchStatus.FIRST_HALF,
          minute: 34,
          addedTime: 0,
          displayClock: "34'",
        },
      };

      await syncService.ingestNormalizedMatches([currentMatchState], true);

      // Also ingest the goal incident event
      await db.insert(schema.events).values({
        id: `ev_${MATCH_ID}_goal_1`,
        eventUid: `ev_uid_${MATCH_ID}_goal_1`,
        fixtureId: MATCH_ID,
        type: 'GOAL',
        minute: 34,
        teamId: TEAM_RMA,
        playerName: 'Vinicius Jr',
        description: 'Goal! Vinicius Jr curls it into the top corner.',
        timestamp: new Date(),
      });

      const res3 = await app.inject({ method: 'GET', url: '/api/v2/tray/live' });
      expect(res3.statusCode).toBe(200);
      const body3 = JSON.parse(res3.payload);
      expect(body3.data.matches[0].score.home).toBe(1);
      expect(body3.data.matches[0].score.away).toBe(0);
      expect(body3.data.matches[0].elapsedMinute).toBe(34);
      expect(body3.data.matches[0].recentEvents.length).toBe(1);
      expect(body3.data.matches[0].recentEvents[0].playerName).toBe('Vinicius Jr');
      const etag3 = res3.headers['etag'];
      expect(etag3).not.toBe(etag2); // ETag must transition on score update

      // Verify detail endpoint /api/v2/matches/:id
      const detailRes = await app.inject({ method: 'GET', url: `/api/v2/matches/${MATCH_ID}` });
      expect(detailRes.statusCode).toBe(200);
      const detailBody = JSON.parse(detailRes.payload);
      expect(detailBody.data.score.home).toBe(1);
      expect(detailBody.data.events.length).toBe(1);

      // --- Milestone 4: Halftime Break (HT, 1-0) ---
      await new Promise((r) => setTimeout(r, 10));
      currentMatchState = {
        ...currentMatchState,
        time: {
          scheduledKickoff: baseKickoff.toISOString(),
          status: MatchStatus.HALFTIME,
          minute: 45,
          addedTime: 2,
          displayClock: 'HT',
        },
      };

      await syncService.ingestNormalizedMatches([currentMatchState], true);

      const res4 = await app.inject({ method: 'GET', url: '/api/v2/tray/live' });
      expect(res4.statusCode).toBe(200);
      const body4 = JSON.parse(res4.payload);
      expect(['HT', 'HALFTIME']).toContain(body4.data.matches[0].state);
      const etag4 = res4.headers['etag'];
      expect(etag4).not.toBe(etag3);

      // --- Milestone 5: Goal 2 (68th Minute: De Bruyne equalizes 1 - 1) ---
      await new Promise((r) => setTimeout(r, 10));
      currentMatchState = {
        ...currentMatchState,
        homeTeam: { providerId: TEAM_RMA, name: 'Real Madrid', score: 1 },
        awayTeam: { providerId: TEAM_MCI, name: 'Manchester City', score: 1 },
        time: {
          scheduledKickoff: baseKickoff.toISOString(),
          status: MatchStatus.SECOND_HALF,
          minute: 68,
          addedTime: 0,
          displayClock: "68'",
        },
      };

      await syncService.ingestNormalizedMatches([currentMatchState], true);

      await db.insert(schema.events).values({
        id: `ev_${MATCH_ID}_goal_2`,
        eventUid: `ev_uid_${MATCH_ID}_goal_2`,
        fixtureId: MATCH_ID,
        type: 'GOAL',
        minute: 68,
        teamId: TEAM_MCI,
        playerName: 'K. De Bruyne',
        description: 'Goal! De Bruyne strikes from outside the box.',
        timestamp: new Date(),
      });

      const res5 = await app.inject({ method: 'GET', url: '/api/v2/tray/live' });
      expect(res5.statusCode).toBe(200);
      const body5 = JSON.parse(res5.payload);
      expect(body5.data.matches[0].score.home).toBe(1);
      expect(body5.data.matches[0].score.away).toBe(1);
      expect(body5.data.matches[0].recentEvents.length).toBe(2);
      const etag5 = res5.headers['etag'];
      expect(etag5).not.toBe(etag4);

      // --- Milestone 6: Full Time (FT, 1-1) ---
      await new Promise((r) => setTimeout(r, 10));
      currentMatchState = {
        ...currentMatchState,
        time: {
          scheduledKickoff: baseKickoff.toISOString(),
          status: MatchStatus.FINISHED,
          minute: 90,
          addedTime: 4,
          displayClock: 'FT',
        },
      };

      await syncService.ingestNormalizedMatches([currentMatchState], true);

      const res6 = await app.inject({ method: 'GET', url: '/api/v2/tray/live' });
      expect(res6.statusCode).toBe(200);
      const body6 = JSON.parse(res6.payload);
      expect(body6.data.matches[0].state).toBe('FINISHED');
      expect(body6.data.matches[0].score.home).toBe(1);
      expect(body6.data.matches[0].score.away).toBe(1);
      const etag6 = res6.headers['etag'];
      expect(etag6).not.toBe(etag5);

      // Verify that all 6 milestones produced unique, progression-deterministic ETags
      const allEtags = [etag1, etag2, etag3, etag4, etag5, etag6];
      const uniqueEtags = new Set(allEtags);
      expect(uniqueEtags.size).toBe(6);
    });
  });

  // =========================================================================
  // 2. Edge Defense Audit: FotMob Cloudflare Challenge & Socket Timeout
  // =========================================================================
  describe('Constraint 2: Edge Defense Audit', () => {
    it('trips CircuitBreaker on Cloudflare 403 / HTML block, swallows error, and serves cachedFallback without crashing', async () => {
      // 1. Seed live match in SQLite
      const matchRepo = new MatchRepository();
      const liveMatch: NormalizedMatch = {
        providerMatchId: MATCH_ID,
        competitionId: UCL_COMP_ID,
        competitionName: 'UEFA Champions League',
        homeTeam: { providerId: TEAM_RMA, name: 'Real Madrid', score: 2 },
        awayTeam: { providerId: TEAM_MCI, name: 'Manchester City', score: 1 },
        time: {
          scheduledKickoff: new Date().toISOString(),
          status: MatchStatus.SECOND_HALF,
          minute: 55,
          addedTime: 0,
          displayClock: "55'",
        },
      };

      await matchRepo.ingestNormalizedMatches([liveMatch], { providerName: 'fotmob', liveOnly: true });

      // 2. Instantiate FotmobProvider with CircuitBreaker (failureThreshold = 1 for immediate trip)
      const circuitBreaker = new CircuitBreaker({ failureThreshold: 1, resetTimeoutMs: 30000 });
      const fotmobProvider = new FotmobProvider({ circuitBreaker });
      const syncService = new SyncService(fotmobProvider, matchRepo);
      (app as any).syncService = syncService;

      expect(circuitBreaker.getState()).toBe(CircuitState.CLOSED);

      // 3. Simulate severe Cloudflare challenge: HTTP 403 HTML challenge block
      const originalFetch = global.fetch;
      global.fetch = vi.fn().mockResolvedValue({
        ok: false,
        status: 403,
        headers: new Headers({ 'content-type': 'text/html' }),
        text: async () => '<!DOCTYPE html><html><title>Attention Required! | Cloudflare</title><body>403 Forbidden Cloudflare Block</body></html>',
      });

      // 4. Trigger live sync: Must swallow ProviderChallengeError and return cached fallback
      const syncResult1 = await syncService.sync({ liveOnly: true });
      expect(syncResult1.cachedFallback).toBe(true);
      expect(syncResult1.hasChanges).toBe(false);

      // Verify CircuitBreaker tripped to OPEN
      expect(circuitBreaker.getState()).toBe(CircuitState.OPEN);

      // 5. Subsequent call through CircuitBreaker trips fast without touching the network
      const syncResult2 = await syncService.sync({ liveOnly: true });
      expect(syncResult2.cachedFallback).toBe(true);

      // 6. Verify client polling /api/v2/tray/live during upstream edge outage:
      // Must respond HTTP 200 OK with cachedFallback: true, X-Cached-Fallback header, and intact cached match!
      const clientPoll = await app.inject({ method: 'GET', url: '/api/v2/tray/live' });
      expect(clientPoll.statusCode).toBe(200);
      expect(clientPoll.headers['x-cached-fallback']).toBe('true');

      const clientBody = JSON.parse(clientPoll.payload);
      expect(clientBody.cachedFallback).toBe(true);
      expect(clientBody.data.cachedFallback).toBe(true);
      expect(clientBody.data.matches.length).toBe(1);
      expect(clientBody.data.matches[0].score.home).toBe(2);
      expect(clientBody.data.matches[0].score.away).toBe(1);

      // 7. Re-poll with ETag while still in fallback: Must return 304 with X-Cached-Fallback header
      const etag = clientPoll.headers['etag'];
      const clientPoll304 = await app.inject({
        method: 'GET',
        url: '/api/v2/tray/live',
        headers: { 'if-none-match': etag },
      });
      expect(clientPoll304.statusCode).toBe(304);
      expect(clientPoll304.headers['x-cached-fallback']).toBe('true');

      global.fetch = originalFetch;
    });

    it('gracefully swallows socket timeout / AbortError without crashing event loop', async () => {
      const matchRepo = new MatchRepository();
      const circuitBreaker = new CircuitBreaker({ failureThreshold: 2, resetTimeoutMs: 30000 });
      const fotmobProvider = new FotmobProvider({ circuitBreaker });
      const syncService = new SyncService(fotmobProvider, matchRepo);
      (app as any).syncService = syncService;

      // Simulate upstream socket timeout (AbortController fires after timeout)
      const originalFetch = global.fetch;
      global.fetch = vi.fn().mockImplementation((_url, options) => {
        return new Promise((_resolve, reject) => {
          if (options?.signal) {
            options.signal.addEventListener('abort', () => {
              const abortErr = new DOMException('The operation was aborted.', 'AbortError');
              reject(abortErr);
            });
          }
          // Simulate immediate timeout trigger
          setTimeout(() => {
            const abortErr = new DOMException('The operation was aborted.', 'AbortError');
            reject(abortErr);
          }, 10);
        });
      });

      // Execute sync: Must NOT throw unhandled rejection, returns cached fallback
      const syncResult = await syncService.sync({ liveOnly: true });
      expect(syncResult.cachedFallback).toBe(true);

      const clientRes = await app.inject({ method: 'GET', url: '/api/v2/tray/live' });
      expect(clientRes.statusCode).toBe(200);
      expect(clientRes.headers['x-cached-fallback']).toBe('true');

      global.fetch = originalFetch;
    });
  });

  // =========================================================================
  // 3. ETag Efficiency Benchmark: Quiet Matchday Period Polling
  // =========================================================================
  describe('Constraint 3: ETag Efficiency Benchmark', () => {
    it('serves HTTP 304 Not Modified for >= 90% of rapid client polls during quiet period', async () => {
      // 1. Seed static match (representing a 15-minute quiet period with no goals)
      const fixedTime = new Date();
      await db.insert(schema.fixtures).values({
        id: MATCH_ID,
        canonicalId: `ft_${MATCH_ID}`,
        competitionId: UCL_COMP_ID,
        seasonId: UCL_SEASON_ID,
        homeTeamId: TEAM_RMA,
        awayTeamId: TEAM_MCI,
        state: 'LIVE',
        elapsedMinute: 72,
        homeScore: 1,
        awayScore: 1,
        kickoffTime: new Date(fixedTime.getTime() - 75 * 60 * 1000),
        updatedAt: fixedTime,
      });

      // 2. Initial cold poll to acquire initial ETag
      const coldPoll = await app.inject({ method: 'GET', url: '/api/v2/tray/live' });
      expect(coldPoll.statusCode).toBe(200);
      const initialEtag = coldPoll.headers['etag'] as string;
      expect(initialEtag).toBeDefined();

      // 3. Simulate rapid 5-second polling (50 polls total)
      const TOTAL_POLLS = 50;
      let count304 = 0;
      let count200 = 0;

      const startTime = performance.now();

      for (let i = 0; i < TOTAL_POLLS; i++) {
        const poll = await app.inject({
          method: 'GET',
          url: '/api/v2/tray/live',
          headers: { 'if-none-match': initialEtag },
        });

        if (poll.statusCode === 304) {
          count304++;
          expect(poll.payload).toBe(''); // 304 must have completely empty body (zero payload serialization)
        } else if (poll.statusCode === 200) {
          count200++;
        }
      }

      const totalDurationMs = performance.now() - startTime;
      const avgDurationPerPollMs = totalDurationMs / TOTAL_POLLS;

      // 4. Assert benchmark metrics
      const notModifiedRatio = count304 / TOTAL_POLLS;
      expect(notModifiedRatio).toBeGreaterThanOrEqual(0.90); // Must exceed 90% threshold (in this test, exactly 100%)
      expect(count304).toBe(50);
      expect(count200).toBe(0);

      // Verify sub-5ms performance per localhost request on Fastify inject
      expect(avgDurationPerPollMs).toBeLessThan(10);
    });
  });

  // =========================================================================
  // 4. Memory & Concurrency Hardening: High-Load Chaos & Leak Detection
  // =========================================================================
  describe('Constraint 4: Memory & Concurrency Hardening', () => {
    it('handles high concurrent load without unhandled promise rejections or memory leakage', async () => {
      // 1. Seed match
      const now = new Date();
      await db.insert(schema.fixtures).values({
        id: MATCH_ID,
        canonicalId: `ft_${MATCH_ID}`,
        competitionId: UCL_COMP_ID,
        seasonId: UCL_SEASON_ID,
        homeTeamId: TEAM_RMA,
        awayTeamId: TEAM_MCI,
        state: 'LIVE',
        elapsedMinute: 80,
        homeScore: 2,
        awayScore: 2,
        kickoffTime: new Date(now.getTime() - 85 * 60 * 1000),
        updatedAt: now,
      });

      // Track unhandled rejection events
      let unhandledCount = 0;
      const unhandledTracker = () => { unhandledCount++; };
      process.on('unhandledRejection', unhandledTracker);

      const memInitial = process.memoryUsage().heapUsed;

      // 2. Fire 30 concurrent mixed requests simultaneously
      const CONCURRENT_REQUESTS = 30;
      const requests = Array.from({ length: CONCURRENT_REQUESTS }).map((_, idx) => {
        if (idx % 3 === 0) {
          return app.inject({ method: 'GET', url: '/api/v2/tray/live' });
        } else if (idx % 3 === 1) {
          return app.inject({
            method: 'GET',
            url: '/api/v2/tray/live',
            headers: { 'if-none-match': 'W/"live-fake-etag-check"' },
          });
        } else {
          return app.inject({ method: 'GET', url: `/api/v2/matches/${MATCH_ID}` });
        }
      });

      const responses = await Promise.all(requests);

      // Assert all concurrent requests succeeded with 200 or 304 (0 server crashes / 500s)
      for (const res of responses) {
        expect([200, 304]).toContain(res.statusCode);
      }

      // 3. Repeat 100 rapid requests to test memory stability
      for (let i = 0; i < 100; i++) {
        await app.inject({ method: 'GET', url: '/api/v2/tray/live' });
      }

      const memFinal = process.memoryUsage().heapUsed;
      const heapDeltaMb = (memFinal - memInitial) / (1024 * 1024);

      // Ensure no massive runaway memory leak (delta under 50 MB for 130 operations)
      expect(heapDeltaMb).toBeLessThan(50);

      // Assert strictly zero unhandled rejections occurred during execution
      expect(unhandledCount).toBe(0);

      process.off('unhandledRejection', unhandledTracker);
    });
  });
});
