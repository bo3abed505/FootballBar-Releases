import { describe, it, expect, beforeAll, afterAll, beforeEach } from 'vitest';
import { eq, inArray } from 'drizzle-orm';
import { db, schema, pool } from '../src/db/index.js';
import { SyncService } from '../src/acquisition/sync.service.js';
import { FootballProvider, ProviderFixture, ProviderCompetition, ProviderSeason, ProviderTeam } from '../src/providers/football-provider.js';
import { MockFootballProvider } from '../src/providers/mock.provider.js';
import { ApiFootballMapper } from '../src/providers/api-football/api-football.mapper.js';
import { ApiFootballFixtureItem, ApiFootballLeagueItem } from '../src/providers/api-football/api-football.dto.js';
import { generateCanonicalId } from '../src/db/canonical-id.js';
import { buildServer } from '../src/server.js';

const CANONICAL_COMP_REGEX = /^ft_comp_[0123456789ABCDEFGHJKMNPQRSTVWXYZ]{26}$/;
const CANONICAL_TEAM_REGEX = /^ft_team_[0123456789ABCDEFGHJKMNPQRSTVWXYZ]{26}$/;
const CANONICAL_FIX_REGEX = /^ft_fix_[0123456789ABCDEFGHJKMNPQRSTVWXYZ]{26}$/;

