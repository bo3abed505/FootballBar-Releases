import { describe, it, expect, beforeAll, afterAll, beforeEach, afterEach } from 'vitest';
import { ApiFootballClient } from '../src/providers/api-football/api-football.client.js';
import { ApiFootballProvider } from '../src/providers/api-football/api-football.provider.js';
import { AcquisitionService } from '../src/acquisition/acquisition.service.js';
import { SyncService } from '../src/acquisition/sync.service.js';
import { RequestGateway } from '../src/gateway/request-gateway.js';
import { pool, db, schema } from '../src/db/index.js';
import { generateCanonicalId } from '../src/db/canonical-id.js';
import { eq } from 'drizzle-orm';
import type { ApiFootballFixtureItem } from '../src/providers/api-football/api-football.dto.js';

describe('Checkpoint 2 — Date-Wide Discovery & Single-Flight Coalescing', () => {
  let gateway: RequestGateway;

  const TEST_COMP_IDS = ['comp_39', 'comp_140', 'comp_999'];
  const TEST_LEAGUE_IDS = ['39', '140', '999'];
  const TEST_FIXTURE_IDS = ['fix_39001', 'fix_39002', 'fix_140001', 'fix_999001'];
  const TEST_SEASON_IDS = ['season_39_2025', 'season_140_2025', 'season_999_2025'];
  const TEST_TEAM_IDS = ['team_40', 'team_50', 'team_529', 'team_541', 'team_901', 'team_902'];

  function createMockFixtureItem(
    fixtureId: number,
    leagueId: number,
    leagueName: string,
    homeTeamId: number,
    homeTeamName: string,
    awayTeamId: number,
    awayTeamName: string,
    dateStr = '2026-03-10T15:00:00Z'
  ): ApiFootballFixtureItem {
    return {
      fixture: {
        id: fixtureId,
        referee: 'M. Oliver',
        timezone: 'UTC',
        date: dateStr,
        timestamp: Math.floor(new Date(dateStr).getTime() / 1000),
        periods: { first: null, second: null },
        venue: { id: 100, name: 'Main Stadium', city: 'City' },
        status: { long: 'Not Started', short: 'NS', elapsed: 0, extra: 0 },
      },
      league: {
        id: leagueId,
        name: leagueName,
        country: 'Country',
        logo: `https://example.com/leagues/${leagueId}.png`,
        flag: null,
        season: 2025,
        round: 'Regular Season - 1',
      },
      teams: {
        home: { id: homeTeamId, name: homeTeamName, logo: `https://example.com/teams/${homeTeamId}.png`, winner: null },
        away: { id: awayTeamId, name: awayTeamName, logo: `https://example.com/teams/${awayTeamId}.png`, winner: null },
      },
      goals: { home: null, away: null },
      score: {
        halftime: { home: null, away: null },
        fulltime: { home: null, away: null },
        extratime: { home: null, away: null },
        penalty: { home: null, away: null },
      },
      events: [],
    };
  }

  const cleanupTestRecords = async () => {
    await pool.query(`DELETE FROM events WHERE fixture_id IN (${TEST_FIXTURE_IDS.map((id) => `'${id}'`).join(',')})`);
    await pool.query(`DELETE FROM fixtures WHERE id IN (${TEST_FIXTURE_IDS.map((id) => `'${id}'`).join(',')})`);
    await pool.query(`DELETE FROM teams WHERE id IN ('team_901', 'team_902')`);
    await pool.query(`DELETE FROM competition_seasons WHERE id = 'season_999_2025'`);
    await pool.query(`DELETE FROM competition_provider_aliases WHERE provider = 'api_football' AND provider_id = '999'`);
    await pool.query(`DELETE FROM competitions WHERE id = 'comp_999'`);
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
  // Test 1: Date-wide discovery consumes exactly 1 provider request
  // =========================================================================
  it('1. Date-wide discovery consumes exactly 1 provider request for a multi-competition day', async () => {
    // 1. Seed active competitions in DB with canonical IDs and provider aliases
    const existing39 = await db.query.competitions.findFirst({ where: eq(schema.competitions.id, 'comp_39') });
    const comp39CanId = existing39?.canonicalId || generateCanonicalId('comp');

    const existing140 = await db.query.competitions.findFirst({ where: eq(schema.competitions.id, 'comp_140') });
    const comp140CanId = existing140?.canonicalId || generateCanonicalId('comp');

    await pool.query(
      `INSERT INTO competitions (id, canonical_id, name, code, type, country, logo_url, created_at, updated_at)
       VALUES 
         ('comp_39', $1, 'Premier League', 'PL', 'league', 'England', 'https://example.com/39.png', NOW(), NOW()),
         ('comp_140', $2, 'La Liga', 'LL', 'league', 'Spain', 'https://example.com/140.png', NOW(), NOW())
       ON CONFLICT (id) DO NOTHING;`,
      [comp39CanId, comp140CanId]
    );

    await pool.query(
      `INSERT INTO competition_provider_aliases (canonical_competition_id, provider, provider_id, first_seen_at, last_seen_at)
       VALUES 
         ($1, 'api_football', '39', NOW(), NOW()),
         ($2, 'api_football', '140', NOW(), NOW())
       ON CONFLICT (provider, provider_id) DO UPDATE SET canonical_competition_id = EXCLUDED.canonical_competition_id;`,
      [comp39CanId, comp140CanId]
    );

    // 2. Setup mock fetch returning fixtures for BOTH Premier League and La Liga for date 2026-03-10
    const fixture39 = createMockFixtureItem(39001, 39, 'Premier League', 40, 'Liverpool', 50, 'Manchester City', '2026-03-10T15:00:00Z');
    const fixture140 = createMockFixtureItem(140001, 140, 'La Liga', 529, 'Barcelona', 541, 'Real Madrid', '2026-03-10T20:00:00Z');

    const requestedUrls: string[] = [];
    const mockFetch = async (url: string) => {
      requestedUrls.push(url);
      return {
        status: 200,
        headers: new Headers({ 'x-ratelimit-requests-remaining': '99' }),
        json: async () => ({
          get: 'fixtures',
          parameters: { date: '2026-03-10' },
          errors: [],
          results: 2,
          paging: { current: 1, total: 1 },
          response: [fixture39, fixture140],
        }),
      };
    };

    const provider = new ApiFootballProvider({
      apiKey: 'test_key',
      gateway,
      fetchFn: mockFetch as any,
    });
    const syncService = new SyncService(provider);

    // 3. Execute discovery via syncSchedule for 2026-03-10
    const summary = await syncService.syncSchedule('2026-03-10', [39, 140]);

    // 4. Assertions: Exactly 1 provider request executed for the date across multiple competitions
    expect(requestedUrls.length).toBe(1);
    expect(requestedUrls[0]).toContain('/fixtures?date=2026-03-10');
    expect(requestedUrls[0]).not.toContain('league=');

    expect(summary.fixturesDiscovered).toBe(2);
    expect(summary.fixturesInserted).toBe(2);

    // Verify both fixtures persisted in DB
    const fix39 = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, 'fix_39001') });
    expect(fix39).toBeDefined();
    expect(fix39?.competitionId).toBe('comp_39');

    const fix140 = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, 'fix_140001') });
    expect(fix140).toBeDefined();
    expect(fix140?.competitionId).toBe('comp_140');
  });

  // =========================================================================
  // Test 2: Unmonitored competitions returned in date payload are safely ignored
  // =========================================================================
  it('2. Unmonitored competitions returned in the date payload are safely ignored', async () => {
    // 1. Seed ONLY Premier League (39) as monitored; league 999 is intentionally unmonitored
    const existing39 = await db.query.competitions.findFirst({ where: eq(schema.competitions.id, 'comp_39') });
    const comp39CanId = existing39?.canonicalId || generateCanonicalId('comp');

    await pool.query(
      `INSERT INTO competitions (id, canonical_id, name, code, type, country, logo_url, created_at, updated_at)
       VALUES ('comp_39', $1, 'Premier League', 'PL', 'league', 'England', 'https://example.com/39.png', NOW(), NOW())
       ON CONFLICT (id) DO NOTHING;`,
      [comp39CanId]
    );

    await pool.query(
      `INSERT INTO competition_provider_aliases (canonical_competition_id, provider, provider_id, first_seen_at, last_seen_at)
       VALUES ($1, 'api_football', '39', NOW(), NOW())
       ON CONFLICT (provider, provider_id) DO UPDATE SET canonical_competition_id = EXCLUDED.canonical_competition_id;`,
      [comp39CanId]
    );

    // 2. Mock payload returns fixture for monitored league 39 AND unmonitored league 999
    const monitoredFix = createMockFixtureItem(39002, 39, 'Premier League', 40, 'Liverpool', 50, 'Manchester City', '2026-03-11T15:00:00Z');
    const unmonitoredFix = createMockFixtureItem(999001, 999, 'Unmonitored League', 901, 'Club Alpha', 902, 'Club Beta', '2026-03-11T16:00:00Z');

    const requestedUrls: string[] = [];
    const mockFetch = async (url: string) => {
      requestedUrls.push(url);
      return {
        status: 200,
        headers: new Headers({ 'x-ratelimit-requests-remaining': '98' }),
        json: async () => ({
          get: 'fixtures',
          parameters: { date: '2026-03-11' },
          errors: [],
          results: 2,
          paging: { current: 1, total: 1 },
          response: [monitoredFix, unmonitoredFix],
        }),
      };
    };

    const provider = new ApiFootballProvider({
      apiKey: 'test_key',
      gateway,
      fetchFn: mockFetch as any,
    });
    const syncService = new SyncService(provider);

    // 3. Execute discovery
    const summary = await syncService.syncSchedule('2026-03-11');

    // 4. Assertions: Exactly 1 request made
    expect(requestedUrls.length).toBe(1);
    expect(requestedUrls[0]).toContain('/fixtures?date=2026-03-11');

    // Only monitored fixture was discovered and ingested
    expect(summary.fixturesDiscovered).toBe(1);
    expect(summary.fixturesInserted).toBe(1);

    // Monitored fixture exists in DB
    const fixMonitored = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, 'fix_39002') });
    expect(fixMonitored).toBeDefined();

    // Unmonitored fixture was NOT inserted
    const fixUnmonitored = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, 'fix_999001') });
    expect(fixUnmonitored).toBeUndefined();

    // Unmonitored competition, teams, and seasons were safely NOT inserted
    const compUnmonitored = await db.query.competitions.findFirst({ where: eq(schema.competitions.id, 'comp_999') });
    expect(compUnmonitored).toBeUndefined();

    const teamAlpha = await db.query.teams.findFirst({ where: eq(schema.teams.id, 'team_901') });
    expect(teamAlpha).toBeUndefined();

    const teamBeta = await db.query.teams.findFirst({ where: eq(schema.teams.id, 'team_902') });
    expect(teamBeta).toBeUndefined();

    const seasonUnmonitored = await db.query.competitionSeasons.findFirst({ where: eq(schema.competitionSeasons.id, 'season_999_2025') });
    expect(seasonUnmonitored).toBeUndefined();
  });

  // =========================================================================
  // Test 3: Concurrent requests coalesce into a single execution
  // =========================================================================
  describe('3. Single-Flight Request Coalescing', () => {
    it('coalesces concurrent requests for identical date/endpoint in ApiFootballClient', async () => {
      let fetchCount = 0;
      const mockFetch = async (url: string) => {
        fetchCount++;
        // Add an intentional delay to allow concurrent callers to overlap in-flight
        await new Promise((resolve) => setTimeout(resolve, 30));
        return {
          status: 200,
          headers: new Headers({ 'x-ratelimit-requests-remaining': '97' }),
          json: async () => ({
            get: 'fixtures',
            parameters: { date: '2026-03-12' },
            errors: [],
            results: 1,
            paging: { current: 1, total: 1 },
            response: [
              createMockFixtureItem(39001, 39, 'Premier League', 40, 'Liverpool', 50, 'Manchester City', '2026-03-12T15:00:00Z'),
            ],
          }),
        };
      };

      const client = new ApiFootballClient({
        apiKey: 'test_key',
        gateway,
        fetchFn: mockFetch as any,
      });

      // Fire 5 concurrent requests for the exact same date
      const promises = [
        client.getFixturesByDate('2026-03-12'),
        client.getFixturesByDate('2026-03-12'),
        client.getFixturesByDate('2026-03-12'),
        client.getFixturesByDate('2026-03-12'),
        client.getFixturesByDate('2026-03-12'),
      ];

      const responses = await Promise.all(promises);

      // Verify that all 5 succeeded and received identical response payloads
      expect(responses.length).toBe(5);
      for (const res of responses) {
        expect(res.success).toBe(true);
        expect(res.data?.response?.length).toBe(1);
        expect(res.data?.response?.[0].fixture.id).toBe(39001);
      }

      // Crucial assertion: Exactly 1 network fetch executed due to single-flight coalescing
      expect(fetchCount).toBe(1);

      // Verify that after resolution, subsequent sequential request executes fresh
      const subsequent = await client.getFixturesByDate('2026-03-12');
      expect(subsequent.success).toBe(true);
      expect(fetchCount).toBe(2);
    });

    it('coalesces concurrent requests in ApiFootballProvider layer', async () => {
      let fetchCount = 0;
      const mockFetch = async () => {
        fetchCount++;
        await new Promise((resolve) => setTimeout(resolve, 30));
        return {
          status: 200,
          headers: new Headers({ 'x-ratelimit-requests-remaining': '96' }),
          json: async () => ({
            get: 'fixtures',
            parameters: { date: '2026-03-13' },
            errors: [],
            results: 1,
            paging: { current: 1, total: 1 },
            response: [
              createMockFixtureItem(39001, 39, 'Premier League', 40, 'Liverpool', 50, 'Manchester City', '2026-03-13T15:00:00Z'),
            ],
          }),
        };
      };

      const provider = new ApiFootballProvider({
        apiKey: 'test_key',
        gateway,
        fetchFn: mockFetch as any,
      });

      // Fire 3 concurrent provider requests
      const [res1, res2, res3] = await Promise.all([
        provider.getFixturesByDate('2026-03-13'),
        provider.getFixturesByDate('2026-03-13'),
        provider.getFixturesByDate('2026-03-13'),
      ]);

      expect(res1.length).toBe(1);
      expect(res2.length).toBe(1);
      expect(res3.length).toBe(1);
      expect(fetchCount).toBe(1);
    });

    it('coalesces concurrent requests in AcquisitionService layer', async () => {
      let fetchCount = 0;
      const mockFetch = async () => {
        fetchCount++;
        await new Promise((resolve) => setTimeout(resolve, 30));
        return {
          status: 200,
          headers: new Headers({ 'x-ratelimit-requests-remaining': '95' }),
          json: async () => ({
            get: 'fixtures',
            parameters: { date: '2026-03-14' },
            errors: [],
            results: 1,
            paging: { current: 1, total: 1 },
            response: [
              createMockFixtureItem(39001, 39, 'Premier League', 40, 'Liverpool', 50, 'Manchester City', '2026-03-14T15:00:00Z'),
            ],
          }),
        };
      };

      const provider = new ApiFootballProvider({
        apiKey: 'test_key',
        gateway,
        fetchFn: mockFetch as any,
      });
      const acquisition = new AcquisitionService(provider);

      // Concurrent acquisitions
      const [batch1, batch2] = await Promise.all([
        acquisition.acquireScheduled('2026-03-14'),
        acquisition.acquireScheduled('2026-03-14'),
      ]);

      expect(batch1.fixtures.length).toBe(1);
      expect(batch2.fixtures.length).toBe(1);
      expect(batch1.fixtures[0].id).toBe('fix_39001');
      expect(batch2.fixtures[0].id).toBe('fix_39001');
      expect(fetchCount).toBe(1);
    });
  });
});
