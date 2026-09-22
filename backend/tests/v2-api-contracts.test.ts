import { describe, it, expect, beforeAll, afterAll, beforeEach, afterEach } from 'vitest';
import { buildServer } from '../src/server.js';
import { FastifyInstance } from 'fastify';
import { pool, db, schema } from '../src/db/index.js';
import { eq, inArray } from 'drizzle-orm';

describe('Checkpoint 4 — Delivery API v2 Canonical Contract & Per-Fixture Freshness', () => {
  let app: FastifyInstance;

  const TEST_COMP_ID = 'comp_v2_test';
  const TEST_COMP_CANONICAL = 'ft_comp_v2_test';
  const TEST_SEASON_ID = 'season_v2_test_2025';
  const TEST_TEAM_HOME_ID = 'team_v2_home';
  const TEST_TEAM_HOME_CANONICAL = 'ft_team_v2_home';
  const TEST_TEAM_AWAY_ID = 'team_v2_away';
  const TEST_TEAM_AWAY_CANONICAL = 'ft_team_v2_away';

  const FIXTURES = {
    liveFresh: {
      id: 'fix_v2_live_fresh',
      canonicalId: 'ft_fix_v2_live_fresh',
      providerId: '999001',
    },
    liveStale: {
      id: 'fix_v2_live_stale',
      canonicalId: 'ft_fix_v2_live_stale',
      providerId: '999002',
    },
    prematch: {
      id: 'fix_v2_prematch',
      canonicalId: 'ft_fix_v2_prematch',
      providerId: '999003',
    },
    finished: {
      id: 'fix_v2_finished',
      canonicalId: 'ft_fix_v2_finished',
      providerId: '999004',
    },
  };

  const cleanupTestRecords = async () => {
    const fixtureIds = Object.values(FIXTURES).map((f) => f.id);
    const canonicalIds = Object.values(FIXTURES).map((f) => f.canonicalId);

    await pool.query(`DELETE FROM events WHERE fixture_id = ANY($1)`, [fixtureIds]);
    await pool.query(`DELETE FROM fixture_provider_aliases WHERE canonical_fixture_id = ANY($1)`, [canonicalIds]);
    await pool.query(`DELETE FROM fixtures WHERE id = ANY($1)`, [fixtureIds]);
    await pool.query(`DELETE FROM teams WHERE id IN ('${TEST_TEAM_HOME_ID}', '${TEST_TEAM_AWAY_ID}')`);
    await pool.query(`DELETE FROM competition_seasons WHERE id = '${TEST_SEASON_ID}'`);
    await pool.query(`DELETE FROM competitions WHERE id = '${TEST_COMP_ID}'`);
  };

  const seedTestData = async () => {
    // 1. Seed competition
    await pool.query(
      `INSERT INTO competitions (id, canonical_id, name, code, type, country, logo_url, created_at, updated_at)
       VALUES ('${TEST_COMP_ID}', '${TEST_COMP_CANONICAL}', 'V2 Test Championship', 'V2TC', 'league', 'England', 'https://example.com/v2.png', NOW(), NOW())
       ON CONFLICT (id) DO NOTHING;`
    );

    // 2. Seed season
    await pool.query(
      `INSERT INTO competition_seasons (id, competition_id, year, display_year, start_date, end_date, is_current)
       VALUES ('${TEST_SEASON_ID}', '${TEST_COMP_ID}', 2025, '2025/2026', '2025-08-01', '2026-05-31', true)
       ON CONFLICT (id) DO NOTHING;`
    );

    // 3. Seed teams
    await pool.query(
      `INSERT INTO teams (id, canonical_id, name, short_name, code, country, created_at, updated_at)
       VALUES 
         ('${TEST_TEAM_HOME_ID}', '${TEST_TEAM_HOME_CANONICAL}', 'V2 Home FC', 'V2H', 'V2H', 'England', NOW(), NOW()),
         ('${TEST_TEAM_AWAY_ID}', '${TEST_TEAM_AWAY_CANONICAL}', 'V2 Away FC', 'V2A', 'V2A', 'England', NOW(), NOW())
       ON CONFLICT (id) DO NOTHING;`
    );

    // 4. Seed fixtures with different provenance states
    const now = new Date();

    // A. Live Fresh (observed 30s ago)
    await pool.query(
      `INSERT INTO fixtures (
        id, canonical_id, competition_id, season_id, home_team_id, away_team_id,
        round, kickoff_time, state, elapsed_minute, home_score, away_score,
        live_source_provider, live_observed_at, last_polled_at, created_at, updated_at
      ) VALUES (
        '${FIXTURES.liveFresh.id}', '${FIXTURES.liveFresh.canonicalId}', '${TEST_COMP_ID}', '${TEST_SEASON_ID}', '${TEST_TEAM_HOME_ID}', '${TEST_TEAM_AWAY_ID}',
        'Round 1', NOW() - INTERVAL '30 minutes', 'LIVE', 30, 1, 0,
        'api_football', NOW() - INTERVAL '30 seconds', NOW() - INTERVAL '30 seconds', NOW(), NOW()
      );`
    );

    // B. Live Stale (observed 300s / 5m ago)
    await pool.query(
      `INSERT INTO fixtures (
        id, canonical_id, competition_id, season_id, home_team_id, away_team_id,
        round, kickoff_time, state, elapsed_minute, home_score, away_score,
        live_source_provider, live_observed_at, last_polled_at, created_at, updated_at
      ) VALUES (
        '${FIXTURES.liveStale.id}', '${FIXTURES.liveStale.canonicalId}', '${TEST_COMP_ID}', '${TEST_SEASON_ID}', '${TEST_TEAM_HOME_ID}', '${TEST_TEAM_AWAY_ID}',
        'Round 1', NOW() - INTERVAL '60 minutes', 'LIVE', 60, 2, 2,
        'api_football', NOW() - INTERVAL '300 seconds', NOW() - INTERVAL '300 seconds', NOW(), NOW()
      );`
    );

    // C. Prematch (schedule observed 1h ago)
    await pool.query(
      `INSERT INTO fixtures (
        id, canonical_id, competition_id, season_id, home_team_id, away_team_id,
        round, kickoff_time, state, elapsed_minute, home_score, away_score,
        schedule_source_provider, schedule_observed_at, last_polled_at, created_at, updated_at
      ) VALUES (
        '${FIXTURES.prematch.id}', '${FIXTURES.prematch.canonicalId}', '${TEST_COMP_ID}', '${TEST_SEASON_ID}', '${TEST_TEAM_HOME_ID}', '${TEST_TEAM_AWAY_ID}',
        'Round 1', NOW() + INTERVAL '2 hours', 'PREMATCH', 0, 0, 0,
        'api_football', NOW() - INTERVAL '1 hour', NOW(), NOW(), NOW()
      );`
    );

    // D. Finished (finalized 1h ago)
    await pool.query(
      `INSERT INTO fixtures (
        id, canonical_id, competition_id, season_id, home_team_id, away_team_id,
        round, kickoff_time, state, elapsed_minute, home_score, away_score,
        live_source_provider, live_observed_at, last_polled_at, created_at, updated_at
      ) VALUES (
        '${FIXTURES.finished.id}', '${FIXTURES.finished.canonicalId}', '${TEST_COMP_ID}', '${TEST_SEASON_ID}', '${TEST_TEAM_HOME_ID}', '${TEST_TEAM_AWAY_ID}',
        'Round 1', NOW() - INTERVAL '3 hours', 'FINISHED', 90, 3, 1,
        'api_football', NOW() - INTERVAL '1 hour', NOW(), NOW(), NOW()
      );`
    );

    // 5. Seed alias mapping legacy providerId to canonical fixture ID
    await pool.query(
      `INSERT INTO fixture_provider_aliases (canonical_fixture_id, provider, provider_id, first_seen_at, last_seen_at)
       VALUES ('${FIXTURES.liveFresh.canonicalId}', 'api_football', '${FIXTURES.liveFresh.providerId}', NOW(), NOW())
       ON CONFLICT (provider, provider_id) DO NOTHING;`
    );
  };

  beforeAll(async () => {
    const dbInfo = await pool.query('SELECT current_database()');
    if (dbInfo.rows[0].current_database !== 'footballtray_test') {
      throw new Error(
        `[SAFETY ERROR] Tests are connected to '${dbInfo.rows[0].current_database}', expected 'footballtray_test'!`
      );
    }
    app = await buildServer();
  });

  beforeEach(async () => {
    await cleanupTestRecords();
    await seedTestData();
  });

  afterEach(async () => {
    await cleanupTestRecords();
  });

  afterAll(async () => {
    await cleanupTestRecords();
    await app.close();
  });

  // =========================================================================
  // Test 1: GET /api/v2/tray envelope structure and schemaVersion
  // =========================================================================
  it('1. GET /api/v2/tray returns envelope with schemaVersion 2.0 and version 2', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/api/v2/tray',
    });

    expect(res.statusCode).toBe(200);
    const body = JSON.parse(res.body);

    expect(body.schemaVersion).toBe('2.0');
    expect(body.version).toBe(2);
    expect(body.requestId).toBeDefined();
    expect(body.serverTime).toBeDefined();
    expect(body.data).toBeDefined();
    expect(Array.isArray(body.data.matches)).toBe(true);
    expect(body.data.matches.length).toBeGreaterThan(0);
  });

  // =========================================================================
  // Test 2: Canonical IDs across all match entities
  // =========================================================================
  it('2. GET /api/v2/tray maps all match, competition, and team IDs to canonical IDs (ft_...)', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/api/v2/tray',
    });

    expect(res.statusCode).toBe(200);
    const body = JSON.parse(res.body);
    const matches: any[] = body.data.matches;

    const testMatch = matches.find((m) => m.legacyId === FIXTURES.liveFresh.id);
    expect(testMatch).toBeDefined();

    // Match ID is canonical
    expect(testMatch.id).toBe(FIXTURES.liveFresh.canonicalId);
    expect(testMatch.id.startsWith('ft_fix_')).toBe(true);
    expect(testMatch.legacyId).toBe(FIXTURES.liveFresh.id);

    // Competition ID is canonical
    expect(testMatch.competition.id).toBe(TEST_COMP_CANONICAL);
    expect(testMatch.competition.id.startsWith('ft_comp_')).toBe(true);

    // Home & Away Team IDs are canonical
    expect(testMatch.homeTeam.id).toBe(TEST_TEAM_HOME_CANONICAL);
    expect(testMatch.homeTeam.id.startsWith('ft_team_')).toBe(true);

    expect(testMatch.awayTeam.id).toBe(TEST_TEAM_AWAY_CANONICAL);
    expect(testMatch.awayTeam.id.startsWith('ft_team_')).toBe(true);
  });

  // =========================================================================
  // Test 3: Per-fixture trust metadata calculation
  // =========================================================================
  it('3. GET /api/v2/tray includes trust metadata (freshnessState, sourceObservedAt, dataAgeSeconds)', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/api/v2/tray',
    });

    expect(res.statusCode).toBe(200);
    const body = JSON.parse(res.body);
    const matches: any[] = body.data.matches;

    // A. Live match polled 30s ago -> FRESH
    const liveFresh = matches.find((m) => m.id === FIXTURES.liveFresh.canonicalId);
    expect(liveFresh).toBeDefined();
    expect(liveFresh.trust).toBeDefined();
    expect(liveFresh.trust.freshnessState).toBe('FRESH');
    expect(liveFresh.trust.sourceObservedAt).not.toBeNull();
    expect(liveFresh.trust.dataAgeSeconds).toBeLessThanOrEqual(120);
    expect(liveFresh.trust.provider).toBe('api_football');

    // B. Live match polled 300s ago -> STALE
    const liveStale = matches.find((m) => m.id === FIXTURES.liveStale.canonicalId);
    expect(liveStale).toBeDefined();
    expect(liveStale.trust).toBeDefined();
    expect(liveStale.trust.freshnessState).toBe('STALE');
    expect(liveStale.trust.dataAgeSeconds).toBeGreaterThan(120);

    // C. Prematch match observed 1h ago -> FRESH
    const prematch = matches.find((m) => m.id === FIXTURES.prematch.canonicalId);
    expect(prematch).toBeDefined();
    expect(prematch.trust).toBeDefined();
    expect(prematch.trust.freshnessState).toBe('FRESH');
    expect(prematch.trust.dataAgeSeconds).toBeLessThanOrEqual(86400);

    // D. Concluded finished match -> FINAL
    const finished = matches.find((m) => m.id === FIXTURES.finished.canonicalId);
    expect(finished).toBeDefined();
    expect(finished.trust).toBeDefined();
    expect(finished.trust.freshnessState).toBe('FINAL');
  });

  // =========================================================================
  // Test 4: ETag caching & 304 Not Modified on /api/v2/tray
  // =========================================================================
  it('4. GET /api/v2/tray supports ETag caching and returns 304 on If-None-Match', async () => {
    const res1 = await app.inject({
      method: 'GET',
      url: '/api/v2/tray',
    });

    expect(res1.statusCode).toBe(200);
    const etag = res1.headers['etag'];
    expect(etag).toBeDefined();

    const res2 = await app.inject({
      method: 'GET',
      url: '/api/v2/tray',
      headers: {
        'if-none-match': etag as string,
      },
    });

    expect(res2.statusCode).toBe(304);
    expect(res2.body).toBe('');
  });

  // =========================================================================
  // Test 5: GET /api/v2/match/:id accepts canonical ID
  // =========================================================================
  it('5. GET /api/v2/match/:id resolves using canonical ID (ft_fix_...)', async () => {
    const res = await app.inject({
      method: 'GET',
      url: `/api/v2/match/${FIXTURES.liveFresh.canonicalId}`,
    });

    expect(res.statusCode).toBe(200);
    const body = JSON.parse(res.body);

    expect(body.schemaVersion).toBe('2.0');
    expect(body.version).toBe(2);
    expect(body.data.id).toBe(FIXTURES.liveFresh.canonicalId);
    expect(body.data.legacyId).toBe(FIXTURES.liveFresh.id);
    expect(body.data.competition.id).toBe(TEST_COMP_CANONICAL);
    expect(body.data.homeTeam.id).toBe(TEST_TEAM_HOME_CANONICAL);
    expect(body.data.awayTeam.id).toBe(TEST_TEAM_AWAY_CANONICAL);
    expect(body.data.trust).toBeDefined();
    expect(body.data.trust.freshnessState).toBe('FRESH');
  });

  // =========================================================================
  // Test 6: GET /api/v2/match/:id resolves using legacy ID & provider aliases
  // =========================================================================
  it('6. GET /api/v2/match/:id resolves using legacy primary ID and fixture_provider_aliases', async () => {
    // A. Via primary key ID (fix_v2_live_fresh)
    const resPrimary = await app.inject({
      method: 'GET',
      url: `/api/v2/match/${FIXTURES.liveFresh.id}`,
    });

    expect(resPrimary.statusCode).toBe(200);
    const bodyPrimary = JSON.parse(resPrimary.body);
    expect(bodyPrimary.data.id).toBe(FIXTURES.liveFresh.canonicalId);

    // B. Via provider alias ID (999001)
    const resAlias = await app.inject({
      method: 'GET',
      url: `/api/v2/match/${FIXTURES.liveFresh.providerId}`,
    });

    expect(resAlias.statusCode).toBe(200);
    const bodyAlias = JSON.parse(resAlias.body);
    expect(bodyAlias.data.id).toBe(FIXTURES.liveFresh.canonicalId);
  });

  // =========================================================================
  // Test 7: GET /api/v2/match/:id returns 404 for unknown match ID
  // =========================================================================
  it('7. GET /api/v2/match/:id returns 404 with MATCH_NOT_FOUND for non-existent match', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/api/v2/match/ft_fix_non_existent',
    });

    expect(res.statusCode).toBe(404);
    const body = JSON.parse(res.body);
    expect(body.schemaVersion).toBe('2.0');
    expect(body.error).toBeDefined();
    expect(body.error.code).toBe('MATCH_NOT_FOUND');
  });
});
