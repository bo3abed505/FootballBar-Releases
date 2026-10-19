import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { db, schema } from '../src/db/index.js';
import { inArray, eq } from 'drizzle-orm';
import { MatchRepository } from '../src/repositories/match.repository.js';
import { SyncService } from '../src/acquisition/sync.service.js';
import { FixturesRepository } from '../src/repositories/fixtures.repository.js';
import {
  IDataProvider,
  NormalizedMatch,
  MatchStatus,
  NormalizedLiveMatch,
  NormalizedMatchDetails,
} from '../src/providers/types.js';
import { ProviderNetworkError, ProviderChallengeError } from '../src/providers/errors.js';
import { NormalizedAcquisitionBatch } from '../src/acquisition/acquisition.service.js';
import { CanonicalFixture } from '../src/normalization/fixture.js';

describe('Database Injection & Normalization Mapping (Phase 7)', () => {
  const matchRepo = new MatchRepository();
  const fixturesRepo = new FixturesRepository();

  const testCompId = 'comp_norm_test_1';
  const testSeasonId = `season_${testCompId}_unknown`;
  const testTeamHome = 'team_norm_home_1';
  const testTeamAway = 'team_norm_away_1';
  const testMatchId1 = 'fix_norm_test_101';
  const testMatchId2 = 'fix_norm_test_102';
  const testMatchId3 = 'fix_norm_test_103';

  const allTestFixtureIds = [testMatchId1, testMatchId2, testMatchId3, 'bad_fixture_1', 'bad_match_1', 'bad_match_2', 'bad_match_3'];
  const allTestTeamIds = [testTeamHome, testTeamAway, 'team_norm_home_2', 'team_norm_away_2'];
  const allTestCompIds = [testCompId, 'comp_norm_test_2'];

  const cleanup = async () => {
    await db.delete(schema.events).where(inArray(schema.events.fixtureId, allTestFixtureIds));
    await db.delete(schema.fixtureProviderAliases).where(inArray(schema.fixtureProviderAliases.providerId, allTestFixtureIds));
    await db.delete(schema.fixtures).where(inArray(schema.fixtures.id, allTestFixtureIds));
    await db.delete(schema.teamProviderAliases).where(inArray(schema.teamProviderAliases.providerId, allTestTeamIds));
    await db.delete(schema.teams).where(inArray(schema.teams.id, allTestTeamIds));
    await db.delete(schema.competitionProviderAliases).where(inArray(schema.competitionProviderAliases.providerId, allTestCompIds));
    await db.delete(schema.competitionSeasons).where(inArray(schema.competitionSeasons.competitionId, allTestCompIds));
    await db.delete(schema.competitions).where(inArray(schema.competitions.id, allTestCompIds));
  };

  beforeEach(async () => {
    await cleanup();
  });

  afterEach(async () => {
    await cleanup();
  });

  // -------------------------------------------------------------------------
  // 1. Transaction Boundaries & Atomicity
  // -------------------------------------------------------------------------
  describe('Constraint 1: Transaction Boundaries & Atomicity', () => {
    it('commits all matches from a single polling cycle atomically in one SQLite transaction', async () => {
      const match1: NormalizedMatch = {
        providerMatchId: testMatchId1,
        competitionId: testCompId,
        competitionName: 'Normalization League',
        homeTeam: { providerId: testTeamHome, name: 'Home FC', score: 1 },
        awayTeam: { providerId: testTeamAway, name: 'Away United', score: 0 },
        time: {
          scheduledKickoff: new Date().toISOString(),
          status: MatchStatus.FIRST_HALF,
          minute: 35,
          addedTime: 0,
          displayClock: "35'",
        },
      };

      const match2: NormalizedMatch = {
        providerMatchId: testMatchId2,
        competitionId: testCompId,
        competitionName: 'Normalization League',
        homeTeam: { providerId: 'team_norm_home_2', name: 'Rovers', score: 2 },
        awayTeam: { providerId: 'team_norm_away_2', name: 'City', score: 2 },
        time: {
          scheduledKickoff: new Date().toISOString(),
          status: MatchStatus.HALFTIME,
          minute: 45,
          addedTime: 2,
          displayClock: 'HT',
        },
      };

      const result = await matchRepo.ingestNormalizedMatches([match1, match2], {
        providerName: 'api_football',
        liveOnly: true,
      });

      expect(result.fixturesInserted).toBe(2);
      expect(result.discardedCount).toBe(0);

      // Verify both fixtures exist in SQLite
      const f1 = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, testMatchId1) });
      const f2 = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, testMatchId2) });

      expect(f1).toBeDefined();
      expect(f1?.homeScore).toBe(1);
      expect(f1?.state).toBe('FIRST_HALF');
      expect(f2).toBeDefined();
      expect(f2?.homeScore).toBe(2);
      expect(f2?.state).toBe('HALFTIME');
    });
  });

  // -------------------------------------------------------------------------
  // 2. Safe Upserts (Deduplication & Foreign Key Stability)
  // -------------------------------------------------------------------------
  describe('Constraint 2: Safe Upserts (Deduplication)', () => {
    it('updates live match in-place without duplicate rows or foreign-key cascade thrashing', async () => {
      // 1. Initial Insert
      const initialMatch: NormalizedMatch = {
        providerMatchId: testMatchId1,
        competitionId: testCompId,
        competitionName: 'Normalization League',
        homeTeam: { providerId: testTeamHome, name: 'Home FC', score: 0 },
        awayTeam: { providerId: testTeamAway, name: 'Away United', score: 0 },
        time: {
          scheduledKickoff: new Date().toISOString(),
          status: MatchStatus.FIRST_HALF,
          minute: 10,
          addedTime: 0,
          displayClock: "10'",
        },
      };

      await matchRepo.ingestNormalizedMatches([initialMatch], {
        providerName: 'api_football',
        liveOnly: true,
      });

      // 2. Attach a child event (foreign key to fixture)
      await db.insert(schema.events).values({
        id: 'evt_test_goal_1',
        eventUid: 'evt_uid_goal_1',
        fixtureId: testMatchId1,
        teamId: testTeamHome,
        type: 'Goal',
        minute: 25,
        playerName: 'Striker A',
      });

      // 3. Second Polling Cycle: Match evolves to 1-0 at 28'
      const updatedMatch: NormalizedMatch = {
        providerMatchId: testMatchId1,
        competitionId: testCompId,
        competitionName: 'Normalization League',
        homeTeam: { providerId: testTeamHome, name: 'Home FC', score: 1 },
        awayTeam: { providerId: testTeamAway, name: 'Away United', score: 0 },
        time: {
          scheduledKickoff: initialMatch.time.scheduledKickoff,
          status: MatchStatus.FIRST_HALF,
          minute: 28,
          addedTime: 0,
          displayClock: "28'",
        },
      };

      const updateResult = await matchRepo.ingestNormalizedMatches([updatedMatch], {
        providerName: 'api_football',
        liveOnly: true,
      });

      expect(updateResult.fixturesInserted).toBe(0);
      expect(updateResult.fixturesUpdated).toBe(1);

      // Verify no duplicate row was created in SQLite
      const allRows = await db.query.fixtures.findMany({ where: eq(schema.fixtures.id, testMatchId1) });
      expect(allRows.length).toBe(1);
      expect(allRows[0].homeScore).toBe(1);
      expect(allRows[0].elapsedMinute).toBe(28);

      // Verify foreign key integrity: the child event was NOT destroyed by delete/re-insert thrashing
      const events = await db.query.events.findMany({ where: eq(schema.events.fixtureId, testMatchId1) });
      expect(events.length).toBe(1);
      expect(events[0].id).toBe('evt_test_goal_1');
    });
  });

  // -------------------------------------------------------------------------
  // 3. Partial Failure Isolation
  // -------------------------------------------------------------------------
  describe('Constraint 3: Partial Failure Isolation', () => {
    it('discards malformed matches before opening transactions in SyncService.processBatch, committing valid matches cleanly', async () => {
      const mockProvider: IDataProvider = {
        name: 'api_football',
        getLiveMatches: async () => [],
        getDailyMatches: async () => [],
        getMatchDetails: async () => ({} as any),
      };
      const syncService = new SyncService(mockProvider, matchRepo);

      const validFixture: CanonicalFixture = {
        id: testMatchId1,
        competitionId: testCompId,
        seasonId: testSeasonId,
        homeTeamId: testTeamHome,
        awayTeamId: testTeamAway,
        round: '1',
        kickoffTime: new Date(),
        state: 'FIRST_HALF',
        elapsedMinute: 15,
        extraMinute: 0,
        homeScore: 1,
        awayScore: 0,
        homeScoreHt: null,
        awayScoreHt: null,
        homeScoreEt: null,
        awayScoreEt: null,
        homeScorePen: null,
        awayScorePen: null,
        venue: 'Valid Arena',
        referee: 'Valid Ref',
        lastPolledAt: new Date(),
        provider: 'api_football',
        providerId: testMatchId1,
      };

      // Malformed fixture: missing homeTeamId, invalid NaN kickoff Date, and negative minute
      const malformedFixture: CanonicalFixture = {
        id: 'bad_fixture_1',
        competitionId: testCompId,
        seasonId: testSeasonId,
        homeTeamId: '', // Invalid empty home team ID triggers validation failure
        awayTeamId: testTeamAway,
        round: null,
        kickoffTime: new Date(NaN), // Invalid NaN Date triggers validation failure
        state: 'FIRST_HALF',
        elapsedMinute: -10, // Negative minute triggers validation failure
        extraMinute: 0,
        homeScore: 0,
        awayScore: 0,
        homeScoreHt: null,
        awayScoreHt: null,
        homeScoreEt: null,
        awayScoreEt: null,
        homeScorePen: null,
        awayScorePen: null,
        venue: null,
        referee: null,
        lastPolledAt: new Date(),
        provider: 'api_football',
        providerId: 'bad_fixture_1',
      };

      const batch: NormalizedAcquisitionBatch = {
        competitions: [{
          id: testCompId,
          name: 'Normalization League',
          code: 'NL',
          type: 'league',
          country: 'England',
          logoUrl: null,
          provider: 'api_football',
          providerId: testCompId,
        }],
        seasons: [{
          id: testSeasonId,
          competitionId: testCompId,
          year: 2026,
          displayYear: '2026',
          startDate: new Date('2026-01-01'),
          endDate: new Date('2026-12-31'),
          isCurrent: true,
        }],
        teams: [
          {
            id: testTeamHome,
            name: 'Home FC',
            shortName: 'HFC',
            code: 'HFC',
            country: 'England',
            logoUrl: null,
            venueName: 'Home Park',
            provider: 'api_football',
            providerId: testTeamHome,
          },
          {
            id: testTeamAway,
            name: 'Away United',
            shortName: 'AWU',
            code: 'AWU',
            country: 'England',
            logoUrl: null,
            venueName: 'Away Stadium',
            provider: 'api_football',
            providerId: testTeamAway,
          },
        ],
        fixtures: [validFixture, malformedFixture],
        events: [],
      };

      // Pass directly through the SyncService.processBatch boundary
      const summary = await syncService.processBatch(batch, true);

      // Assert partial failure isolation: exactly 1 malformed fixture was screened and dropped
      expect(summary.discardedCount).toBe(1);
      // Exactly 1 valid fixture was committed
      expect(summary.fixturesInserted).toBe(1);
      expect(summary.hasChanges).toBe(true);

      // Verify SQLite state: valid match is committed to the database
      const validRow = await db.query.fixtures.findFirst({
        where: eq(schema.fixtures.id, testMatchId1),
      });
      expect(validRow).toBeDefined();
      expect(validRow?.id).toBe(testMatchId1);
      expect(validRow?.homeScore).toBe(1);
      expect(validRow?.state).toBe('FIRST_HALF');

      // Verify SQLite state: malformed fixture was dropped and never committed
      const badRow = await db.query.fixtures.findFirst({
        where: eq(schema.fixtures.id, 'bad_fixture_1'),
      });
      expect(badRow).toBeUndefined();
    });

    it('screens and discards malformed raw matches in SyncService.ingestNormalizedMatches before opening transactions', async () => {
      const mockProvider: IDataProvider = {
        name: 'api_football',
        getLiveMatches: async () => [],
        getDailyMatches: async () => [],
        getMatchDetails: async () => ({} as any),
      };
      const syncService = new SyncService(mockProvider, matchRepo);

      const validMatch: NormalizedMatch = {
        providerMatchId: testMatchId2,
        competitionId: testCompId,
        competitionName: 'Normalization League',
        homeTeam: { providerId: testTeamHome, name: 'Home FC', score: 2 },
        awayTeam: { providerId: testTeamAway, name: 'Away United', score: 1 },
        time: {
          scheduledKickoff: new Date().toISOString(),
          status: MatchStatus.FIRST_HALF,
          minute: 25,
          addedTime: 0,
          displayClock: "25'",
        },
      };

      const malformedMatchNoHomeTeam = {
        providerMatchId: 'bad_match_1',
        time: { scheduledKickoff: new Date().toISOString(), status: MatchStatus.FIRST_HALF },
      };

      const malformedMatchBadDate = {
        providerMatchId: 'bad_match_2',
        homeTeam: { providerId: 'h', name: 'Home' },
        awayTeam: { providerId: 'a', name: 'Away' },
        time: { scheduledKickoff: 'not-a-real-date', status: MatchStatus.FIRST_HALF },
      };

      const malformedMatchNegativeMinute = {
        providerMatchId: 'bad_match_3',
        homeTeam: { providerId: 'h', name: 'Home' },
        awayTeam: { providerId: 'a', name: 'Away' },
        time: { scheduledKickoff: new Date().toISOString(), status: MatchStatus.FIRST_HALF, minute: -10 },
      };

      const rawBatch = [
        malformedMatchNoHomeTeam,
        validMatch,
        malformedMatchBadDate,
        malformedMatchNegativeMinute,
      ];

      const result = await syncService.ingestNormalizedMatches(rawBatch, true);

      // 3 malformed records were safely isolated and discarded
      expect(result.discardedCount).toBe(3);
      // The 1 valid match was successfully committed
      expect(result.fixturesInserted).toBe(1);

      // Verify SQLite state: valid match exists, malformed matches do NOT exist
      const validRow = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, testMatchId2) });
      expect(validRow).toBeDefined();
      expect(validRow?.homeScore).toBe(2);

      const bad1 = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, 'bad_match_1') });
      expect(bad1).toBeUndefined();
    });
  });

  // -------------------------------------------------------------------------
  // 4. Cache & Offline Fallback Behavior
  // -------------------------------------------------------------------------
  describe('Constraint 4: Cache & Offline Fallback Behavior', () => {
    it('gracefully swallows CircuitBreakerOpen/network errors and serves cached state from SQLite', async () => {
      // 1. Seed stable match in database
      const stableMatch: NormalizedMatch = {
        providerMatchId: testMatchId1,
        competitionId: testCompId,
        competitionName: 'Normalization League',
        homeTeam: { providerId: testTeamHome, name: 'Home FC', score: 2 },
        awayTeam: { providerId: testTeamAway, name: 'Away United', score: 1 },
        time: {
          scheduledKickoff: new Date().toISOString(),
          status: MatchStatus.SECOND_HALF,
          minute: 60,
          addedTime: 0,
          displayClock: "60'",
        },
      };

      await matchRepo.ingestNormalizedMatches([stableMatch], {
        providerName: 'mock_provider',
        liveOnly: true,
      });

      // 2. Create provider that trips the CircuitBreaker
      let circuitTripped = true;
      const failingProvider: IDataProvider = {
        name: 'mock_failing',
        getLiveMatches: async () => {
          if (circuitTripped) {
            throw new Error('CircuitBreakerOpen');
          }
          return [];
        },
        getDailyMatches: async () => [],
        getMatchDetails: async () => ({} as any),
      };

      const syncService = new SyncService(failingProvider, matchRepo);

      // 3. Execute sync during outage: must NOT throw and must NOT crash
      const summary = await syncService.sync({ liveOnly: true });

      expect(summary.cachedFallback).toBe(true);
      expect(summary.hasChanges).toBe(false);

      // 4. Query repository (simulating REST API /api/tray)
      const cachedMatches = await fixturesRepo.getTrayMatches({ competitions: [testCompId] });
      expect(cachedMatches.length).toBe(1);
      expect(cachedMatches[0].id).toBe(testMatchId1);
      expect(cachedMatches[0].homeScore).toBe(2);
      expect(cachedMatches[0].awayScore).toBe(1);
    });

    it('gracefully handles timeout / ProviderNetworkError and continues serving last-known good state', async () => {
      // 1. Seed stable match
      const stableMatch: NormalizedMatch = {
        providerMatchId: testMatchId1,
        competitionId: testCompId,
        competitionName: 'Normalization League',
        homeTeam: { providerId: testTeamHome, name: 'Home FC', score: 3 },
        awayTeam: { providerId: testTeamAway, name: 'Away United', score: 2 },
        time: {
          scheduledKickoff: new Date().toISOString(),
          status: MatchStatus.SECOND_HALF,
          minute: 75,
          addedTime: 0,
          displayClock: "75'",
        },
      };

      await matchRepo.ingestNormalizedMatches([stableMatch], {
        providerName: 'mock_provider',
        liveOnly: true,
      });

      // 2. Provider with network timeout
      const timeoutProvider: IDataProvider = {
        name: 'mock_timeout',
        getLiveMatches: async () => {
          throw new ProviderNetworkError('Upstream socket timeout (ETIMEDOUT)');
        },
        getDailyMatches: async () => [],
        getMatchDetails: async () => ({} as any),
      };

      const syncService = new SyncService(timeoutProvider, matchRepo);
      const summary = await syncService.sync({ liveOnly: true });

      expect(summary.cachedFallback).toBe(true);

      const cachedMatches = await fixturesRepo.getTrayMatches({ competitions: [testCompId] });
      expect(cachedMatches.length).toBe(1);
      expect(cachedMatches[0].homeScore).toBe(3);
    });
  });

  // -------------------------------------------------------------------------
  // 5. Rollback Safety
  // -------------------------------------------------------------------------
  describe('Constraint 5: Rollback Safety', () => {
    it('rolls back completely if a critical error occurs mid-transaction, leaving zero orphaned rows', async () => {
      const batchWithBadForeignKey = {
        competitions: [{
          id: 'comp_rollback_test',
          name: 'Rollback Comp',
          code: null,
          type: 'league',
          country: null,
          logoUrl: null,
          provider: 'test',
          providerId: 'comp_rollback_test',
        }],
        seasons: [],
        teams: [{
          id: 'team_rollback_test',
          name: 'Rollback Team',
          shortName: 'RT',
          code: null,
          country: null,
          logoUrl: null,
          venueName: null,
          provider: 'test',
          providerId: 'team_rollback_test',
        }],
        fixtures: [{
          id: 'fix_rollback_test',
          // Deliberately reference non-existent season and foreign keys that trigger constraint error
          competitionId: 'non_existent_comp_id_9999',
          seasonId: 'non_existent_season_id_9999',
          homeTeamId: 'team_rollback_test',
          awayTeamId: 'non_existent_team_id_9999',
          round: null,
          kickoffTime: new Date(),
          state: 'LIVE',
          elapsedMinute: 10,
          extraMinute: 0,
          homeScore: 0,
          awayScore: 0,
          homeScoreHt: null,
          awayScoreHt: null,
          homeScoreEt: null,
          awayScoreEt: null,
          homeScorePen: null,
          awayScorePen: null,
          venue: null,
          referee: null,
          lastPolledAt: new Date(),
          provider: 'test',
          providerId: 'fix_rollback_test',
        }],
        events: [],
      };

      // In SQLite with foreign_keys = ON, inserting a fixture with non-existent foreign keys fails
      await expect(
        matchRepo.persistBatch(batchWithBadForeignKey as any)
      ).rejects.toThrow();

      // Ensure that because the transaction rolled back:
      // The competition and team from that failed batch were NOT orphaned in SQLite!
      const comp = await db.query.competitions.findFirst({
        where: eq(schema.competitions.id, 'comp_rollback_test'),
      });
      const team = await db.query.teams.findFirst({
        where: eq(schema.teams.id, 'team_rollback_test'),
      });
      const fix = await db.query.fixtures.findFirst({
        where: eq(schema.fixtures.id, 'fix_rollback_test'),
      });

      expect(comp).toBeUndefined();
      expect(team).toBeUndefined();
      expect(fix).toBeUndefined();
    });
  });
});
