import { describe, it, expect, beforeAll, afterAll, beforeEach, afterEach, vi } from 'vitest';
import { ApiFootballClient } from '../src/providers/api-football/api-football.client.js';
import { ApiFootballProvider } from '../src/providers/api-football/api-football.provider.js';
import { ApiFootballMapper } from '../src/providers/api-football/api-football.mapper.js';
import { SyncService } from '../src/acquisition/sync.service.js';
import { RequestGateway } from '../src/gateway/request-gateway.js';
import { pool, db, schema } from '../src/db/index.js';
import { generateCanonicalId } from '../src/db/canonical-id.js';
import { eq, inArray } from 'drizzle-orm';
import { Logger } from '../src/diagnostics/logger.js';
import type { ApiFootballFixtureItem } from '../src/providers/api-football/api-football.dto.js';

describe('Checkpoint 3 — API-Football ids Batch Detail & Finalization Optimization', () => {
  let gateway: RequestGateway;

  const TEST_COMP_ID = 'comp_batch_test';
  const TEST_SEASON_ID = 'season_batch_test_2025';
  const TEST_TEAM_HOME = 'team_42';
  const TEST_TEAM_AWAY = 'team_49';
  const TEST_FIXTURE_IDS = ['fix_101', 'fix_102', 'fix_103'];

  function createFullMockFixtureItem(
    fixtureId: number,
    homeScore = 0,
    awayScore = 0,
    includeDeepDetails = false
  ): ApiFootballFixtureItem {
    const item: ApiFootballFixtureItem = {
      fixture: {
        id: fixtureId,
        referee: 'M. Oliver',
        timezone: 'UTC',
        date: '2026-03-20T15:00:00Z',
        timestamp: 1774018800,
        periods: { first: 1774018800, second: 1774022400 },
        venue: { id: 10, name: 'Emirates Stadium', city: 'London' },
        status: { long: 'Match Finished', short: 'FT', elapsed: 90, extra: 0 },
      },
      league: {
        id: 39,
        name: 'Premier League',
        country: 'England',
        logo: 'https://example.com/39.png',
        flag: null,
        season: 2025,
        round: 'Regular Season - 29',
      },
      teams: {
        home: { id: 42, name: 'Arsenal', logo: 'https://example.com/42.png', winner: homeScore > awayScore },
        away: { id: 49, name: 'Chelsea', logo: 'https://example.com/49.png', winner: awayScore > homeScore },
      },
      goals: { home: homeScore, away: awayScore },
      score: {
        halftime: { home: 1, away: 0 },
        fulltime: { home: homeScore, away: awayScore },
        extratime: { home: null, away: null },
        penalty: { home: null, away: null },
      },
      events: [
        {
          time: { elapsed: 23, extra: null },
          team: { id: 42, name: 'Arsenal', logo: 'https://example.com/42.png' },
          player: { id: 110, name: 'B. Saka' },
          assist: { id: 111, name: 'M. Odegaard' },
          type: 'Goal',
          detail: 'Normal Goal',
          comments: null,
        },
      ],
    };

    if (includeDeepDetails) {
      item.lineups = [
        {
          team: { id: 42, name: 'Arsenal', logo: 'https://example.com/42.png', colors: null },
          coach: { id: 1, name: 'M. Arteta', photo: 'https://example.com/coach1.png' },
          formation: '4-3-3',
          startXI: [
            { player: { id: 101, name: 'D. Raya', number: 22, pos: 'G', grid: '1:1' } },
            { player: { id: 102, name: 'B. White', number: 4, pos: 'D', grid: '2:4' } },
            { player: { id: 103, name: 'W. Saliba', number: 2, pos: 'D', grid: '2:3' } },
            { player: { id: 104, name: 'Gabriel', number: 6, pos: 'D', grid: '2:2' } },
            { player: { id: 105, name: 'J. Timber', number: 12, pos: 'D', grid: '2:1' } },
            { player: { id: 106, name: 'T. Partey', number: 5, pos: 'M', grid: '3:3' } },
            { player: { id: 107, name: 'D. Rice', number: 41, pos: 'M', grid: '3:2' } },
            { player: { id: 111, name: 'M. Odegaard', number: 8, pos: 'M', grid: '3:1' } },
            { player: { id: 110, name: 'B. Saka', number: 7, pos: 'F', grid: '4:3' } },
            { player: { id: 108, name: 'K. Havertz', number: 29, pos: 'F', grid: '4:2' } },
            { player: { id: 109, name: 'G. Martinelli', number: 11, pos: 'F', grid: '4:1' } },
          ],
          substitutes: [
            { player: { id: 120, name: 'Neto', number: 32, pos: 'G', grid: null } },
            { player: { id: 121, name: 'L. Trossard', number: 19, pos: 'F', grid: null } },
          ],
        },
        {
          team: { id: 49, name: 'Chelsea', logo: 'https://example.com/49.png', colors: null },
          coach: { id: 2, name: 'E. Maresca', photo: 'https://example.com/coach2.png' },
          formation: '4-2-3-1',
          startXI: [
            { player: { id: 201, name: 'R. Sanchez', number: 1, pos: 'G', grid: '1:1' } },
            { player: { id: 202, name: 'M. Gusto', number: 27, pos: 'D', grid: '2:4' } },
            { player: { id: 203, name: 'W. Fofana', number: 29, pos: 'D', grid: '2:3' } },
            { player: { id: 204, name: 'L. Colwill', number: 6, pos: 'D', grid: '2:2' } },
            { player: { id: 205, name: 'M. Cucurella', number: 3, pos: 'D', grid: '2:1' } },
            { player: { id: 206, name: 'M. Caicedo', number: 25, pos: 'M', grid: '3:2' } },
            { player: { id: 207, name: 'R. Lavia', number: 45, pos: 'M', grid: '3:1' } },
            { player: { id: 208, name: 'N. Madueke', number: 11, pos: 'M', grid: '4:3' } },
            { player: { id: 209, name: 'C. Palmer', number: 20, pos: 'M', grid: '4:2' } },
            { player: { id: 210, name: 'P. Neto', number: 7, pos: 'M', grid: '4:1' } },
            { player: { id: 211, name: 'N. Jackson', number: 15, pos: 'F', grid: '5:1' } },
          ],
          substitutes: [
            { player: { id: 220, name: 'F. Jorgensen', number: 12, pos: 'G', grid: null } },
            { player: { id: 221, name: 'C. Nkunku', number: 18, pos: 'F', grid: null } },
          ],
        },
      ];

      item.statistics = [
        {
          team: { id: 42, name: 'Arsenal', logo: 'https://example.com/42.png' },
          statistics: [
            { type: 'Shots on Goal', value: 7 },
            { type: 'Total Shots', value: 16 },
            { type: 'Ball Possession', value: '56%' },
            { type: 'Passes %', value: '88%' },
            { type: 'Corner Kicks', value: 8 },
          ],
        },
        {
          team: { id: 49, name: 'Chelsea', logo: 'https://example.com/49.png' },
          statistics: [
            { type: 'Shots on Goal', value: 3 },
            { type: 'Total Shots', value: 9 },
            { type: 'Ball Possession', value: '44%' },
            { type: 'Passes %', value: '81%' },
            { type: 'Corner Kicks', value: 3 },
          ],
        },
      ];

      item.players = [
        {
          team: { id: 42, name: 'Arsenal', logo: 'https://example.com/42.png', update: '2026-03-20' },
          players: [
            {
              player: { id: 110, name: 'B. Saka', photo: 'https://example.com/saka.png' },
              statistics: [
                {
                  games: { minutes: 90, number: 7, position: 'F', rating: '8.4', captain: false, substitute: false },
                  goals: { total: 1, conceded: 0, assists: 0, saves: 0 },
                },
              ],
            },
            {
              player: { id: 111, name: 'M. Odegaard', photo: 'https://example.com/odegaard.png' },
              statistics: [
                {
                  games: { minutes: 90, number: 8, position: 'M', rating: '7.9', captain: true, substitute: false },
                  goals: { total: 0, conceded: 0, assists: 1, saves: 0 },
                },
              ],
            },
          ],
        },
      ];
    }

    return item;
  }

  const cleanupTestRecords = async () => {
    await pool.query(`DELETE FROM events WHERE fixture_id IN (${TEST_FIXTURE_IDS.map((id) => `'${id}'`).join(',')})`);
    await pool.query(`DELETE FROM fixtures WHERE id IN (${TEST_FIXTURE_IDS.map((id) => `'${id}'`).join(',')})`);
    await pool.query(`DELETE FROM teams WHERE id IN ('${TEST_TEAM_HOME}', '${TEST_TEAM_AWAY}')`);
    await pool.query(`DELETE FROM competition_seasons WHERE id = '${TEST_SEASON_ID}'`);
    await pool.query(`DELETE FROM competition_provider_aliases WHERE provider = 'api_football' AND provider_id = '39'`);
    await pool.query(`DELETE FROM competitions WHERE id = '${TEST_COMP_ID}'`);
    await pool.query(`DELETE FROM api_request_ledger WHERE provider = 'api_football'`);
    await pool.query(`DELETE FROM provider_daily_budget WHERE provider = 'api_football'`);
  };

  beforeAll(async () => {
    const dbInfo = await pool.query('SELECT current_database()');
    if (dbInfo.rows[0].current_database !== 'footballtray_test') {
      throw new Error(
        `[SAFETY ERROR] Tests are connected to '${dbInfo.rows[0].current_database}', expected 'footballtray_test'!`
      );
    }
  });

  beforeEach(async () => {
    gateway = new RequestGateway(pool);
    await cleanupTestRecords();
  });

  afterEach(async () => {
    await cleanupTestRecords();
  });

  afterAll(async () => {
    await cleanupTestRecords();
  });

  // =========================================================================
  // Test 1: Batch Detail Fetching via `ids` consumes exactly 1 provider request
  // =========================================================================
  it('1. Batch detail fetch for multiple fixtures consumes exactly 1 provider request credit via /fixtures?ids=...', async () => {
    const requestedUrls: string[] = [];
    const mockFetch = async (url: string) => {
      requestedUrls.push(url);
      return {
        status: 200,
        headers: new Headers({ 'x-ratelimit-requests-remaining': '99' }),
        json: async () => ({
          get: 'fixtures',
          parameters: { ids: '101-102-103' },
          errors: [],
          results: 3,
          paging: { current: 1, total: 1 },
          response: [
            createFullMockFixtureItem(101, 2, 1),
            createFullMockFixtureItem(102, 0, 0),
            createFullMockFixtureItem(103, 3, 2),
          ],
        }),
      };
    };

    const client = new ApiFootballClient({
      apiKey: 'test_key',
      gateway,
      fetchFn: mockFetch as any,
    });

    const response = await client.getFixturesByIds(['fix_101', 'fix_102', 'fix_103']);

    // Verify response
    expect(response.success).toBe(true);
    expect(response.data?.response?.length).toBe(3);

    // Verify URL format: uses hyphen-separated IDs
    expect(requestedUrls.length).toBe(1);
    expect(requestedUrls[0]).toContain('/fixtures?ids=101-102-103');

    // Verify exactly 1 request logged in api_request_ledger
    const ledgerRows = await pool.query(
      `SELECT * FROM api_request_ledger WHERE provider = 'api_football'`
    );
    expect(ledgerRows.rows.length).toBe(1);
    expect(ledgerRows.rows[0].endpoint).toBe('/fixtures?ids=101-102-103');
    expect(ledgerRows.rows[0].cost_units).toBe(1);

    // Verify budget consumed exactly 1 unit
    const budgetRows = await pool.query(
      `SELECT * FROM provider_daily_budget WHERE provider = 'api_football'`
    );
    expect(budgetRows.rows.length).toBe(1);
    expect(budgetRows.rows[0].consumed_calls).toBe(1);
  });

  // =========================================================================
  // Test 2: Single-flight coalescing for concurrent batch detail requests
  // =========================================================================
  it('2. Coalesces concurrent batch detail requests for identical IDs', async () => {
    let fetchCount = 0;
    const mockFetch = async () => {
      fetchCount++;
      await new Promise((resolve) => setTimeout(resolve, 30));
      return {
        status: 200,
        headers: new Headers({ 'x-ratelimit-requests-remaining': '98' }),
        json: async () => ({
          get: 'fixtures',
          parameters: { ids: '101-102' },
          errors: [],
          results: 2,
          paging: { current: 1, total: 1 },
          response: [createFullMockFixtureItem(101, 1, 0), createFullMockFixtureItem(102, 2, 2)],
        }),
      };
    };

    const client = new ApiFootballClient({
      apiKey: 'test_key',
      gateway,
      fetchFn: mockFetch as any,
    });

    // Fire 3 concurrent batch requests with the same fixture IDs
    const [res1, res2, res3] = await Promise.all([
      client.getFixturesByIds(['101', '102']),
      client.getFixturesByIds(['101', '102']),
      client.getFixturesByIds(['101', '102']),
    ]);

    expect(res1.success).toBe(true);
    expect(res2.success).toBe(true);
    expect(res3.success).toBe(true);
    expect(fetchCount).toBe(1);
  });

  // =========================================================================
  // Test 3: Deep Data Parsing in ApiFootballMapper (events, lineups, stats, player ratings)
  // =========================================================================
  it('3. ApiFootballMapper correctly maps embedded events, lineups, statistics, and player ratings', () => {
    const rawFixture = createFullMockFixtureItem(101, 2, 1, true);
    const detail = ApiFootballMapper.mapFixtureDetail(rawFixture);

    // 1. Basic detail mapping
    expect(detail.id).toBe('fix_101');
    expect(detail.score.home).toBe(2);
    expect(detail.score.away).toBe(1);
    expect(detail.score.homeHt).toBe(1);
    expect(detail.score.awayHt).toBe(0);

    // 2. Events mapping
    expect(detail.events.length).toBe(1);
    expect(detail.events[0].playerName).toBe('B. Saka');
    expect(detail.events[0].assistPlayerName).toBe('M. Odegaard');
    expect(detail.events[0].minute).toBe(23);
    expect(detail.events[0].type).toBe('Goal');

    // 3. Lineups mapping
    expect(detail.lineups).toBeDefined();
    expect(detail.lineups?.length).toBe(2);

    const homeLineup = detail.lineups![0];
    expect(homeLineup.formation).toBe('4-3-3');
    expect(homeLineup.coachName).toBe('M. Arteta');
    expect(homeLineup.startXi.length).toBe(11);
    expect(homeLineup.startXi[0].name).toBe('D. Raya');
    expect(homeLineup.startXi[0].number).toBe(22);
    expect(homeLineup.startXi[0].position).toBe('G');
    expect(homeLineup.startXi[0].grid).toBe('1:1');
    expect(homeLineup.substitutes.length).toBe(2);
    expect(homeLineup.substitutes[1].name).toBe('L. Trossard');

    const awayLineup = detail.lineups![1];
    expect(awayLineup.formation).toBe('4-2-3-1');
    expect(awayLineup.coachName).toBe('E. Maresca');
    expect(awayLineup.startXi.length).toBe(11);
    expect(awayLineup.substitutes.length).toBe(2);

    // 4. Team Statistics mapping
    expect(detail.statistics).toBeDefined();
    expect(detail.statistics?.length).toBe(2);

    const homeStats = detail.statistics![0];
    expect(homeStats.teamId).toBe('team_42');
    expect(homeStats.shotsOnGoal).toBe(7);
    expect(homeStats.totalShots).toBe(16);
    expect(homeStats.possessionPercent).toBe(56); // '56%' -> parsed as numeric 56
    expect(homeStats.cornerKicks).toBe(8);

    const awayStats = detail.statistics![1];
    expect(awayStats.teamId).toBe('team_49');
    expect(awayStats.shotsOnGoal).toBe(3);
    expect(awayStats.possessionPercent).toBe(44);

    // 5. Player Ratings mapping
    expect(detail.playerRatings).toBeDefined();
    expect(detail.playerRatings?.length).toBe(2);

    const sakaRating = detail.playerRatings?.find((p) => p.name === 'B. Saka');
    expect(sakaRating).toBeDefined();
    expect(sakaRating?.rating).toBe(8.4);

    const odegaardRating = detail.playerRatings?.find((p) => p.name === 'M. Odegaard');
    expect(odegaardRating).toBeDefined();
    expect(odegaardRating?.rating).toBe(7.9);
  });

  // =========================================================================
  // Test 4: Partial Failure Isolation in Provider Layer
  // =========================================================================
  it('4. Provider isolates malformed items in batch detail response and returns valid fixtures', async () => {
    const valid1 = createFullMockFixtureItem(101, 1, 0);
    const valid2 = createFullMockFixtureItem(103, 2, 2);
    // Malformed fixture: missing fixture id / invalid structure
    const malformed = {
      fixture: null,
      league: null,
      teams: null,
    } as any;

    const warnSpy = vi.spyOn(Logger, 'warn');

    const mockFetch = async () => ({
      status: 200,
      headers: new Headers({ 'x-ratelimit-requests-remaining': '97' }),
      json: async () => ({
        get: 'fixtures',
        parameters: { ids: '101-999-103' },
        errors: [],
        results: 3,
        paging: { current: 1, total: 1 },
        response: [valid1, malformed, valid2],
      }),
    });

    const provider = new ApiFootballProvider({
      apiKey: 'test_key',
      gateway,
      fetchFn: mockFetch as any,
    });

    const results = await provider.getFixtureDetailsBatch(['fix_101', 'fix_999', 'fix_103']);

    // Valid fixtures are preserved and returned
    expect(results.length).toBe(2);
    expect(results[0].id).toBe('fix_101');
    expect(results[1].id).toBe('fix_103');

    // Warning was logged for the malformed item
    expect(warnSpy).toHaveBeenCalled();
    warnSpy.mockRestore();
  });

  // =========================================================================
  // Test 5: Batch Finalization Sweep & Partial Failure Isolation in SyncService
  // =========================================================================
  it('5. SyncService.acquireFinalization reconciles multiple finished matches concurrently in a single batch', async () => {
    // 1. Seed competition and teams
    const compCanonicalId = generateCanonicalId('comp');
    await pool.query(
      `INSERT INTO competitions (id, canonical_id, name, code, type, country, logo_url, created_at, updated_at)
       VALUES ('${TEST_COMP_ID}', '${compCanonicalId}', 'Batch Test League', 'BTL', 'league', 'England', 'https://example.com/logo.png', NOW(), NOW())
       ON CONFLICT (id) DO NOTHING;`
    );

    await pool.query(
      `INSERT INTO competition_seasons (id, competition_id, year, display_year, start_date, end_date, is_current)
       VALUES ('${TEST_SEASON_ID}', '${TEST_COMP_ID}', 2025, '2025/2026', '2025-08-01', '2026-05-31', true)
       ON CONFLICT (id) DO NOTHING;`
    );

    const teamHomeCanId = generateCanonicalId('team');
    const teamAwayCanId = generateCanonicalId('team');
    await pool.query(
      `INSERT INTO teams (id, canonical_id, name, short_name, code, country, created_at, updated_at)
       VALUES 
         ('${TEST_TEAM_HOME}', '${teamHomeCanId}', 'Team Home', 'HOM', 'HOM', 'England', NOW(), NOW()),
         ('${TEST_TEAM_AWAY}', '${teamAwayCanId}', 'Team Away', 'AWY', 'AWY', 'England', NOW(), NOW())
       ON CONFLICT (id) DO NOTHING;`
    );

    // 2. Seed 3 candidate fixtures in FINISHED state with outdated scores (0-0)
    const now = new Date();
    for (const fixId of TEST_FIXTURE_IDS) {
      const fixCanId = generateCanonicalId('fix');
      await pool.query(
        `INSERT INTO fixtures (
          id, canonical_id, competition_id, season_id, home_team_id, away_team_id,
          round, kickoff_time, state, elapsed_minute, home_score, away_score,
          last_polled_at, created_at, updated_at
        ) VALUES (
          $1, $2, '${TEST_COMP_ID}', '${TEST_SEASON_ID}', '${TEST_TEAM_HOME}', '${TEST_TEAM_AWAY}',
          'Round 1', NOW() - INTERVAL '2 hours', 'FINISHED', 90, 0, 0,
          NULL, NOW(), $3
        );`,
        [fixId, fixCanId, now]
      );
    }

    // 3. Setup mock provider returning updated scores for all 3 fixtures with new events
    const requestedUrls: string[] = [];
    const mockFetch = async (url: string) => {
      requestedUrls.push(url);
      return {
        status: 200,
        headers: new Headers({ 'x-ratelimit-requests-remaining': '96' }),
        json: async () => ({
          get: 'fixtures',
          parameters: { ids: '101-102-103' },
          errors: [],
          results: 3,
          paging: { current: 1, total: 1 },
          response: [
            createFullMockFixtureItem(101, 2, 1),
            createFullMockFixtureItem(102, 3, 0),
            createFullMockFixtureItem(103, 1, 1),
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

    // 4. Run finalization sweep
    const summary = await syncService.acquireFinalization([TEST_COMP_ID]);

    // 5. Assertions
    expect(requestedUrls.length).toBe(1);
    expect(requestedUrls[0]).toContain('/fixtures?ids=101-102-103');
    expect(summary.fixturesUpdated).toBe(3);
    expect(summary.eventsInserted).toBeGreaterThanOrEqual(3);

    // Verify DB state for all 3 fixtures
    const fix1 = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, 'fix_101') });
    expect(fix1?.homeScore).toBe(2);
    expect(fix1?.awayScore).toBe(1);
    expect(fix1?.lastPolledAt).not.toBeNull();

    const fix2 = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, 'fix_102') });
    expect(fix2?.homeScore).toBe(3);
    expect(fix2?.awayScore).toBe(0);
    expect(fix2?.lastPolledAt).not.toBeNull();

    const fix3 = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, 'fix_103') });
    expect(fix3?.homeScore).toBe(1);
    expect(fix3?.awayScore).toBe(1);
    expect(fix3?.lastPolledAt).not.toBeNull();
  });

  // =========================================================================
  // Test 6: Partial Failure Isolation in SyncService
  // =========================================================================
  it('6. Partial failure in batch response touches lastPolledAt for missing fixture and reconciles remaining', async () => {
    // 1. Seed competition and teams
    const compCanonicalId = generateCanonicalId('comp');
    await pool.query(
      `INSERT INTO competitions (id, canonical_id, name, code, type, country, logo_url, created_at, updated_at)
       VALUES ('${TEST_COMP_ID}', '${compCanonicalId}', 'Batch Test League', 'BTL', 'league', 'England', 'https://example.com/logo.png', NOW(), NOW())
       ON CONFLICT (id) DO NOTHING;`
    );

    await pool.query(
      `INSERT INTO competition_seasons (id, competition_id, year, display_year, start_date, end_date, is_current)
       VALUES ('${TEST_SEASON_ID}', '${TEST_COMP_ID}', 2025, '2025/2026', '2025-08-01', '2026-05-31', true)
       ON CONFLICT (id) DO NOTHING;`
    );

    const teamHomeCanId = generateCanonicalId('team');
    const teamAwayCanId = generateCanonicalId('team');
    await pool.query(
      `INSERT INTO teams (id, canonical_id, name, short_name, code, country, created_at, updated_at)
       VALUES 
         ('${TEST_TEAM_HOME}', '${teamHomeCanId}', 'Team Home', 'HOM', 'HOM', 'England', NOW(), NOW()),
         ('${TEST_TEAM_AWAY}', '${teamAwayCanId}', 'Team Away', 'AWY', 'AWY', 'England', NOW(), NOW())
       ON CONFLICT (id) DO NOTHING;`
    );

    // 2. Seed 2 candidate fixtures
    const now = new Date();
    await pool.query(
      `INSERT INTO fixtures (
        id, canonical_id, competition_id, season_id, home_team_id, away_team_id,
        round, kickoff_time, state, elapsed_minute, home_score, away_score,
        last_polled_at, created_at, updated_at
      ) VALUES 
        ('fix_101', '${generateCanonicalId('fix')}', '${TEST_COMP_ID}', '${TEST_SEASON_ID}', '${TEST_TEAM_HOME}', '${TEST_TEAM_AWAY}', 'Round 1', NOW() - INTERVAL '2 hours', 'FINISHED', 90, 0, 0, NULL, NOW(), $1),
        ('fix_102', '${generateCanonicalId('fix')}', '${TEST_COMP_ID}', '${TEST_SEASON_ID}', '${TEST_TEAM_HOME}', '${TEST_TEAM_AWAY}', 'Round 1', NOW() - INTERVAL '2 hours', 'FINISHED', 90, 0, 0, NULL, NOW(), $1);`,
      [now]
    );

    // 3. Mock provider returns ONLY fixture 101, omitting 102
    const mockFetch = async () => ({
      status: 200,
      headers: new Headers({ 'x-ratelimit-requests-remaining': '95' }),
      json: async () => ({
        get: 'fixtures',
        parameters: { ids: '101-102' },
        errors: [],
        results: 1,
        paging: { current: 1, total: 1 },
        response: [createFullMockFixtureItem(101, 2, 0)],
      }),
    });

    const warnSpy = vi.spyOn(Logger, 'warn');

    const provider = new ApiFootballProvider({
      apiKey: 'test_key',
      gateway,
      fetchFn: mockFetch as any,
    });
    const syncService = new SyncService(provider);

    // 4. Run finalization sweep
    const summary = await syncService.acquireFinalization([TEST_COMP_ID]);

    // 5. Assertions: fixture 101 was updated
    expect(summary.fixturesUpdated).toBe(1);
    const fix1 = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, 'fix_101') });
    expect(fix1?.homeScore).toBe(2);
    expect(fix1?.lastPolledAt).not.toBeNull();

    // Fixture 102 was omitted from response, but lastPolledAt was touched so it does not loop indefinitely
    const fix2 = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, 'fix_102') });
    expect(fix2?.homeScore).toBe(0);
    expect(fix2?.lastPolledAt).not.toBeNull();

    // Logger warning was emitted for the missing fixture
    expect(warnSpy).toHaveBeenCalledWith(
      'SyncService',
      'MissingBatchFixture',
      expect.stringContaining('fix_102'),
      undefined
    );

    warnSpy.mockRestore();
  });
});
