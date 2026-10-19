import { describe, it, expect, beforeAll, afterAll, beforeEach, afterEach } from 'vitest';
import { buildServer } from '../src/server.js';
import { FastifyInstance } from 'fastify';
import { db, schema } from '../src/db/index.js';
import { inArray, eq } from 'drizzle-orm';

describe('Phase 8 — Client API Consumption & UI Data Binding (/api/v2/tray/live & /api/v2/matches/:id)', () => {
  let app: FastifyInstance;

  const TEST_COMP_ID = 'comp_live_tray_test';
  const TEST_SEASON_ID = 'season_live_tray_2026';
  const TEST_TEAM_1 = 'team_live_tray_1';
  const TEST_TEAM_2 = 'team_live_tray_2';
  const TEST_TEAM_3 = 'team_live_tray_3';
  const TEST_TEAM_4 = 'team_live_tray_4';

  const FIXTURE_1 = 'fix_live_tray_101';
  const FIXTURE_2 = 'fix_live_tray_102';
  const FIXTURE_3 = 'fix_live_tray_103';

  const allFixtureIds = [FIXTURE_1, FIXTURE_2, FIXTURE_3];
  const allTeamIds = [TEST_TEAM_1, TEST_TEAM_2, TEST_TEAM_3, TEST_TEAM_4];

  const cleanup = async () => {
    await db.delete(schema.events).where(inArray(schema.events.fixtureId, allFixtureIds));
    await db.delete(schema.fixtureProviderAliases).where(inArray(schema.fixtureProviderAliases.providerId, allFixtureIds));
    await db.delete(schema.fixtures).where(inArray(schema.fixtures.id, allFixtureIds));
    await db.delete(schema.teamProviderAliases).where(inArray(schema.teamProviderAliases.providerId, allTeamIds));
    await db.delete(schema.teams).where(inArray(schema.teams.id, allTeamIds));
    await db.delete(schema.competitionSeasons).where(eq(schema.competitionSeasons.id, TEST_SEASON_ID));
    await db.delete(schema.competitions).where(eq(schema.competitions.id, TEST_COMP_ID));
  };

  beforeAll(async () => {
    app = await buildServer();
    await app.ready();
    await cleanup();
  });

  afterAll(async () => {
    await cleanup();
    await app.close();
  });

  beforeEach(async () => {
    await cleanup();

    // 1. Seed competition & season
    await db.insert(schema.competitions).values({
      id: TEST_COMP_ID,
      canonicalId: `ft_${TEST_COMP_ID}`,
      name: 'Live Tray Test League',
      code: 'LTTL',
      type: 'LEAGUE',
      country: 'Testland',
    }).onConflictDoNothing();

    await db.insert(schema.competitionSeasons).values({
      id: TEST_SEASON_ID,
      competitionId: TEST_COMP_ID,
      year: 2026,
      displayYear: '2025/2026',
      startDate: new Date('2025-08-01'),
      endDate: new Date('2026-05-31'),
      isCurrent: true,
    }).onConflictDoNothing();

    // 2. Seed teams
    for (const tid of allTeamIds) {
      await db.insert(schema.teams).values({
        id: tid,
        canonicalId: `ft_${tid}`,
        name: `Team ${tid}`,
        shortName: tid.slice(-4),
      }).onConflictDoNothing();
    }
  });

  afterEach(async () => {
    await cleanup();
  });

  it('GET /api/v2/tray/live returns 200 OK with deterministic ETag header on cold request', async () => {
    const fixedNow = new Date();
    await db.insert(schema.fixtures).values({
      id: FIXTURE_1,
      canonicalId: `ft_${FIXTURE_1}`,
      competitionId: TEST_COMP_ID,
      seasonId: TEST_SEASON_ID,
      homeTeamId: TEST_TEAM_1,
      awayTeamId: TEST_TEAM_2,
      state: 'LIVE',
      elapsedMinute: 23,
      homeScore: 1,
      awayScore: 0,
      kickoffTime: new Date(fixedNow.getTime() - 25 * 60 * 1000),
      updatedAt: fixedNow,
    });

    const res = await app.inject({
      method: 'GET',
      url: '/api/v2/tray/live',
    });

    expect(res.statusCode).toBe(200);
    const body = JSON.parse(res.payload);
    expect(body.schemaVersion).toBe('2.0');
    expect(body.data).toBeDefined();
    expect(body.data.matches.length).toBeGreaterThanOrEqual(1);

    const found = body.data.matches.find((m: any) => m.id === `ft_${FIXTURE_1}` || m.legacyId === FIXTURE_1);
    expect(found).toBeDefined();
    expect(found.state).toBe('LIVE');
    expect(found.score.home).toBe(1);

    // ETag header must be present and formatted as weak ETag W/"live-..."
    const etag = res.headers['etag'];
    expect(etag).toBeDefined();
    expect(etag).toMatch(/^W\/"live-\d+-[a-f0-9]{8}-[a-f0-9]{8}"$/);
  });

  it('GET /api/v2/tray/live returns 304 Not Modified when If-None-Match matches', async () => {
    const fixedNow = new Date();
    await db.insert(schema.fixtures).values({
      id: FIXTURE_1,
      canonicalId: `ft_${FIXTURE_1}`,
      competitionId: TEST_COMP_ID,
      seasonId: TEST_SEASON_ID,
      homeTeamId: TEST_TEAM_1,
      awayTeamId: TEST_TEAM_2,
      state: 'LIVE',
      elapsedMinute: 35,
      homeScore: 1,
      awayScore: 1,
      kickoffTime: new Date(fixedNow.getTime() - 40 * 60 * 1000),
      updatedAt: fixedNow,
    });

    // Initial poll
    const res1 = await app.inject({
      method: 'GET',
      url: '/api/v2/tray/live',
    });
    expect(res1.statusCode).toBe(200);
    const etag = res1.headers['etag'];
    expect(etag).toBeDefined();

    // Re-poll with exact ETag
    const res2 = await app.inject({
      method: 'GET',
      url: '/api/v2/tray/live',
      headers: {
        'if-none-match': etag as string,
      },
    });

    expect(res2.statusCode).toBe(304);
    expect(res2.payload).toBe('');
    expect(res2.headers['etag']).toBe(etag);
  });

  it('Guarantees ETag collision avoidance on match removal via idsHash caveat defense', async () => {
    const fixedTimestamp = new Date('2026-10-13T12:00:00.000Z');

    // Seed 2 matches with exact same updatedAt timestamp
    await db.insert(schema.fixtures).values([
      {
        id: FIXTURE_1,
        canonicalId: `ft_${FIXTURE_1}`,
        competitionId: TEST_COMP_ID,
        seasonId: TEST_SEASON_ID,
        homeTeamId: TEST_TEAM_1,
        awayTeamId: TEST_TEAM_2,
        state: 'LIVE',
        elapsedMinute: 10,
        kickoffTime: new Date(),
        updatedAt: fixedTimestamp,
      },
      {
        id: FIXTURE_2,
        canonicalId: `ft_${FIXTURE_2}`,
        competitionId: TEST_COMP_ID,
        seasonId: TEST_SEASON_ID,
        homeTeamId: TEST_TEAM_3,
        awayTeamId: TEST_TEAM_4,
        state: 'LIVE',
        elapsedMinute: 15,
        kickoffTime: new Date(),
        updatedAt: fixedTimestamp,
      },
    ]);

    // Poll 1: Get ETag with FIXTURE_1 and FIXTURE_2
    const res1 = await app.inject({
      method: 'GET',
      url: '/api/v2/tray/live',
    });
    expect(res1.statusCode).toBe(200);
    const etag1 = res1.headers['etag'] as string;

    // Mutate composition: remove FIXTURE_2 and add FIXTURE_3 with exact same timestamp
    await db.delete(schema.fixtures).where(eq(schema.fixtures.id, FIXTURE_2));
    await db.insert(schema.fixtures).values({
      id: FIXTURE_3,
      canonicalId: `ft_${FIXTURE_3}`,
      competitionId: TEST_COMP_ID,
      seasonId: TEST_SEASON_ID,
      homeTeamId: TEST_TEAM_3,
      awayTeamId: TEST_TEAM_4,
      state: 'LIVE',
      elapsedMinute: 20,
      kickoffTime: new Date(),
      updatedAt: fixedTimestamp, // Exactly identical timestamp
    });

    // Poll 2 with previous ETag: Even though match count is 2 and maxUpdatedAt is identical,
    // idsHash changes, so the server MUST return 200 OK (NOT 304)!
    const res2 = await app.inject({
      method: 'GET',
      url: '/api/v2/tray/live',
      headers: {
        'if-none-match': etag1,
      },
    });

    expect(res2.statusCode).toBe(200);
    const etag2 = res2.headers['etag'] as string;
    expect(etag2).not.toBe(etag1);

    const body2 = JSON.parse(res2.payload);
    const ids = body2.data.matches.map((m: any) => m.id);
    expect(ids).toContain(`ft_${FIXTURE_3}`);
    expect(ids).not.toContain(`ft_${FIXTURE_2}`);
  });

  it('GET /api/v2/matches/:id returns detailed fixture with ETag and 304 on re-poll', async () => {
    const fixedNow = new Date();
    await db.insert(schema.fixtures).values({
      id: FIXTURE_1,
      canonicalId: `ft_${FIXTURE_1}`,
      competitionId: TEST_COMP_ID,
      seasonId: TEST_SEASON_ID,
      homeTeamId: TEST_TEAM_1,
      awayTeamId: TEST_TEAM_2,
      state: 'LIVE',
      elapsedMinute: 50,
      homeScore: 2,
      awayScore: 1,
      kickoffTime: new Date(fixedNow.getTime() - 60 * 60 * 1000),
      updatedAt: fixedNow,
    });

    // Cold detail request
    const res1 = await app.inject({
      method: 'GET',
      url: `/api/v2/matches/${FIXTURE_1}`,
    });

    expect(res1.statusCode).toBe(200);
    const body1 = JSON.parse(res1.payload);
    expect(body1.schemaVersion).toBe('2.0');
    expect(body1.data.id).toBe(`ft_${FIXTURE_1}`);
    expect(body1.data.legacyId).toBe(FIXTURE_1);
    expect(body1.data.score.home).toBe(2);

    const etag = res1.headers['etag'] as string;
    expect(etag).toBeDefined();
    expect(etag).toMatch(/^W\/"match-.*"$/);

    // Re-poll with If-None-Match
    const res2 = await app.inject({
      method: 'GET',
      url: `/api/v2/matches/${FIXTURE_1}`,
      headers: {
        'if-none-match': etag,
      },
    });

    expect(res2.statusCode).toBe(304);
    expect(res2.payload).toBe('');

    // Also verify backward compatible singular alias /api/v2/match/:id
    const resAlias = await app.inject({
      method: 'GET',
      url: `/api/v2/match/${FIXTURE_1}`,
      headers: {
        'if-none-match': etag,
      },
    });

    expect(resAlias.statusCode).toBe(304);
  });

  it('GET /api/v2/matches/:id returns 404 for unknown match', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/api/v2/matches/non_existent_id',
    });

    expect(res.statusCode).toBe(404);
    const body = JSON.parse(res.payload);
    expect(body.error?.code).toBe('MATCH_NOT_FOUND');
  });
});
