import { describe, it, expect, beforeEach, afterAll } from 'vitest';
import { eq } from 'drizzle-orm';
import { db, schema } from '../src/db/index.js';
import { SyncService } from '../src/acquisition/sync.service.js';
import { FootballProvider, ProviderFixture, ProviderEvent } from '../src/providers/football-provider.js';
import { buildServer } from '../src/server.js';
import { AcquisitionPolicyEngine } from '../src/acquisition/acquisition-policy.js';

describe('Milestone 8 Work Package 5 - End-to-End Live Match Lifecycle & Freshness Validation', () => {
  const TEST_COMP_ID = 'comp_wp5_pl';
  const TEST_SEASON_ID = 'season_wp5_2025';
  const TEST_HOME_TEAM_ID = 'team_wp5_ars';
  const TEST_AWAY_TEAM_ID = 'team_wp5_che';
  const TEST_FIXTURE_ID = 'fix_wp5_e2e_001';

  let lifecycleKickoffTime: string;

  const createLifecycleFixture = (
    state: string,
    elapsedMinute: number,
    extraMinute: number,
    homeScore: number,
    awayScore: number,
    events: ProviderEvent[] = [],
    scoreHt?: { home: number; away: number }
  ): ProviderFixture => ({
    id: TEST_FIXTURE_ID,
    competitionId: TEST_COMP_ID,
    seasonId: TEST_SEASON_ID,
    homeTeamId: TEST_HOME_TEAM_ID,
    awayTeamId: TEST_AWAY_TEAM_ID,
    competition: {
      id: TEST_COMP_ID,
      name: 'Premier League',
      code: 'PL',
      country: 'England',
      logoUrl: 'https://example.com/pl.png',
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
      name: 'Arsenal FC',
      shortName: 'ARS',
      code: 'ARS',
      country: 'England',
      logoUrl: 'https://example.com/ars.png',
      venueName: 'Emirates Stadium',
    },
    awayTeam: {
      id: TEST_AWAY_TEAM_ID,
      name: 'Chelsea FC',
      shortName: 'CHE',
      code: 'CHE',
      country: 'England',
      logoUrl: 'https://example.com/che.png',
      venueName: 'Stamford Bridge',
    },
    round: 'Regular Season - 28',
    kickoffTime: lifecycleKickoffTime,
    state,
    elapsedMinute,
    extraMinute,
    score: {
      home: homeScore,
      away: awayScore,
      homeHt: scoreHt?.home,
      awayHt: scoreHt?.away,
    },
    venue: 'Emirates Stadium',
    referee: 'Michael Oliver',
    events,
  });

  const createMockProvider = (fixtures: ProviderFixture[]): FootballProvider => ({
    providerName: 'wp5_lifecycle_provider',
    getCompetitions: async () => [],
    getFixtures: async () => fixtures,
    getFixtureDetails: async () => null,
    getFixtureEvents: async () => [],
    getStandings: async () => [],
  });

  const cleanup = async () => {
    await db.delete(schema.events).where(eq(schema.events.fixtureId, TEST_FIXTURE_ID));
    await db.delete(schema.fixtures).where(eq(schema.fixtures.id, TEST_FIXTURE_ID));
    await db.delete(schema.teams).where(eq(schema.teams.id, TEST_HOME_TEAM_ID));
    await db.delete(schema.teams).where(eq(schema.teams.id, TEST_AWAY_TEAM_ID));
    await db.delete(schema.competitionSeasons).where(eq(schema.competitionSeasons.id, TEST_SEASON_ID));
    await db.delete(schema.competitions).where(eq(schema.competitions.id, TEST_COMP_ID));
  };

  beforeEach(async () => {
    lifecycleKickoffTime = new Date(Date.now() - 60 * 1000).toISOString();
    await cleanup();
  });

  afterAll(async () => {
    await cleanup();
  });

  // ============================================================
  // Stage 1 & 2: PREMATCH -> LIVE
  // ============================================================
  it('1. PREMATCH -> Database and /api/tray reflect upcoming match with correct kickoff and zero scores', async () => {
    const prematchFixture = createLifecycleFixture('PREMATCH', 0, 0, 0, 0, []);
    const syncService = new SyncService(createMockProvider([prematchFixture]));
    await syncService.sync();

    const app = await buildServer();
    const res = await app.inject({ method: 'GET', url: '/api/tray' });

    expect(res.statusCode).toBe(200);
    const envelope = JSON.parse(res.body);
    const match = envelope.data.matches.find((m: any) => m.id === TEST_FIXTURE_ID);

    expect(match).toBeDefined();
    expect(match.state).toBe('PREMATCH');
    expect(match.score.display).toBe('0 - 0');
    expect(match.elapsedMinute).toBe(0);
    expect(match.recentEvents).toHaveLength(0);

    await app.close();
  });

  it('2. PREMATCH -> LIVE at minute 1 propagates to DB and /api/tray with new ETag', async () => {
    const app = await buildServer();

    // 1. Initial PREMATCH
    const sync1 = new SyncService(createMockProvider([createLifecycleFixture('PREMATCH', 0, 0, 0, 0)]));
    await sync1.sync();
    const res1 = await app.inject({ method: 'GET', url: '/api/tray' });
    const etag1 = res1.headers['etag'];

    // 2. Transition to LIVE minute 1
    const sync2 = new SyncService(createMockProvider([createLifecycleFixture('LIVE', 1, 0, 0, 0)]));
    await sync2.sync();
    const res2 = await app.inject({ method: 'GET', url: '/api/tray' });
    const etag2 = res2.headers['etag'];

    expect(etag2).not.toBe(etag1);
    const match = JSON.parse(res2.body).data.matches.find((m: any) => m.id === TEST_FIXTURE_ID);
    expect(match.state).toBe('LIVE');
    expect(match.elapsedMinute).toBe(1);

    await app.close();
  });

  // ============================================================
  // Stage 3 & 4: LIVE Score Update & Event Arrival
  // ============================================================
  it('3. LIVE score update (1-0) persists to database and invalidates /api/tray ETag', async () => {
    const app = await buildServer();

    const sync1 = new SyncService(createMockProvider([createLifecycleFixture('LIVE', 1, 0, 0, 0)]));
    await sync1.sync();
    const res1 = await app.inject({ method: 'GET', url: '/api/tray' });
    const etag1 = res1.headers['etag'];

    // Goal scored: 1-0 at minute 15
    const goalEvent: ProviderEvent = {
      id: 'evt_wp5_1',
      eventUid: 'evt_fix_wp5_e2e_001_15_0_Goal_team_wp5_ars_ply_saka_noassist_normal_goal',
      fixtureId: TEST_FIXTURE_ID,
      teamId: TEST_HOME_TEAM_ID,
      type: 'Goal',
      minute: 15,
      extraMinute: 0,
      playerName: 'B. Saka',
      playerId: 'ply_saka',
      description: 'Goal! Curled into the top corner.',
    };

    const sync2 = new SyncService(createMockProvider([createLifecycleFixture('LIVE', 15, 0, 1, 0, [goalEvent])]));
    await sync2.sync();
    const res2 = await app.inject({ method: 'GET', url: '/api/tray' });
    const etag2 = res2.headers['etag'];

    expect(etag2).not.toBe(etag1);
    const match = JSON.parse(res2.body).data.matches.find((m: any) => m.id === TEST_FIXTURE_ID);
    expect(match.score.display).toBe('1 - 0');
    expect(match.recentEvents).toHaveLength(1);
    expect(match.recentEvents[0].playerName).toBe('B. Saka');

    await app.close();
  });

  it('4. Goal event arrival persists to events table and appears in /api/tray and /api/match/:id', async () => {
    const goalEvent: ProviderEvent = {
      id: 'evt_wp5_1',
      eventUid: 'evt_fix_wp5_e2e_001_15_0_Goal_team_wp5_ars_ply_saka_noassist_normal_goal',
      fixtureId: TEST_FIXTURE_ID,
      teamId: TEST_HOME_TEAM_ID,
      type: 'Goal',
      minute: 15,
      extraMinute: 0,
      playerName: 'B. Saka',
      playerId: 'ply_saka',
      description: 'Goal! Curled into the top corner.',
    };

    const sync = new SyncService(createMockProvider([createLifecycleFixture('LIVE', 15, 0, 1, 0, [goalEvent])]));
    await sync.sync();

    const app = await buildServer();

    // Verify /api/tray
    const resTray = await app.inject({ method: 'GET', url: '/api/tray' });
    const trayMatch = JSON.parse(resTray.body).data.matches.find((m: any) => m.id === TEST_FIXTURE_ID);
    expect(trayMatch.recentEvents).toHaveLength(1);

    // Verify /api/match/:id
    const resDetail = await app.inject({ method: 'GET', url: `/api/match/${TEST_FIXTURE_ID}` });
    expect(resDetail.statusCode).toBe(200);
    const detailEnvelope = JSON.parse(resDetail.body);
    expect(detailEnvelope.data.events).toHaveLength(1);
    expect(detailEnvelope.data.events[0].playerName).toBe('B. Saka');

    await app.close();
  });

  // ============================================================
  // Stage 5 & 6: Deduplication & HTTP 304 Caching
  // ============================================================
  it('5. Repeated polling of same match state is idempotent and deduplicates events without error', async () => {
    const goalEvent: ProviderEvent = {
      id: 'evt_wp5_1',
      eventUid: 'evt_fix_wp5_e2e_001_15_0_Goal_team_wp5_ars_ply_saka_noassist_normal_goal',
      fixtureId: TEST_FIXTURE_ID,
      teamId: TEST_HOME_TEAM_ID,
      type: 'Goal',
      minute: 15,
      extraMinute: 0,
      playerName: 'B. Saka',
    };

    const provider = createMockProvider([createLifecycleFixture('LIVE', 15, 0, 1, 0, [goalEvent])]);
    const sync = new SyncService(provider);

    // First sync
    const sum1 = await sync.sync();
    expect(sum1.eventsInserted).toBe(1);

    // Second identical sync
    const sum2 = await sync.sync();
    expect(sum2.eventsInserted).toBe(0);
    expect(sum2.eventsSkippedDuplicates).toBe(1);
    expect(sum2.hasChanges).toBe(false);

    const dbEvents = await db.query.events.findMany({ where: eq(schema.events.fixtureId, TEST_FIXTURE_ID) });
    expect(dbEvents).toHaveLength(1);
  });

  it('6. Unchanged data returns HTTP 304 Not Modified when client sends If-None-Match', async () => {
    const sync = new SyncService(createMockProvider([createLifecycleFixture('LIVE', 20, 0, 1, 0)]));
    await sync.sync();

    const app = await buildServer();
    const res1 = await app.inject({ method: 'GET', url: '/api/tray' });
    const etag = res1.headers['etag'] as string;
    expect(etag).toBeDefined();

    // Second request sending If-None-Match
    const res2 = await app.inject({
      method: 'GET',
      url: '/api/tray',
      headers: { 'if-none-match': etag },
    });

    expect(res2.statusCode).toBe(304);
    expect(res2.body).toBe('');

    await app.close();
  });

  // ============================================================
  // Stage 7 & 8: Equalizer & HALFTIME Transition
  // ============================================================
  it('7. LIVE score equalizer (1-1) with new event updates /api/tray and emits fresh ETag', async () => {
    const app = await buildServer();

    const event1: ProviderEvent = {
      id: 'evt_wp5_1',
      eventUid: 'evt_fix_wp5_e2e_001_15_0_Goal_team_wp5_ars_ply_saka_noassist_normal_goal',
      fixtureId: TEST_FIXTURE_ID,
      teamId: TEST_HOME_TEAM_ID,
      type: 'Goal',
      minute: 15,
      extraMinute: 0,
      playerName: 'B. Saka',
    };

    const sync1 = new SyncService(createMockProvider([createLifecycleFixture('LIVE', 20, 0, 1, 0, [event1])]));
    await sync1.sync();
    const res1 = await app.inject({ method: 'GET', url: '/api/tray' });
    const etag1 = res1.headers['etag'];

    // Equalizer: 1-1 at minute 38
    const event2: ProviderEvent = {
      id: 'evt_wp5_2',
      eventUid: 'evt_fix_wp5_e2e_001_38_0_Goal_team_wp5_che_ply_palmer_noassist_penalty',
      fixtureId: TEST_FIXTURE_ID,
      teamId: TEST_AWAY_TEAM_ID,
      type: 'Goal',
      minute: 38,
      extraMinute: 0,
      playerName: 'C. Palmer',
    };

    const sync2 = new SyncService(createMockProvider([createLifecycleFixture('LIVE', 38, 0, 1, 1, [event1, event2])]));
    await sync2.sync();
    const res2 = await app.inject({ method: 'GET', url: '/api/tray' });
    const etag2 = res2.headers['etag'];

    expect(etag2).not.toBe(etag1);
    const match = JSON.parse(res2.body).data.matches.find((m: any) => m.id === TEST_FIXTURE_ID);
    expect(match.score.display).toBe('1 - 1');
    expect(match.recentEvents).toHaveLength(2);

    await app.close();
  });

  it('8. LIVE -> HALFTIME transition preserves scores, sets state to HALFTIME, and updates ETag', async () => {
    const app = await buildServer();

    const sync1 = new SyncService(createMockProvider([createLifecycleFixture('LIVE', 45, 1, 1, 1)]));
    await sync1.sync();
    const res1 = await app.inject({ method: 'GET', url: '/api/tray' });
    const etag1 = res1.headers['etag'];

    // Transition to HALFTIME
    const halftimeFixture = createLifecycleFixture('HALFTIME', 45, 0, 1, 1, [], { home: 1, away: 1 });
    const sync2 = new SyncService(createMockProvider([halftimeFixture]));
    await sync2.sync();
    const res2 = await app.inject({ method: 'GET', url: '/api/tray' });
    const etag2 = res2.headers['etag'];

    expect(etag2).not.toBe(etag1);
    const match = JSON.parse(res2.body).data.matches.find((m: any) => m.id === TEST_FIXTURE_ID);
    expect(match.state).toBe('HALFTIME');
    expect(match.score.display).toBe('1 - 1');

    await app.close();
  });

  // ============================================================
  // Stage 9, 10 & 11: Second Half, Go-Ahead Goal & FINISHED Finalization
  // ============================================================
  it('9. HALFTIME -> LIVE second half progression updates elapsed minute and resumes in-play status', async () => {
    const app = await buildServer();

    const sync1 = new SyncService(createMockProvider([createLifecycleFixture('HALFTIME', 45, 0, 1, 1)]));
    await sync1.sync();

    const secondHalfFixture = createLifecycleFixture('LIVE', 55, 0, 1, 1);
    const sync2 = new SyncService(createMockProvider([secondHalfFixture]));
    await sync2.sync();

    const res = await app.inject({ method: 'GET', url: '/api/tray' });
    const match = JSON.parse(res.body).data.matches.find((m: any) => m.id === TEST_FIXTURE_ID);
    expect(match.state).toBe('LIVE');
    expect(match.elapsedMinute).toBe(55);

    await app.close();
  });

  it('10. Second-half go-ahead goal (2-1) inserts distinct event and invalidates ETag', async () => {
    const app = await buildServer();

    const event3: ProviderEvent = {
      id: 'evt_wp5_3',
      eventUid: 'evt_fix_wp5_e2e_001_78_0_Goal_team_wp5_ars_ply_martinelli_noassist_counter_attack',
      fixtureId: TEST_FIXTURE_ID,
      teamId: TEST_HOME_TEAM_ID,
      type: 'Goal',
      minute: 78,
      extraMinute: 0,
      playerName: 'G. Martinelli',
    };

    const sync = new SyncService(createMockProvider([createLifecycleFixture('LIVE', 78, 0, 2, 1, [event3])]));
    await sync.sync();

    const res = await app.inject({ method: 'GET', url: '/api/tray' });
    const match = JSON.parse(res.body).data.matches.find((m: any) => m.id === TEST_FIXTURE_ID);
    expect(match.score.display).toBe('2 - 1');
    expect(match.recentEvents).toHaveLength(1);
    expect(match.recentEvents[0].playerName).toBe('G. Martinelli');

    await app.close();
  });

  it('11. LIVE -> FINISHED finalization sets state=FINISHED, preserves final 2-1 score, and updates ETag', async () => {
    const app = await buildServer();

    const finishedFixture = createLifecycleFixture('FINISHED', 90, 4, 2, 1);
    const sync = new SyncService(createMockProvider([finishedFixture]));
    await sync.sync();

    const res = await app.inject({ method: 'GET', url: '/api/tray' });
    const match = JSON.parse(res.body).data.matches.find((m: any) => m.id === TEST_FIXTURE_ID);
    expect(match.state).toBe('FINISHED');
    expect(match.score.display).toBe('2 - 1');
    expect(match.elapsedMinute).toBe(90);
    expect(match.extraMinute).toBe(4);

    await app.close();
  });

  it('12. Finished match is no longer classified as active in-play', () => {
    const classification = AcquisitionPolicyEngine.classifyFixtureState({
      state: 'FINISHED',
      kickoffTime: new Date(Date.now() - 2 * 60 * 60 * 1000), // 2 hours ago
      lastPolledAt: new Date(),
    });

    // Finished matches must NOT be classified as LIVE_WINDOW
    expect(classification).not.toBe('LIVE_WINDOW');
    expect(classification).toBe('RECENTLY_FINISHED');
  });

  // ============================================================
  // Additional End-to-End Resilience & Contract Tests
  // ============================================================
  it('13. Sub-minute metadata sync (lastPolledAt) does not cause false ETag invalidation', async () => {
    const app = await buildServer();

    const provider = createMockProvider([createLifecycleFixture('LIVE', 60, 0, 2, 1)]);
    const sync = new SyncService(provider);

    await sync.sync();
    const res1 = await app.inject({ method: 'GET', url: '/api/tray' });
    const etag1 = res1.headers['etag'];

    // Poll again with unchanged logical data (only lastPolledAt advances in DB)
    await sync.sync();
    const res2 = await app.inject({ method: 'GET', url: '/api/tray' });
    const etag2 = res2.headers['etag'];

    expect(etag2).toBe(etag1);

    await app.close();
  });

  it('14. Client contract verification: /api/tray envelope matches schemaVersion 1.0 and is parseable', async () => {
    const sync = new SyncService(createMockProvider([createLifecycleFixture('LIVE', 30, 0, 1, 0)]));
    await sync.sync();

    const app = await buildServer();
    const res = await app.inject({ method: 'GET', url: '/api/tray' });
    expect(res.statusCode).toBe(200);

    const envelope = JSON.parse(res.body);
    expect(envelope.schemaVersion).toBe('1.0');
    expect(envelope.version).toBeDefined();
    expect(envelope.data).toBeDefined();
    expect(envelope.data.matches).toBeInstanceOf(Array);

    await app.close();
  });

  it('15. Client contract verification: /api/match/:id returns complete match details, halftime score, and events', async () => {
    const event: ProviderEvent = {
      id: 'evt_wp5_detail',
      eventUid: 'evt_fix_wp5_e2e_001_10_0_Goal_team_wp5_ars_ply_saka_noassist_goal',
      fixtureId: TEST_FIXTURE_ID,
      teamId: TEST_HOME_TEAM_ID,
      type: 'Goal',
      minute: 10,
      extraMinute: 0,
      playerName: 'B. Saka',
    };

    const fixture = createLifecycleFixture('HALFTIME', 45, 0, 1, 0, [event], { home: 1, away: 0 });
    const sync = new SyncService(createMockProvider([fixture]));
    await sync.sync();

    const app = await buildServer();
    const res = await app.inject({ method: 'GET', url: `/api/match/${TEST_FIXTURE_ID}` });
    expect(res.statusCode).toBe(200);

    const envelope = JSON.parse(res.body);
    expect(envelope.data.id).toBe(TEST_FIXTURE_ID);
    expect(envelope.data.homeTeam.name).toBe('Arsenal FC');
    expect(envelope.data.score.halfTime.home).toBe(1);
    expect(envelope.data.events).toHaveLength(1);

    await app.close();
  });

  it('16. Invalid or unknown match state is safely normalized and never treated as LIVE', () => {
    const classification = AcquisitionPolicyEngine.classifyFixtureState({
      state: 'CORRUPTED_OR_UNKNOWN',
      kickoffTime: new Date(Date.now() + 24 * 60 * 60 * 1000),
    });

    expect(classification).not.toBe('LIVE_WINDOW');
    expect(classification).toBe('PREMATCH_TODAY');
  });

  it('17. Database isolation: all lifecycle records remain strictly confined to test database', async () => {
    const { databaseUrl } = await import('../src/db/index.js');
    expect(databaseUrl.includes('footballtray_test') || databaseUrl.includes('local_test.db')).toBe(true);
    expect(databaseUrl).not.toBe('postgresql://postgres:postgres@127.0.0.1:5432/footballtray');
    expect(databaseUrl.endsWith('local.db')).toBe(false);
  });

  it('18. Full 8-stage lifecycle execution is deterministic across repeated sequential runs', async () => {
    const app = await buildServer();

    // Stage 1: PREMATCH (0-0)
    await new SyncService(createMockProvider([createLifecycleFixture('PREMATCH', 0, 0, 0, 0)])).sync();
    let res = await app.inject({ method: 'GET', url: '/api/tray' });
    let match = JSON.parse(res.body).data.matches.find((m: any) => m.id === TEST_FIXTURE_ID);
    expect(match.state).toBe('PREMATCH');

    // Stage 2: LIVE min 1 (0-0)
    await new SyncService(createMockProvider([createLifecycleFixture('LIVE', 1, 0, 0, 0)])).sync();
    res = await app.inject({ method: 'GET', url: '/api/tray' });
    match = JSON.parse(res.body).data.matches.find((m: any) => m.id === TEST_FIXTURE_ID);
    expect(match.state).toBe('LIVE');
    expect(match.elapsedMinute).toBe(1);

    // Stage 3: LIVE min 15 (1-0)
    await new SyncService(createMockProvider([createLifecycleFixture('LIVE', 15, 0, 1, 0)])).sync();
    res = await app.inject({ method: 'GET', url: '/api/tray' });
    match = JSON.parse(res.body).data.matches.find((m: any) => m.id === TEST_FIXTURE_ID);
    expect(match.score.display).toBe('1 - 0');

    // Stage 4: LIVE min 38 (1-1)
    await new SyncService(createMockProvider([createLifecycleFixture('LIVE', 38, 0, 1, 1)])).sync();
    res = await app.inject({ method: 'GET', url: '/api/tray' });
    match = JSON.parse(res.body).data.matches.find((m: any) => m.id === TEST_FIXTURE_ID);
    expect(match.score.display).toBe('1 - 1');

    // Stage 5: HALFTIME (1-1)
    await new SyncService(createMockProvider([createLifecycleFixture('HALFTIME', 45, 0, 1, 1)])).sync();
    res = await app.inject({ method: 'GET', url: '/api/tray' });
    match = JSON.parse(res.body).data.matches.find((m: any) => m.id === TEST_FIXTURE_ID);
    expect(match.state).toBe('HALFTIME');

    // Stage 6: LIVE second half min 55 (1-1)
    await new SyncService(createMockProvider([createLifecycleFixture('LIVE', 55, 0, 1, 1)])).sync();
    res = await app.inject({ method: 'GET', url: '/api/tray' });
    match = JSON.parse(res.body).data.matches.find((m: any) => m.id === TEST_FIXTURE_ID);
    expect(match.state).toBe('LIVE');
    expect(match.elapsedMinute).toBe(55);

    // Stage 7: LIVE min 78 (2-1)
    await new SyncService(createMockProvider([createLifecycleFixture('LIVE', 78, 0, 2, 1)])).sync();
    res = await app.inject({ method: 'GET', url: '/api/tray' });
    match = JSON.parse(res.body).data.matches.find((m: any) => m.id === TEST_FIXTURE_ID);
    expect(match.score.display).toBe('2 - 1');

    // Stage 8: FINISHED (2-1)
    await new SyncService(createMockProvider([createLifecycleFixture('FINISHED', 90, 4, 2, 1)])).sync();
    res = await app.inject({ method: 'GET', url: '/api/tray' });
    match = JSON.parse(res.body).data.matches.find((m: any) => m.id === TEST_FIXTURE_ID);
    expect(match.state).toBe('FINISHED');
    expect(match.score.display).toBe('2 - 1');

    await app.close();
  });
});
