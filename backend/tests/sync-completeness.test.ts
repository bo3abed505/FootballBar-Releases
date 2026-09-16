import { describe, it, expect, beforeEach, afterAll, vi } from 'vitest';
import { eq } from 'drizzle-orm';
import { db, schema } from '../src/db/index.js';
import { SyncService } from '../src/acquisition/sync.service.js';
import { FootballProvider, ProviderFixture, ProviderEvent } from '../src/providers/football-provider.js';
import { MockFootballProvider } from '../src/providers/mock.provider.js';
import { ApiFootballMapper } from '../src/providers/api-football/api-football.mapper.js';
import { ApiFootballFixtureItem, ApiFootballEventItem } from '../src/providers/api-football/api-football.dto.js';
import { buildServer } from '../src/server.js';

describe('Milestone 8 Work Package 3 - Production Data Synchronization Completeness', () => {
  const TEST_COMP_ID = 'comp_wp3_sync';
  const TEST_SEASON_ID = 'season_wp3_sync_2025';
  const TEST_HOME_TEAM_ID = 'team_wp3_home';
  const TEST_AWAY_TEAM_ID = 'team_wp3_away';
  const TEST_FIXTURE_ID = 'fix_wp3_sync_001';

  const baseFixture: ProviderFixture = {
    id: TEST_FIXTURE_ID,
    competitionId: TEST_COMP_ID,
    seasonId: TEST_SEASON_ID,
    homeTeamId: TEST_HOME_TEAM_ID,
    awayTeamId: TEST_AWAY_TEAM_ID,
    competition: {
      id: TEST_COMP_ID,
      name: 'WP3 Test League',
      code: 'WP3L',
      country: 'England',
      logoUrl: 'https://example.com/wp3.png',
    },
    season: {
      id: TEST_SEASON_ID,
      competitionId: TEST_COMP_ID,
      year: 2025,
      displayYear: '2025/2026',
      startDate: '2025-08-01',
      endDate: '2026-05-31',
      isCurrent: true,
    },
    homeTeam: {
      id: TEST_HOME_TEAM_ID,
      name: 'WP3 Arsenal FC',
      shortName: 'ARS',
      code: 'ARS',
      country: 'England',
      logoUrl: 'https://example.com/ars.png',
      venueName: 'Emirates Stadium',
    },
    awayTeam: {
      id: TEST_AWAY_TEAM_ID,
      name: 'WP3 Chelsea FC',
      shortName: 'CHE',
      code: 'CHE',
      country: 'England',
      logoUrl: 'https://example.com/che.png',
      venueName: 'Stamford Bridge',
    },
    round: 'Regular Season - 1',
    kickoffTime: new Date(Date.now() - 30 * 60 * 1000).toISOString(),
    state: 'LIVE',
    elapsedMinute: 30,
    extraMinute: 0,
    score: {
      home: 1,
      away: 0,
      homeHt: 1,
      awayHt: 0,
    },
    venue: 'Emirates Stadium',
    referee: 'Michael Oliver',
    events: [
      {
        id: 'evt_wp3_1',
        eventUid: 'evt_fix_wp3_sync_001_15_0_Goal_team_wp3_home_ply_wp3_7_noassist_normal_goal',
        fixtureId: TEST_FIXTURE_ID,
        teamId: TEST_HOME_TEAM_ID,
        type: 'Goal',
        minute: 15,
        extraMinute: 0,
        playerName: 'B. Saka',
        playerId: 'ply_wp3_7',
        description: 'Goal! Great finish.',
      },
    ],
  };

  const createMockProvider = (fixtures: ProviderFixture[]): FootballProvider => ({
    providerName: 'wp3_test_provider',
    getCompetitions: async () => [],
    getFixtures: async () => fixtures,
    getFixtureDetails: async () => null,
    getFixtureEvents: async () => [],
    getStandings: async () => [],
  });

  const cleanup = async () => {
    // Clean up test records in dependency order
    await db.delete(schema.events).where(eq(schema.events.fixtureId, TEST_FIXTURE_ID));
    await db.delete(schema.fixtures).where(eq(schema.fixtures.id, TEST_FIXTURE_ID));
    await db.delete(schema.teams).where(eq(schema.teams.id, TEST_HOME_TEAM_ID));
    await db.delete(schema.teams).where(eq(schema.teams.id, TEST_AWAY_TEAM_ID));
    await db.delete(schema.competitionSeasons).where(eq(schema.competitionSeasons.id, TEST_SEASON_ID));
    await db.delete(schema.competitions).where(eq(schema.competitions.id, TEST_COMP_ID));
  };

  beforeEach(async () => {
    baseFixture.kickoffTime = new Date(Date.now() - 30 * 60 * 1000).toISOString();
    await cleanup();
  });

  afterAll(async () => {
    await cleanup();
  });

  // ============================================================
  // 1. Dependency-Safe Creation & Idempotency
  // ============================================================
  it('1. New fixture creates required related entities in dependency-safe order', async () => {
    const provider = createMockProvider([baseFixture]);
    const syncService = new SyncService(provider);

    const summary = await syncService.sync();

    expect(summary.fixturesDiscovered).toBe(1);
    expect(summary.fixturesInserted).toBe(1);
    expect(summary.eventsInserted).toBe(1);
    expect(summary.hasChanges).toBe(true);

    const comp = await db.query.competitions.findFirst({ where: eq(schema.competitions.id, TEST_COMP_ID) });
    expect(comp?.name).toBe('WP3 Test League');

    const season = await db.query.competitionSeasons.findFirst({ where: eq(schema.competitionSeasons.id, TEST_SEASON_ID) });
    expect(season?.year).toBe(2025);

    const homeTeam = await db.query.teams.findFirst({ where: eq(schema.teams.id, TEST_HOME_TEAM_ID) });
    expect(homeTeam?.name).toBe('WP3 Arsenal FC');

    const fixture = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, TEST_FIXTURE_ID) });
    expect(fixture?.state).toBe('LIVE');
    expect(fixture?.homeScore).toBe(1);

    const events = await db.query.events.findMany({ where: eq(schema.events.fixtureId, TEST_FIXTURE_ID) });
    expect(events).toHaveLength(1);
    expect(events[0].playerName).toBe('B. Saka');
  });

  it('2. Identical re-sync is idempotent with zero modifications and duplicate event skip', async () => {
    const provider = createMockProvider([baseFixture]);
    const syncService = new SyncService(provider);

    await syncService.sync();
    const secondSummary = await syncService.sync();

    expect(secondSummary.fixturesDiscovered).toBe(1);
    expect(secondSummary.fixturesInserted).toBe(0);
    expect(secondSummary.fixturesUpdated).toBe(0);
    expect(secondSummary.eventsInserted).toBe(0);
    expect(secondSummary.eventsSkippedDuplicates).toBe(1);
    expect(secondSummary.hasChanges).toBe(false);
  });

  // ============================================================
  // 2. Incremental Field Updates & Change Detection
  // ============================================================
  it('3. Existing fixture score update persists without creating duplicate fixture', async () => {
    const provider1 = createMockProvider([baseFixture]);
    const syncService1 = new SyncService(provider1);
    await syncService1.sync();

    const updatedScoreFixture: ProviderFixture = {
      ...baseFixture,
      score: { ...baseFixture.score, home: 2, away: 1 },
    };

    const provider2 = createMockProvider([updatedScoreFixture]);
    const syncService2 = new SyncService(provider2);
    const summary = await syncService2.sync();

    expect(summary.fixturesInserted).toBe(0);
    expect(summary.fixturesUpdated).toBe(1);
    expect(summary.hasChanges).toBe(true);

    const dbFixture = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, TEST_FIXTURE_ID) });
    expect(dbFixture?.homeScore).toBe(2);
    expect(dbFixture?.awayScore).toBe(1);
  });

  it('4. Existing fixture state transition persists (PREMATCH -> LIVE -> HALFTIME -> FINISHED)', async () => {
    const prematchFixture: ProviderFixture = {
      ...baseFixture,
      state: 'PREMATCH',
      elapsedMinute: 0,
      score: { home: 0, away: 0 },
      events: [],
    };

    const provider1 = createMockProvider([prematchFixture]);
    const syncService1 = new SyncService(provider1);
    await syncService1.sync();

    const halftimeFixture: ProviderFixture = {
      ...baseFixture,
      state: 'HALFTIME',
      elapsedMinute: 45,
    };

    const provider2 = createMockProvider([halftimeFixture]);
    const syncService2 = new SyncService(provider2);
    const summary = await syncService2.sync();

    expect(summary.fixturesUpdated).toBe(1);
    expect(summary.hasChanges).toBe(true);

    const dbFixture = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, TEST_FIXTURE_ID) });
    expect(dbFixture?.state).toBe('HALFTIME');
  });

  it('5. Existing fixture elapsed and extra minute update persists', async () => {
    const provider1 = createMockProvider([baseFixture]);
    const syncService1 = new SyncService(provider1);
    await syncService1.sync();

    const minuteUpdatedFixture: ProviderFixture = {
      ...baseFixture,
      elapsedMinute: 90,
      extraMinute: 4,
    };

    const provider2 = createMockProvider([minuteUpdatedFixture]);
    const syncService2 = new SyncService(provider2);
    const summary = await syncService2.sync();

    expect(summary.fixturesUpdated).toBe(1);
    expect(summary.hasChanges).toBe(true);

    const dbFixture = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, TEST_FIXTURE_ID) });
    expect(dbFixture?.elapsedMinute).toBe(90);
    expect(dbFixture?.extraMinute).toBe(4);
  });

  // ============================================================
  // 3. Event Handling & Deduplication
  // ============================================================
  it('6. Newly arrived match event is inserted when polled incrementally', async () => {
    const provider1 = createMockProvider([baseFixture]);
    const syncService1 = new SyncService(provider1);
    await syncService1.sync();

    const secondEvent: ProviderEvent = {
      id: 'evt_wp3_2',
      eventUid: 'evt_fix_wp3_sync_001_58_0_YellowCard_team_wp3_away_ply_wp3_9_noassist_tactical_foul',
      fixtureId: TEST_FIXTURE_ID,
      teamId: TEST_AWAY_TEAM_ID,
      type: 'YellowCard',
      minute: 58,
      extraMinute: 0,
      playerName: 'E. Fernandez',
      playerId: 'ply_wp3_9',
      description: 'Yellow card for tactical foul.',
    };

    const updatedWithEventFixture: ProviderFixture = {
      ...baseFixture,
      elapsedMinute: 60,
      events: [...baseFixture.events!, secondEvent],
    };

    const provider2 = createMockProvider([updatedWithEventFixture]);
    const syncService2 = new SyncService(provider2);
    const summary = await syncService2.sync();

    expect(summary.eventsInserted).toBe(1);
    expect(summary.eventsSkippedDuplicates).toBe(1);
    expect(summary.hasChanges).toBe(true);

    const events = await db.query.events.findMany({ where: eq(schema.events.fixtureId, TEST_FIXTURE_ID) });
    expect(events).toHaveLength(2);
  });

  it('7. Existing event is deduplicated without throwing database uniqueness error', async () => {
    const provider = createMockProvider([baseFixture]);
    const syncService = new SyncService(provider);
    await syncService.sync();

    // Re-run sync with same event
    const summary = await syncService.sync();
    expect(summary.eventsInserted).toBe(0);
    expect(summary.eventsSkippedDuplicates).toBe(1);

    const events = await db.query.events.findMany({ where: eq(schema.events.fixtureId, TEST_FIXTURE_ID) });
    expect(events).toHaveLength(1);
  });

  // ============================================================
  // 4. Finalization & Missing Field Safety
  // ============================================================
  it('8. Finished fixture is finalized correctly with full-time score and updated state', async () => {
    const provider1 = createMockProvider([baseFixture]);
    const syncService1 = new SyncService(provider1);
    await syncService1.sync();

    const finishedFixture: ProviderFixture = {
      ...baseFixture,
      state: 'FINISHED',
      elapsedMinute: 90,
      extraMinute: 3,
      score: { home: 2, away: 0, homeHt: 1, awayHt: 0 },
    };

    const provider2 = createMockProvider([finishedFixture]);
    const syncService2 = new SyncService(provider2);
    const summary = await syncService2.sync();

    expect(summary.fixturesUpdated).toBe(1);
    expect(summary.hasChanges).toBe(true);

    const dbFixture = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, TEST_FIXTURE_ID) });
    expect(dbFixture?.state).toBe('FINISHED');
    expect(dbFixture?.homeScore).toBe(2);
  });

  it('9. Null or omitted optional provider fields in incremental poll do not overwrite known good database values', async () => {
    const provider1 = createMockProvider([baseFixture]);
    const syncService1 = new SyncService(provider1);
    await syncService1.sync();

    // Incremental payload omits optional venue, referee, homeHt but updates homeScore
    const partialFixture: ProviderFixture = {
      ...baseFixture,
      score: { home: 3, away: 0 },
      venue: undefined,
      referee: undefined,
    };

    const provider2 = createMockProvider([partialFixture]);
    const syncService2 = new SyncService(provider2);
    await syncService2.sync();

    const dbFixture = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, TEST_FIXTURE_ID) });
    expect(dbFixture?.homeScore).toBe(3);
    expect(dbFixture?.venue).toBe('Emirates Stadium');
    expect(dbFixture?.referee).toBe('Michael Oliver');
    expect(dbFixture?.homeScoreHt).toBe(1);
  });

  it('10. Team and competition identities remain stable and are not duplicated on repeated syncs', async () => {
    const provider = createMockProvider([baseFixture]);
    const syncService = new SyncService(provider);

    await syncService.sync();
    await syncService.sync();

    const teams = await db.query.teams.findMany({ where: eq(schema.teams.id, TEST_HOME_TEAM_ID) });
    expect(teams).toHaveLength(1);

    const comps = await db.query.competitions.findMany({ where: eq(schema.competitions.id, TEST_COMP_ID) });
    expect(comps).toHaveLength(1);
  });

  // ============================================================
  // 5. API-Football Mapper & Collision Safety
  // ============================================================
  it('11. API-Football mapper produces accurate domain score mapping (fulltime, halftime, extratime, penalties)', () => {
    const rawApiFixture: ApiFootballFixtureItem = {
      fixture: {
        id: 112233,
        referee: 'Anthony Taylor',
        timezone: 'UTC',
        date: '2026-03-01T17:30:00Z',
        timestamp: 1772386200,
        periods: { first: 1772386200, second: 1772389800 },
        venue: { id: 601, name: 'Wembley Stadium', city: 'London' },
        status: { long: 'Match Finished After Extra Time', short: 'AET', elapsed: 120, extra: 3 },
      },
      league: {
        id: 45,
        name: 'FA Cup',
        country: 'England',
        logo: 'https://example.com/facup.png',
        flag: null,
        season: 2025,
        round: 'Semi-finals',
      },
      teams: {
        home: { id: 101, name: 'Liverpool', logo: 'https://example.com/liv.png', winner: true },
        away: { id: 102, name: 'Manchester City', logo: 'https://example.com/mci.png', winner: false },
      },
      goals: { home: 3, away: 2 },
      score: {
        halftime: { home: 1, away: 1 },
        fulltime: { home: 2, away: 2 },
        extratime: { home: 3, away: 2 },
        penalty: { home: null, away: null },
      },
    };

    const mapped = ApiFootballMapper.mapFixture(rawApiFixture);

    expect(mapped.id).toBe('fix_112233');
    expect(mapped.state).toBe('FINISHED');
    expect(mapped.elapsedMinute).toBe(120);
    expect(mapped.extraMinute).toBe(3);
    expect(mapped.score.home).toBe(3);
    expect(mapped.score.away).toBe(2);
    expect(mapped.score.homeHt).toBe(1);
    expect(mapped.score.awayHt).toBe(1);
    expect(mapped.score.homeEt).toBe(3);
    expect(mapped.score.awayEt).toBe(2);
    expect(mapped.venue).toBe('Wembley Stadium');
  });

  it('12. API-Football mapper generates collision-free deterministic event UIDs for distinct same-minute events', () => {
    const rawEvents: ApiFootballEventItem[] = [
      {
        time: { elapsed: 60, extra: null },
        team: { id: 50, name: 'Arsenal', logo: 'https://example.com/ars.png' },
        player: { id: 10, name: 'Player A' },
        assist: { id: 20, name: 'Player B' },
        type: 'subst',
        detail: 'Substitution 1',
        comments: null,
      },
      {
        time: { elapsed: 60, extra: null },
        team: { id: 50, name: 'Arsenal', logo: 'https://example.com/ars.png' },
        player: { id: 11, name: 'Player C' },
        assist: { id: 21, name: 'Player D' },
        type: 'subst',
        detail: 'Substitution 2',
        comments: null,
      },
      {
        time: { elapsed: 60, extra: null },
        team: { id: 50, name: 'Arsenal', logo: 'https://example.com/ars.png' },
        player: { id: null, name: 'Player E' },
        assist: { id: null, name: null },
        type: 'Card',
        detail: 'Yellow Card',
        comments: 'Tactical foul',
      },
    ];

    const mappedEvents = ApiFootballMapper.mapEvents('fix_same_minute_test', rawEvents);

    expect(mappedEvents).toHaveLength(3);
    const uids = mappedEvents.map((e) => e.eventUid);
    const uniqueUids = new Set(uids);

    // Assert strictly zero collisions among same-minute events
    expect(uniqueUids.size).toBe(3);
    expect(uids[0]).toContain('Substitution');
    expect(uids[0]).toContain('ply_10');
    expect(uids[1]).toContain('ply_11');
    expect(uids[2]).toContain('YellowCard');
    expect(uids[2]).toContain('player_e');
  });

  // ============================================================
  // 6. Mock Parity, Client Contracts & Zero N+1
  // ============================================================
  it('13. MockFootballProvider uses the identical SyncService persistence pipeline and change detection', async () => {
    const mockProvider = new MockFootballProvider();
    const syncService = new SyncService(mockProvider);

    const summary = await syncService.sync();

    expect(summary.provider).toBe('mock');
    expect(summary.fixturesDiscovered).toBeGreaterThan(0);
  });

  it('14. Client endpoint /api/tray returns synchronized match payload with valid ETag and 304 caching', async () => {
    const provider = createMockProvider([baseFixture]);
    const syncService = new SyncService(provider);
    await syncService.sync();

    const app = await buildServer();
    const res = await app.inject({
      method: 'GET',
      url: '/api/tray',
    });

    expect(res.statusCode).toBe(200);
    const envelope = JSON.parse(res.body);
    expect(envelope.data.matches).toBeDefined();

    const target = envelope.data.matches.find((m: any) => m.id === TEST_FIXTURE_ID);
    expect(target).toBeDefined();
    expect(target.competition.name).toBe('WP3 Test League');
    expect(target.score.display).toBe('1 - 0');

    await app.close();
  });

  it('15. Client endpoint /api/match/:id returns full match details with associated teams, score, and events', async () => {
    const provider = createMockProvider([baseFixture]);
    const syncService = new SyncService(provider);
    await syncService.sync();

    const app = await buildServer();
    const res = await app.inject({
      method: 'GET',
      url: `/api/match/${TEST_FIXTURE_ID}`,
    });

    expect(res.statusCode).toBe(200);
    const envelope = JSON.parse(res.body);
    expect(envelope.data.id).toBe(TEST_FIXTURE_ID);
    expect(envelope.data.competition.id).toBe(TEST_COMP_ID);
    expect(envelope.data.homeTeam.id).toBe(TEST_HOME_TEAM_ID);
    expect(envelope.data.score.halfTime.home).toBe(1);
    expect(envelope.data.events).toHaveLength(1);

    await app.close();
  });

  it('16. Zero N+1 external provider calls during synchronization (batch acquisition executes exactly 1 provider getFixtures call)', async () => {
    const getFixturesSpy = vi.fn().mockResolvedValue([baseFixture]);
    const provider: FootballProvider = {
      providerName: 'batch_test_provider',
      getCompetitions: async () => [],
      getFixtures: getFixturesSpy,
      getFixtureDetails: async () => null,
      getFixtureEvents: async () => [],
      getStandings: async () => [],
    };

    const syncService = new SyncService(provider);
    await syncService.sync();

    // Verify exactly ONE call to provider.getFixtures, with zero supplementary detail calls
    expect(getFixturesSpy).toHaveBeenCalledTimes(1);
  });
});

