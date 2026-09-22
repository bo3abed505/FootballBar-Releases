import { describe, it, expect, beforeAll, afterAll, beforeEach, afterEach } from 'vitest';
import { buildServer } from '../src/server.js';
import { FastifyInstance } from 'fastify';
import { pool } from '../src/db/index.js';

describe('Checkpoint 4 — v1 API Backward-Compatibility & Regression Isolation', () => {
  let app: FastifyInstance;

  const TEST_COMP_ID = 'comp_v1_compat';
  const TEST_COMP_CANONICAL = 'ft_comp_v1_compat';
  const TEST_SEASON_ID = 'season_v1_compat_2025';
  const TEST_TEAM_HOME_ID = 'team_v1_home';
  const TEST_TEAM_HOME_CANONICAL = 'ft_team_v1_home';
  const TEST_TEAM_AWAY_ID = 'team_v1_away';
  const TEST_TEAM_AWAY_CANONICAL = 'ft_team_v1_away';
  const TEST_FIXTURE_ID = 'fix_v1_compat_match';
  const TEST_FIXTURE_CANONICAL = 'ft_fix_v1_compat_match';

  const cleanupTestRecords = async () => {
    await pool.query(`DELETE FROM events WHERE fixture_id = '${TEST_FIXTURE_ID}'`);
    await pool.query(`DELETE FROM fixture_provider_aliases WHERE canonical_fixture_id = '${TEST_FIXTURE_CANONICAL}'`);
    await pool.query(`DELETE FROM fixtures WHERE id = '${TEST_FIXTURE_ID}'`);
    await pool.query(`DELETE FROM teams WHERE id IN ('${TEST_TEAM_HOME_ID}', '${TEST_TEAM_AWAY_ID}')`);
    await pool.query(`DELETE FROM competition_seasons WHERE id = '${TEST_SEASON_ID}'`);
    await pool.query(`DELETE FROM competitions WHERE id = '${TEST_COMP_ID}'`);
  };

  const seedTestData = async () => {
    await pool.query(
      `INSERT INTO competitions (id, canonical_id, name, code, type, country, logo_url, created_at, updated_at)
       VALUES ('${TEST_COMP_ID}', '${TEST_COMP_CANONICAL}', 'V1 Compatibility League', 'V1CL', 'league', 'England', 'https://example.com/v1.png', NOW(), NOW())
       ON CONFLICT (id) DO NOTHING;`
    );

    await pool.query(
      `INSERT INTO competition_seasons (id, competition_id, year, display_year, start_date, end_date, is_current)
       VALUES ('${TEST_SEASON_ID}', '${TEST_COMP_ID}', 2025, '2025/2026', '2025-08-01', '2026-05-31', true)
       ON CONFLICT (id) DO NOTHING;`
    );

    await pool.query(
      `INSERT INTO teams (id, canonical_id, name, short_name, code, country, created_at, updated_at)
       VALUES 
         ('${TEST_TEAM_HOME_ID}', '${TEST_TEAM_HOME_CANONICAL}', 'V1 Home FC', 'HOM', 'HOM', 'England', NOW(), NOW()),
         ('${TEST_TEAM_AWAY_ID}', '${TEST_TEAM_AWAY_CANONICAL}', 'V1 Away FC', 'AWY', 'AWY', 'England', NOW(), NOW())
       ON CONFLICT (id) DO NOTHING;`
    );

    await pool.query(
      `INSERT INTO fixtures (
        id, canonical_id, competition_id, season_id, home_team_id, away_team_id,
        round, kickoff_time, state, elapsed_minute, home_score, away_score,
        live_source_provider, live_observed_at, schedule_source_provider, schedule_observed_at,
        last_polled_at, created_at, updated_at
      ) VALUES (
        '${TEST_FIXTURE_ID}', '${TEST_FIXTURE_CANONICAL}', '${TEST_COMP_ID}', '${TEST_SEASON_ID}', '${TEST_TEAM_HOME_ID}', '${TEST_TEAM_AWAY_ID}',
        'Round 1', NOW() - INTERVAL '45 minutes', 'LIVE', 45, 1, 0,
        'api_football', NOW() - INTERVAL '20 seconds', 'api_football', NOW() - INTERVAL '5 hours',
        NOW() - INTERVAL '20 seconds', NOW(), NOW()
      );`
    );

    await pool.query(
      `INSERT INTO events (id, event_uid, fixture_id, team_id, type, minute, extra_minute, player_name, description)
       VALUES ('evt_v1_goal', 'evt_v1_goal_uid', '${TEST_FIXTURE_ID}', '${TEST_TEAM_HOME_ID}', 'Goal', 22, 0, 'J. Striker', 'Goal!')
       ON CONFLICT (id) DO NOTHING;`
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
  // Test 1: GET /api/tray retains 100% v1 schema structure
  // =========================================================================
  it('1. GET /api/tray retains exact schemaVersion 1.0, version 1, and legacy ID keys', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/api/tray',
    });

    expect(res.statusCode).toBe(200);
    const body = JSON.parse(res.body);

    // Envelope assertions
    expect(body.schemaVersion).toBe('1.0');
    expect(body.version).toBe(1);
    expect(body.requestId).toBeDefined();
    expect(body.serverTime).toBeDefined();
    expect(body.stale).toBe(false);
    expect(body.dataAgeSeconds).toBe(0);

    // Match assertions
    const matches: any[] = body.data.matches;
    const testMatch = matches.find((m) => m.id === TEST_FIXTURE_ID);
    expect(testMatch).toBeDefined();

    // Verify exact legacy IDs (NEVER canonical ft_ IDs in v1)
    expect(testMatch.id).toBe(TEST_FIXTURE_ID);
    expect(testMatch.id.startsWith('fix_')).toBe(true);

    expect(testMatch.competition.id).toBe(TEST_COMP_ID);
    expect(testMatch.competition.id.startsWith('comp_')).toBe(true);

    expect(testMatch.homeTeam.id).toBe(TEST_TEAM_HOME_ID);
    expect(testMatch.homeTeam.id.startsWith('team_')).toBe(true);

    expect(testMatch.awayTeam.id).toBe(TEST_TEAM_AWAY_ID);
    expect(testMatch.awayTeam.id.startsWith('team_')).toBe(true);

    // Verify v1 exact keys set on match
    const matchKeys = Object.keys(testMatch).sort();
    const expectedMatchKeys = [
      'id',
      'competition',
      'homeTeam',
      'awayTeam',
      'state',
      'elapsedMinute',
      'extraMinute',
      'score',
      'kickoffTime',
      'recentEvents',
    ].sort();
    expect(matchKeys).toEqual(expectedMatchKeys);

    // Verify NO v2 fields leaked
    expect(testMatch.trust).toBeUndefined();
    expect(testMatch.canonicalId).toBeUndefined();
    expect(testMatch.liveSourceProvider).toBeUndefined();
    expect(testMatch.scheduleSourceProvider).toBeUndefined();
  });

  // =========================================================================
  // Test 2: GET /api/match/:id retains 100% v1 contract
  // =========================================================================
  it('2. GET /api/match/:id retains exact v1 contract and suppresses canonicalId', async () => {
    const res = await app.inject({
      method: 'GET',
      url: `/api/match/${TEST_FIXTURE_ID}`,
    });

    expect(res.statusCode).toBe(200);
    const body = JSON.parse(res.body);

    expect(body.schemaVersion).toBe('1.0');
    expect(body.version).toBe(1);

    const fixture = body.data;
    expect(fixture.id).toBe(TEST_FIXTURE_ID);
    expect(fixture.canonicalId).toBeUndefined();
    expect(fixture.competition.id).toBe(TEST_COMP_ID);
    expect(fixture.competition.canonicalId).toBeUndefined();
    expect(fixture.homeTeam.id).toBe(TEST_TEAM_HOME_ID);
    expect(fixture.homeTeam.canonicalId).toBeUndefined();
    expect(fixture.awayTeam.id).toBe(TEST_TEAM_AWAY_ID);
    expect(fixture.awayTeam.canonicalId).toBeUndefined();
    expect(fixture.trust).toBeUndefined();
  });

  // =========================================================================
  // Test 3: ETag and 304 caching on v1 /api/tray remain functional
  // =========================================================================
  it('3. GET /api/tray ETag caching operates identically without regression', async () => {
    const res1 = await app.inject({
      method: 'GET',
      url: '/api/tray',
    });

    expect(res1.statusCode).toBe(200);
    const etag = res1.headers['etag'];
    expect(etag).toBeDefined();

    const res2 = await app.inject({
      method: 'GET',
      url: '/api/tray',
      headers: {
        'if-none-match': etag as string,
      },
    });

    expect(res2.statusCode).toBe(304);
    expect(res2.body).toBe('');
  });
});