describe('Checkpoint 1B: Canonical Identity Maintenance During Ingestion', () => {
  const TEST_LEAGUE_ID = 9939;
  const TEST_HOME_TEAM_ID = 9529;
  const TEST_AWAY_TEAM_ID = 9541;
  const TEST_FIXTURE_ID = 999157;

  const TEST_COMP_ID = `comp_${TEST_LEAGUE_ID}`;
  const TEST_SEASON_ID = `season_${TEST_LEAGUE_ID}_2025`;
  const TEST_HOME_TEAM_ID_STR = `team_${TEST_HOME_TEAM_ID}`;
  const TEST_AWAY_TEAM_ID_STR = `team_${TEST_AWAY_TEAM_ID}`;
  const TEST_FIXTURE_ID_STR = `fix_${TEST_FIXTURE_ID}`;

  const ALL_TEST_FIXTURE_IDS = [TEST_FIXTURE_ID_STR, `${TEST_FIXTURE_ID_STR}_null`, `${TEST_FIXTURE_ID_STR}_conflict`];
  const ALL_TEST_TEAM_IDS = [TEST_HOME_TEAM_ID_STR, TEST_AWAY_TEAM_ID_STR, `${TEST_HOME_TEAM_ID_STR}_null`, `${TEST_AWAY_TEAM_ID_STR}_null`];
  const ALL_TEST_COMP_IDS = [TEST_COMP_ID, `${TEST_COMP_ID}_null`];
  const ALL_TEST_SEASON_IDS = [TEST_SEASON_ID, `${TEST_SEASON_ID}_null`];

  const cleanupTestRecords = async () => {
    // 1. Delete fixture aliases for our test provider IDs
    await pool.query(
      `DELETE FROM fixture_provider_aliases WHERE provider = 'api_football' AND provider_id = ANY($1);`,
      [[String(TEST_FIXTURE_ID), `${TEST_FIXTURE_ID}_null`, `${TEST_FIXTURE_ID}_conflict`]]
    );

    // 2. Delete team aliases
    await pool.query(
      `DELETE FROM team_provider_aliases WHERE provider = 'api_football' AND provider_id = ANY($1);`,
      [[String(TEST_HOME_TEAM_ID), String(TEST_AWAY_TEAM_ID), `${TEST_HOME_TEAM_ID}_null`, `${TEST_AWAY_TEAM_ID}_null`]]
    );

    // 3. Delete competition aliases
    await pool.query(
      `DELETE FROM competition_provider_aliases WHERE provider = 'api_football' AND provider_id = ANY($1);`,
      [[String(TEST_LEAGUE_ID), `${TEST_LEAGUE_ID}_null`]]
    );

    // 4. Delete fixture child records and fixtures
    await pool.query(
      `DELETE FROM lineup_players WHERE lineup_id IN (SELECT id FROM lineups WHERE fixture_id = ANY($1));`,
      [ALL_TEST_FIXTURE_IDS]
    );
    await pool.query(`DELETE FROM lineups WHERE fixture_id = ANY($1);`, [ALL_TEST_FIXTURE_IDS]);
    await pool.query(`DELETE FROM fixture_statistics WHERE fixture_id = ANY($1);`, [ALL_TEST_FIXTURE_IDS]);
    await pool.query(`DELETE FROM events WHERE fixture_id = ANY($1);`, [ALL_TEST_FIXTURE_IDS]);
    await pool.query(`DELETE FROM fixtures WHERE id = ANY($1);`, [ALL_TEST_FIXTURE_IDS]);

    // 5. Delete seasons, standings, teams, competitions
    await pool.query(`DELETE FROM standings WHERE competition_id = ANY($1);`, [ALL_TEST_COMP_IDS]);
    await pool.query(`DELETE FROM competition_seasons WHERE id = ANY($1);`, [ALL_TEST_SEASON_IDS]);
    await pool.query(`DELETE FROM teams WHERE id = ANY($1);`, [ALL_TEST_TEAM_IDS]);
    await pool.query(`DELETE FROM competitions WHERE id = ANY($1);`, [ALL_TEST_COMP_IDS]);
  };

  beforeAll(async () => {
    // Target test DB verification
    const dbInfo = await pool.query<{ current_database: string }>('SELECT current_database();');
    if (dbInfo.rows[0].current_database !== 'footballtray_test') {
      throw new Error(`[SAFETY] Expected 'footballtray_test', but connected to '${dbInfo.rows[0].current_database}'`);
    }
    await cleanupTestRecords();
  });

  afterAll(async () => {
    await cleanupTestRecords();
    await pool.end();
  });

  beforeEach(async () => {
    await cleanupTestRecords();
  });

  function createMockApiFootballFixtureItem(overrides?: Partial<ApiFootballFixtureItem>): ApiFootballFixtureItem {
    return {
      fixture: {
        id: TEST_FIXTURE_ID,
        referee: 'M. Oliver',
        timezone: 'UTC',
        date: new Date(Date.now() + 3600000).toISOString(),
        timestamp: Math.floor(Date.now() / 1000) + 3600,
        periods: { first: null, second: null },
        venue: { id: 100, name: 'Test Stadium', city: 'London' },
        status: { long: 'Not Started', short: 'NS', elapsed: 0, extra: null },
      },
      league: {
        id: TEST_LEAGUE_ID,
        name: 'Test Premier League',
        country: 'England',
        logo: 'https://example.com/league.png',
        flag: null,
        season: 2025,
        round: 'Regular Season - 1',
      },
      teams: {
        home: { id: TEST_HOME_TEAM_ID, name: 'Test Home FC', logo: 'https://example.com/home.png', winner: null },
        away: { id: TEST_AWAY_TEAM_ID, name: 'Test Away FC', logo: 'https://example.com/away.png', winner: null },
      },
      goals: { home: 0, away: 0 },
      score: {
        halftime: { home: null, away: null },
        fulltime: { home: null, away: null },
        extratime: { home: null, away: null },
        penalty: { home: null, away: null },
      },
      events: [],
      ...overrides,
    };
  }

  function createProviderWithFixtures(providerName: string, fixtures: ProviderFixture[]): FootballProvider {
    return {
      providerName,
      getCompetitions: async () => [],
      getFixtures: async () => fixtures,
      getFixtureDetails: async () => null,
      getFixtureEvents: async () => [],
      getStandings: async () => [],
    };
  }

  it('1. New API-Football ingestion creates canonical IDs and provider aliases for competition, team, and fixture', async () => {
    const rawItem = createMockApiFootballFixtureItem();
    const mappedFixture = ApiFootballMapper.mapFixture(rawItem);

    expect(mappedFixture.provider).toBe('api_football');
    expect(mappedFixture.providerId).toBe(String(TEST_FIXTURE_ID));
    expect(mappedFixture.competition.provider).toBe('api_football');
    expect(mappedFixture.competition.providerId).toBe(String(TEST_LEAGUE_ID));
    expect(mappedFixture.homeTeam.provider).toBe('api_football');
    expect(mappedFixture.homeTeam.providerId).toBe(String(TEST_HOME_TEAM_ID));

    const provider = createProviderWithFixtures('api_football', [mappedFixture]);
    const syncService = new SyncService(provider);

    const summary = await syncService.sync();
    expect(summary.fixturesInserted).toBe(1);

    // 1. Check competition
    const comp = await db.query.competitions.findFirst({
      where: eq(schema.competitions.id, TEST_COMP_ID),
    });
    expect(comp).toBeDefined();
    expect(comp!.id).toBe(TEST_COMP_ID);
    expect(comp!.canonicalId).toBeDefined();
    expect(CANONICAL_COMP_REGEX.test(comp!.canonicalId!)).toBe(true);

    const compAlias = await db.query.competitionProviderAliases.findFirst({
      where: eq(schema.competitionProviderAliases.canonicalCompetitionId, comp!.canonicalId!),
    });
    expect(compAlias).toBeDefined();
    expect(compAlias!.provider).toBe('api_football');
    expect(compAlias!.providerId).toBe(String(TEST_LEAGUE_ID));

    // 2. Check teams
    const homeTeam = await db.query.teams.findFirst({
      where: eq(schema.teams.id, TEST_HOME_TEAM_ID_STR),
    });
    expect(homeTeam).toBeDefined();
    expect(homeTeam!.id).toBe(TEST_HOME_TEAM_ID_STR);
    expect(CANONICAL_TEAM_REGEX.test(homeTeam!.canonicalId!)).toBe(true);

    const homeTeamAlias = await db.query.teamProviderAliases.findFirst({
      where: eq(schema.teamProviderAliases.canonicalTeamId, homeTeam!.canonicalId!),
    });
    expect(homeTeamAlias).toBeDefined();
    expect(homeTeamAlias!.provider).toBe('api_football');
    expect(homeTeamAlias!.providerId).toBe(String(TEST_HOME_TEAM_ID));

    // 3. Check fixture
    const fix = await db.query.fixtures.findFirst({
      where: eq(schema.fixtures.id, TEST_FIXTURE_ID_STR),
    });
    expect(fix).toBeDefined();
    expect(fix!.id).toBe(TEST_FIXTURE_ID_STR);
    expect(CANONICAL_FIX_REGEX.test(fix!.canonicalId!)).toBe(true);

    const fixAlias = await db.query.fixtureProviderAliases.findFirst({
      where: eq(schema.fixtureProviderAliases.canonicalFixtureId, fix!.canonicalId!),
    });
    expect(fixAlias).toBeDefined();
    expect(fixAlias!.provider).toBe('api_football');
    expect(fixAlias!.providerId).toBe(String(TEST_FIXTURE_ID));
  });

  it('2. Existing NULL canonical_id is self-healed when touched by normal ingestion', async () => {
    // Under Migration 0004+, canonical_id is NOT NULL. Temporarily drop NOT NULL to test legacy self-healing
    await pool.query('ALTER TABLE competitions ALTER COLUMN canonical_id DROP NOT NULL;');
    await pool.query('ALTER TABLE teams ALTER COLUMN canonical_id DROP NOT NULL;');
    await pool.query('ALTER TABLE fixtures ALTER COLUMN canonical_id DROP NOT NULL;');

    try {
      // Seed existing legacy records with canonical_id = NULL
      await pool.query(
        `INSERT INTO competitions (id, canonical_id, name, created_at, updated_at)
         VALUES ($1, NULL, 'Legacy Null Comp', NOW(), NOW());`,
        [TEST_COMP_ID]
      );

      await pool.query(
        `INSERT INTO competition_seasons (id, competition_id, year, display_year, start_date, end_date, is_current, created_at)
         VALUES ($1, $2, 2025, '2025/2026', NOW(), NOW(), true, NOW());`,
        [TEST_SEASON_ID, TEST_COMP_ID]
      );

      await pool.query(
        `INSERT INTO teams (id, canonical_id, name, short_name, created_at, updated_at)
         VALUES ($1, NULL, 'Legacy Null Home', 'HOM', NOW(), NOW()),
                ($2, NULL, 'Legacy Null Away', 'AWY', NOW(), NOW());`,
        [TEST_HOME_TEAM_ID_STR, TEST_AWAY_TEAM_ID_STR]
      );

      await pool.query(
        `INSERT INTO fixtures (id, canonical_id, competition_id, season_id, home_team_id, away_team_id, kickoff_time, state, elapsed_minute, extra_minute, home_score, away_score, created_at, updated_at)
         VALUES ($1, NULL, $2, $3, $4, $5, NOW(), 'PREMATCH', 0, 0, 0, 0, NOW(), NOW());`,
        [TEST_FIXTURE_ID_STR, TEST_COMP_ID, TEST_SEASON_ID, TEST_HOME_TEAM_ID_STR, TEST_AWAY_TEAM_ID_STR]
      );

      // Verify initial NULL state
      const preComp = await db.query.competitions.findFirst({ where: eq(schema.competitions.id, TEST_COMP_ID) });
      const preFix = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, TEST_FIXTURE_ID_STR) });
      expect(preComp!.canonicalId).toBeNull();
      expect(preFix!.canonicalId).toBeNull();

      // Ingest via normal API-Football sync
      const rawItem = createMockApiFootballFixtureItem();
      const mappedFixture = ApiFootballMapper.mapFixture(rawItem);
      const provider = createProviderWithFixtures('api_football', [mappedFixture]);
      const syncService = new SyncService(provider);

      await syncService.sync();

      // Verify self-healed canonical IDs
      const postComp = await db.query.competitions.findFirst({ where: eq(schema.competitions.id, TEST_COMP_ID) });
      expect(postComp!.canonicalId).not.toBeNull();
      expect(CANONICAL_COMP_REGEX.test(postComp!.canonicalId!)).toBe(true);

      const postFix = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, TEST_FIXTURE_ID_STR) });
      expect(postFix!.canonicalId).not.toBeNull();
      expect(CANONICAL_FIX_REGEX.test(postFix!.canonicalId!)).toBe(true);

      // Verify aliases were established
      const compAlias = await db.query.competitionProviderAliases.findFirst({
        where: eq(schema.competitionProviderAliases.providerId, String(TEST_LEAGUE_ID)),
      });
      expect(compAlias).toBeDefined();
      expect(compAlias!.canonicalCompetitionId).toBe(postComp!.canonicalId);

      const fixAlias = await db.query.fixtureProviderAliases.findFirst({
        where: eq(schema.fixtureProviderAliases.providerId, String(TEST_FIXTURE_ID)),
      });
      expect(fixAlias).toBeDefined();
      expect(fixAlias!.canonicalFixtureId).toBe(postFix!.canonicalId);
    } finally {
      await cleanupTestRecords();
      await pool.query('ALTER TABLE competitions ALTER COLUMN canonical_id SET NOT NULL;');
      await pool.query('ALTER TABLE teams ALTER COLUMN canonical_id SET NOT NULL;');
      await pool.query('ALTER TABLE fixtures ALTER COLUMN canonical_id SET NOT NULL;');
    }
  });

  it('3. Existing non-null canonical_id is preserved and NEVER replaced on update or repeat ingestion', async () => {
    const customCompCanonical = generateCanonicalId('comp');
    const customHomeCanonical = generateCanonicalId('team');
    const customAwayCanonical = generateCanonicalId('team');
    const customFixCanonical = generateCanonicalId('fix');

    // Pre-insert records with explicit known canonical IDs
    await pool.query(
      `INSERT INTO competitions (id, canonical_id, name, created_at, updated_at)
       VALUES ($1, $2, 'Existing Comp', NOW(), NOW());`,
      [TEST_COMP_ID, customCompCanonical]
    );
    await pool.query(
      `INSERT INTO competition_provider_aliases (canonical_competition_id, provider, provider_id, first_seen_at, last_seen_at)
       VALUES ($1, 'api_football', $2, NOW(), NOW());`,
      [customCompCanonical, String(TEST_LEAGUE_ID)]
    );

    await pool.query(
      `INSERT INTO competition_seasons (id, competition_id, year, display_year, start_date, end_date, is_current, created_at)
       VALUES ($1, $2, 2025, '2025/2026', NOW(), NOW(), true, NOW());`,
      [TEST_SEASON_ID, TEST_COMP_ID]
    );

    await pool.query(
      `INSERT INTO teams (id, canonical_id, name, short_name, created_at, updated_at)
       VALUES ($1, $2, 'Home', 'HOM', NOW(), NOW()),
              ($3, $4, 'Away', 'AWY', NOW(), NOW());`,
      [TEST_HOME_TEAM_ID_STR, customHomeCanonical, TEST_AWAY_TEAM_ID_STR, customAwayCanonical]
    );
    await pool.query(
      `INSERT INTO team_provider_aliases (canonical_team_id, provider, provider_id, first_seen_at, last_seen_at)
       VALUES ($1, 'api_football', $2, NOW(), NOW()),
              ($3, 'api_football', $4, NOW(), NOW());`,
      [customHomeCanonical, String(TEST_HOME_TEAM_ID), customAwayCanonical, String(TEST_AWAY_TEAM_ID)]
    );

    await pool.query(
      `INSERT INTO fixtures (id, canonical_id, competition_id, season_id, home_team_id, away_team_id, kickoff_time, state, elapsed_minute, extra_minute, home_score, away_score, created_at, updated_at)
       VALUES ($1, $2, $3, $4, $5, $6, NOW(), 'PREMATCH', 0, 0, 0, 0, NOW(), NOW());`,
      [TEST_FIXTURE_ID_STR, customFixCanonical, TEST_COMP_ID, TEST_SEASON_ID, TEST_HOME_TEAM_ID_STR, TEST_AWAY_TEAM_ID_STR]
    );
    await pool.query(
      `INSERT INTO fixture_provider_aliases (canonical_fixture_id, provider, provider_id, first_seen_at, last_seen_at)
       VALUES ($1, 'api_football', $2, NOW(), NOW());`,
      [customFixCanonical, String(TEST_FIXTURE_ID)]
    );

    // Run normal API-Football sync with updated live state (1-0, 55th min)
    const rawItem = createMockApiFootballFixtureItem({
      fixture: {
        id: TEST_FIXTURE_ID,
        referee: 'M. Oliver',
        timezone: 'UTC',
        date: new Date().toISOString(),
        timestamp: Math.floor(Date.now() / 1000),
        periods: { first: null, second: null },
        venue: { id: 100, name: 'Test Stadium', city: 'London' },
        status: { long: 'Second Half', short: '2H', elapsed: 55, extra: null },
      },
      goals: { home: 1, away: 0 },
    });
    const mappedFixture = ApiFootballMapper.mapFixture(rawItem);
    const provider = createProviderWithFixtures('api_football', [mappedFixture]);
    const syncService = new SyncService(provider);

    const summary = await syncService.sync();
    expect(summary.fixturesUpdated).toBe(1);

    // Verify fixture updated score & state
    const fix = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, TEST_FIXTURE_ID_STR) });
    expect(fix!.state).toBe('LIVE');
    expect(fix!.elapsedMinute).toBe(55);
    expect(fix!.homeScore).toBe(1);

    // CRITICAL: Verify canonical IDs remain exactly identical
    expect(fix!.canonicalId).toBe(customFixCanonical);

    const comp = await db.query.competitions.findFirst({ where: eq(schema.competitions.id, TEST_COMP_ID) });
    expect(comp!.canonicalId).toBe(customCompCanonical);

    const homeTeam = await db.query.teams.findFirst({ where: eq(schema.teams.id, TEST_HOME_TEAM_ID_STR) });
    expect(homeTeam!.canonicalId).toBe(customHomeCanonical);
  });

  it('4. Repeated sync passes are completely idempotent with no identity churn or duplicate aliases', async () => {
    const rawItem = createMockApiFootballFixtureItem();
    const mappedFixture = ApiFootballMapper.mapFixture(rawItem);
    const provider = createProviderWithFixtures('api_football', [mappedFixture]);
    const syncService = new SyncService(provider);

    // Pass 1: Insert
    await syncService.sync();
    const fix1 = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, TEST_FIXTURE_ID_STR) });
    const canonicalId1 = fix1!.canonicalId;

    // Pass 2: Identical data rerun
    const summary2 = await syncService.sync();
    expect(summary2.fixturesInserted).toBe(0);
    expect(summary2.fixturesUpdated).toBe(0);

    const fix2 = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, TEST_FIXTURE_ID_STR) });
    expect(fix2!.canonicalId).toBe(canonicalId1);

    // Verify exactly one alias row exists
    const aliases = await db
      .select()
      .from(schema.fixtureProviderAliases)
      .where(eq(schema.fixtureProviderAliases.providerId, String(TEST_FIXTURE_ID)));
    expect(aliases.length).toBe(1);
    expect(aliases[0].canonicalFixtureId).toBe(canonicalId1);
  });

  it('5. Provider alias conflict fails closed and aborts transaction without corrupting data', async () => {
    // Setup a conflicting pre-existing alias pointing to another canonical entity
    const foreignCanonical = generateCanonicalId('fix');

    // Create foreign fixture to satisfy FK constraint
    await pool.query(
      `INSERT INTO competitions (id, canonical_id, name, created_at, updated_at)
       VALUES ($1, $2, 'Conflict Comp', NOW(), NOW());`,
      [TEST_COMP_ID, generateCanonicalId('comp')]
    );
    await pool.query(
      `INSERT INTO competition_seasons (id, competition_id, year, display_year, start_date, end_date, is_current, created_at)
       VALUES ($1, $2, 2025, '2025/2026', NOW(), NOW(), true, NOW());`,
      [TEST_SEASON_ID, TEST_COMP_ID]
    );
    await pool.query(
      `INSERT INTO teams (id, canonical_id, name, short_name, created_at, updated_at)
       VALUES ($1, $2, 'Conflict Home', 'CHOM', NOW(), NOW()),
              ($3, $4, 'Conflict Away', 'CAWY', NOW(), NOW());`,
      [TEST_HOME_TEAM_ID_STR, generateCanonicalId('team'), TEST_AWAY_TEAM_ID_STR, generateCanonicalId('team')]
    );
    await pool.query(
      `INSERT INTO fixtures (id, canonical_id, competition_id, season_id, home_team_id, away_team_id, kickoff_time, state, elapsed_minute, extra_minute, home_score, away_score, created_at, updated_at)
       VALUES ('fix_conflict_foreign', $1, $2, $3, $4, $5, NOW(), 'PREMATCH', 0, 0, 0, 0, NOW(), NOW());`,
      [foreignCanonical, TEST_COMP_ID, TEST_SEASON_ID, TEST_HOME_TEAM_ID_STR, TEST_AWAY_TEAM_ID_STR]
    );

    // Pre-insert alias mapping provider_id 999157 to the foreign canonical fixture
    await pool.query(
      `INSERT INTO fixture_provider_aliases (canonical_fixture_id, provider, provider_id, first_seen_at, last_seen_at)
       VALUES ($1, 'api_football', $2, NOW(), NOW());`,
      [foreignCanonical, String(TEST_FIXTURE_ID)]
    );

    // Now attempt to sync a new fixture with provider_id 999157
    const rawItem = createMockApiFootballFixtureItem();
    const mappedFixture = ApiFootballMapper.mapFixture(rawItem);
    const provider = createProviderWithFixtures('api_football', [mappedFixture]);
    const syncService = new SyncService(provider);

    // Must fail closed with error
    await expect(syncService.sync()).rejects.toThrow(/Conflicting provider alias detected/);

    // Verify rollback: fixture TEST_FIXTURE_ID_STR was NOT committed
    const fix = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, TEST_FIXTURE_ID_STR) });
    expect(fix).toBeUndefined();

    // Clean up foreign fixture
    await pool.query(`DELETE FROM fixture_provider_aliases WHERE canonical_fixture_id = $1;`, [foreignCanonical]);
    await pool.query(`DELETE FROM fixtures WHERE id = 'fix_conflict_foreign';`);
  });

  it('6. Mock provider ingestion assigns canonical IDs but NEVER creates api_football aliases', async () => {
    const mockProvider = new MockFootballProvider();
    const syncService = new SyncService(mockProvider);

    await syncService.sync();

    // Check seeded mock fixture: fix_elclasico_2026
    const elClasico = await db.query.fixtures.findFirst({
      where: eq(schema.fixtures.id, 'fix_elclasico_2026'),
    });
    expect(elClasico).toBeDefined();
    expect(elClasico!.canonicalId).toBeDefined();
    expect(CANONICAL_FIX_REGEX.test(elClasico!.canonicalId!)).toBe(true);

    // CRITICAL: Ensure NO api_football alias exists for mock fixture or teams
    const fixAlias = await db.query.fixtureProviderAliases.findFirst({
      where: eq(schema.fixtureProviderAliases.canonicalFixtureId, elClasico!.canonicalId!),
    });
    expect(fixAlias).toBeUndefined();

    const barca = await db.query.teams.findFirst({ where: eq(schema.teams.id, 'team_barcelona') });
    expect(barca).toBeDefined();
    expect(barca!.canonicalId).toBeDefined();
    const barcaAlias = await db.query.teamProviderAliases.findFirst({
      where: eq(schema.teamProviderAliases.canonicalTeamId, barca!.canonicalId!),
    });
    expect(barcaAlias).toBeUndefined();
  });

  it('7. Public v1 API contract preserves legacy IDs and leaks zero canonical identity', async () => {
    // Ingest our test fixture
    const rawItem = createMockApiFootballFixtureItem();
    const mappedFixture = ApiFootballMapper.mapFixture(rawItem);
    const provider = createProviderWithFixtures('api_football', [mappedFixture]);
    const syncService = new SyncService(provider);
    await syncService.sync();

    const server = await buildServer();

    // 1. GET /api/tray
    const trayRes = await server.inject({
      method: 'GET',
      url: '/api/tray',
    });
    expect(trayRes.statusCode).toBe(200);
    const trayPayload = trayRes.json();
    const trayMatches = trayPayload.data?.matches || [];
    expect(Array.isArray(trayMatches)).toBe(true);
    expect(trayMatches.length).toBeGreaterThan(0);

    for (const match of trayMatches) {
      expect(match.id).toMatch(/^(fix_|repo_)/);
      expect(match).not.toHaveProperty('canonicalId');
      expect(match).not.toHaveProperty('canonical_id');
      if (match.competition) {
        expect(match.competition).not.toHaveProperty('canonicalId');
        expect(match.competition).not.toHaveProperty('canonical_id');
      }
      if (match.homeTeam) {
        expect(match.homeTeam).not.toHaveProperty('canonicalId');
        expect(match.homeTeam).not.toHaveProperty('canonical_id');
      }
      if (match.awayTeam) {
        expect(match.awayTeam).not.toHaveProperty('canonicalId');
        expect(match.awayTeam).not.toHaveProperty('canonical_id');
      }
    }

    // 2. GET /api/match/:id
    const matchRes = await server.inject({
      method: 'GET',
      url: `/api/match/${TEST_FIXTURE_ID_STR}`,
    });
    expect(matchRes.statusCode).toBe(200);
    const matchData = matchRes.json().data;
    expect(matchData).toBeDefined();
    expect(matchData.id).toBe(TEST_FIXTURE_ID_STR);
    expect(matchData).not.toHaveProperty('canonicalId');
    expect(matchData).not.toHaveProperty('canonical_id');
    expect(matchData.competition).not.toHaveProperty('canonicalId');
    expect(matchData.competition).not.toHaveProperty('canonical_id');
    expect(matchData.homeTeam).not.toHaveProperty('canonicalId');
    expect(matchData.homeTeam).not.toHaveProperty('canonical_id');

    await server.close();
  });

  it('8. Competition alias conflict fails closed and rolls back cleanly', async () => {
    const foreignCompCanonical = generateCanonicalId('comp');
    await pool.query(
      `INSERT INTO competitions (id, canonical_id, name, created_at, updated_at)
       VALUES ('comp_conflict_foreign', $1, 'Foreign Comp', NOW(), NOW());`,
      [foreignCompCanonical]
    );
    await pool.query(
      `INSERT INTO competition_provider_aliases (canonical_competition_id, provider, provider_id, first_seen_at, last_seen_at)
       VALUES ($1, 'api_football', $2, NOW(), NOW());`,
      [foreignCompCanonical, String(TEST_LEAGUE_ID)]
    );

    const rawItem = createMockApiFootballFixtureItem();
    const mappedFixture = ApiFootballMapper.mapFixture(rawItem);
    const provider = createProviderWithFixtures('api_football', [mappedFixture]);
    const syncService = new SyncService(provider);

    await expect(syncService.sync()).rejects.toThrow(/Conflicting provider alias detected in competition_provider_aliases/);

    // Verify comp was NOT inserted
    const comp = await db.query.competitions.findFirst({ where: eq(schema.competitions.id, TEST_COMP_ID) });
    expect(comp).toBeUndefined();

    // Clean up
    await pool.query(`DELETE FROM competition_provider_aliases WHERE canonical_competition_id = $1;`, [foreignCompCanonical]);
    await pool.query(`DELETE FROM competitions WHERE id = 'comp_conflict_foreign';`);
  });

  it('9. Team alias conflict fails closed and rolls back cleanly', async () => {
    const foreignTeamCanonical = generateCanonicalId('team');
    await pool.query(
      `INSERT INTO teams (id, canonical_id, name, short_name, created_at, updated_at)
       VALUES ('team_conflict_foreign', $1, 'Foreign Team', 'FTM', NOW(), NOW());`,
      [foreignTeamCanonical]
    );
    await pool.query(
      `INSERT INTO team_provider_aliases (canonical_team_id, provider, provider_id, first_seen_at, last_seen_at)
       VALUES ($1, 'api_football', $2, NOW(), NOW());`,
      [foreignTeamCanonical, String(TEST_HOME_TEAM_ID)]
    );

    const rawItem = createMockApiFootballFixtureItem();
    const mappedFixture = ApiFootballMapper.mapFixture(rawItem);
    const provider = createProviderWithFixtures('api_football', [mappedFixture]);
    const syncService = new SyncService(provider);

    await expect(syncService.sync()).rejects.toThrow(/Conflicting provider alias detected in team_provider_aliases/);

    // Verify team was NOT inserted
    const team = await db.query.teams.findFirst({ where: eq(schema.teams.id, TEST_HOME_TEAM_ID_STR) });
    expect(team).toBeUndefined();

    // Clean up
    await pool.query(`DELETE FROM team_provider_aliases WHERE canonical_team_id = $1;`, [foreignTeamCanonical]);
    await pool.query(`DELETE FROM teams WHERE id = 'team_conflict_foreign';`);
  });

  it('10. Concurrent ingestion passes for the same entity are race-safe and preserve canonical ID', async () => {
    const rawItem = createMockApiFootballFixtureItem();
    const mappedFixture = ApiFootballMapper.mapFixture(rawItem);
    const provider = createProviderWithFixtures('api_football', [mappedFixture]);

    const syncService1 = new SyncService(provider);
    const syncService2 = new SyncService(provider);

    // Launch both syncs concurrently
    const [res1, res2] = await Promise.all([syncService1.sync(), syncService2.sync()]);
    expect(res1.fixturesInserted + res2.fixturesInserted).toBeGreaterThanOrEqual(1);

    const fix = await db.query.fixtures.findFirst({ where: eq(schema.fixtures.id, TEST_FIXTURE_ID_STR) });
    expect(fix).toBeDefined();
    expect(CANONICAL_FIX_REGEX.test(fix!.canonicalId!)).toBe(true);

    // Ensure only 1 alias exists
    const aliases = await db
      .select()
      .from(schema.fixtureProviderAliases)
      .where(eq(schema.fixtureProviderAliases.providerId, String(TEST_FIXTURE_ID)));
    expect(aliases.length).toBe(1);
    expect(aliases[0].canonicalFixtureId).toBe(fix!.canonicalId);
  });
});
