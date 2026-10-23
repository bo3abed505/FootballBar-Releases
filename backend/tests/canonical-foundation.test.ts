import { describe, it, expect, beforeAll, afterAll, beforeEach } from 'vitest';
import pg from 'pg';
import dotenv from 'dotenv';
import { buildServer } from '../src/server.js';
import { FastifyInstance } from 'fastify';
import { backfillCanonical, isRecognizedApiFootballId } from '../src/db/backfill-canonical.js';
import {
  generateCanonicalId,
  CROCKFORD_ALPHABET,
  CANONICAL_PAYLOAD_LENGTH,
  encodeTime,
  encodeEntropy,
} from '../src/db/canonical-id.js';

dotenv.config();

const { Pool } = pg;

// ===========================================================================
// In-Memory Database Double for Isolated backfillCanonical() Unit Testing
// ===========================================================================

interface EntityRecord {
  id: string;
  canonical_id: string | null;
}

interface AliasRecord {
  canonical_id: string;
  provider: string;
  provider_id: string;
}

class FakeDatabase {
  dbName: string;
  inTransaction: boolean = false;
  executedQueries: string[] = [];

  competitions = new Map<string, EntityRecord>();
  teams = new Map<string, EntityRecord>();
  fixtures = new Map<string, EntityRecord>();

  competitionAliases = new Map<string, AliasRecord>(); // key: `${provider}:${provider_id}`
  teamAliases = new Map<string, AliasRecord>();
  fixtureAliases = new Map<string, AliasRecord>();

  private snapshot?: {
    competitions: Map<string, EntityRecord>;
    teams: Map<string, EntityRecord>;
    fixtures: Map<string, EntityRecord>;
    competitionAliases: Map<string, AliasRecord>;
    teamAliases: Map<string, AliasRecord>;
    fixtureAliases: Map<string, AliasRecord>;
  };

  constructor(dbName: string = 'footballtray_test') {
    this.dbName = dbName;
  }

  createClient(): pg.PoolClient {
    return {
      query: async (sql: string, params: any[] = []) => {
        const norm = sql.trim().replace(/\s+/g, ' ');
        const upper = norm.toUpperCase();

        if (upper.startsWith('SELECT CURRENT_DATABASE()')) {
          this.executedQueries.push('SELECT current_database()');
          return { rows: [{ db_name: this.dbName }], rowCount: 1 };
        }

        if (upper === 'BEGIN') {
          this.inTransaction = true;
          this.snapshot = {
            competitions: new Map(Array.from(this.competitions.entries()).map(([k, v]) => [k, { ...v }])),
            teams: new Map(Array.from(this.teams.entries()).map(([k, v]) => [k, { ...v }])),
            fixtures: new Map(Array.from(this.fixtures.entries()).map(([k, v]) => [k, { ...v }])),
            competitionAliases: new Map(Array.from(this.competitionAliases.entries()).map(([k, v]) => [k, { ...v }])),
            teamAliases: new Map(Array.from(this.teamAliases.entries()).map(([k, v]) => [k, { ...v }])),
            fixtureAliases: new Map(Array.from(this.fixtureAliases.entries()).map(([k, v]) => [k, { ...v }])),
          };
          this.executedQueries.push('BEGIN');
          return { rows: [], rowCount: 0 };
        }

        if (upper === 'COMMIT') {
          this.inTransaction = false;
          this.snapshot = undefined;
          this.executedQueries.push('COMMIT');
          return { rows: [], rowCount: 0 };
        }

        if (upper === 'ROLLBACK') {
          this.inTransaction = false;
          if (this.snapshot) {
            this.competitions = this.snapshot.competitions;
            this.teams = this.snapshot.teams;
            this.fixtures = this.snapshot.fixtures;
            this.competitionAliases = this.snapshot.competitionAliases;
            this.teamAliases = this.snapshot.teamAliases;
            this.fixtureAliases = this.snapshot.fixtureAliases;
          }
          this.executedQueries.push('ROLLBACK');
          return { rows: [], rowCount: 0 };
        }

        // SELECT entities
        if (upper.includes('FROM "COMPETITIONS"') && upper.includes('SELECT ID, CANONICAL_ID')) {
          this.executedQueries.push('SELECT competitions');
          return {
            rows: Array.from(this.competitions.values()).sort((a, b) => a.id.localeCompare(b.id)),
            rowCount: this.competitions.size,
          };
        }

        if (upper.includes('FROM "TEAMS"') && upper.includes('SELECT ID, CANONICAL_ID')) {
          this.executedQueries.push('SELECT teams');
          return {
            rows: Array.from(this.teams.values()).sort((a, b) => a.id.localeCompare(b.id)),
            rowCount: this.teams.size,
          };
        }

        if (upper.includes('FROM "FIXTURES"') && upper.includes('SELECT ID, CANONICAL_ID')) {
          this.executedQueries.push('SELECT fixtures');
          return {
            rows: Array.from(this.fixtures.values()).sort((a, b) => a.id.localeCompare(b.id)),
            rowCount: this.fixtures.size,
          };
        }

        // UPDATE entities
        if (upper.startsWith('UPDATE "COMPETITIONS"')) {
          const [canId, id] = params;
          this.executedQueries.push(`UPDATE competitions ${id} -> ${canId}`);
          const row = this.competitions.get(id);
          if (row && row.canonical_id === null) {
            row.canonical_id = canId;
            return { rows: [], rowCount: 1 };
          }
          return { rows: [], rowCount: 0 };
        }

        if (upper.startsWith('UPDATE "TEAMS"')) {
          const [canId, id] = params;
          this.executedQueries.push(`UPDATE teams ${id} -> ${canId}`);
          const row = this.teams.get(id);
          if (row && row.canonical_id === null) {
            row.canonical_id = canId;
            return { rows: [], rowCount: 1 };
          }
          return { rows: [], rowCount: 0 };
        }

        if (upper.startsWith('UPDATE "FIXTURES"')) {
          const [canId, id] = params;
          this.executedQueries.push(`UPDATE fixtures ${id} -> ${canId}`);
          const row = this.fixtures.get(id);
          if (row && row.canonical_id === null) {
            row.canonical_id = canId;
            return { rows: [], rowCount: 1 };
          }
          return { rows: [], rowCount: 0 };
        }

        // INSERT aliases (ON CONFLICT DO NOTHING)
        if (upper.startsWith('INSERT INTO "COMPETITION_PROVIDER_ALIASES"')) {
          const [canId, prov, provId] = params;
          const key = `${prov}:${provId}`;
          this.executedQueries.push(`INSERT competition_provider_aliases ${key} -> ${canId}`);
          if (this.competitionAliases.has(key)) {
            return { rows: [], rowCount: 0 };
          }
          this.competitionAliases.set(key, { canonical_id: canId, provider: prov, provider_id: provId });
          return { rows: [], rowCount: 1 };
        }

        if (upper.startsWith('INSERT INTO "TEAM_PROVIDER_ALIASES"')) {
          const [canId, prov, provId] = params;
          const key = `${prov}:${provId}`;
          this.executedQueries.push(`INSERT team_provider_aliases ${key} -> ${canId}`);
          if (this.teamAliases.has(key)) {
            return { rows: [], rowCount: 0 };
          }
          this.teamAliases.set(key, { canonical_id: canId, provider: prov, provider_id: provId });
          return { rows: [], rowCount: 1 };
        }

        if (upper.startsWith('INSERT INTO "FIXTURE_PROVIDER_ALIASES"')) {
          const [canId, prov, provId] = params;
          const key = `${prov}:${provId}`;
          this.executedQueries.push(`INSERT fixture_provider_aliases ${key} -> ${canId}`);
          if (this.fixtureAliases.has(key)) {
            return { rows: [], rowCount: 0 };
          }
          this.fixtureAliases.set(key, { canonical_id: canId, provider: prov, provider_id: provId });
          return { rows: [], rowCount: 1 };
        }

        // SELECT existing alias
        if (upper.includes('FROM "COMPETITION_PROVIDER_ALIASES"')) {
          const [prov, provId] = params;
          const key = `${prov}:${provId}`;
          this.executedQueries.push(`SELECT competition_provider_aliases ${key}`);
          const row = this.competitionAliases.get(key);
          return {
            rows: row ? [{ canonical_competition_id: row.canonical_id }] : [],
            rowCount: row ? 1 : 0,
          };
        }

        if (upper.includes('FROM "TEAM_PROVIDER_ALIASES"')) {
          const [prov, provId] = params;
          const key = `${prov}:${provId}`;
          this.executedQueries.push(`SELECT team_provider_aliases ${key}`);
          const row = this.teamAliases.get(key);
          return {
            rows: row ? [{ canonical_team_id: row.canonical_id }] : [],
            rowCount: row ? 1 : 0,
          };
        }

        if (upper.includes('FROM "FIXTURE_PROVIDER_ALIASES"')) {
          const [prov, provId] = params;
          const key = `${prov}:${provId}`;
          this.executedQueries.push(`SELECT fixture_provider_aliases ${key}`);
          const row = this.fixtureAliases.get(key);
          return {
            rows: row ? [{ canonical_fixture_id: row.canonical_id }] : [],
            rowCount: row ? 1 : 0,
          };
        }

        throw new Error(`Unhandled SQL query in FakeDatabase: ${sql}`);
      },
      release: () => { },
    } as unknown as pg.PoolClient;
  }
}

// ===========================================================================
// Real PostgreSQL Integration Tests & In-Memory Backfill Suite
// ===========================================================================

const hasPostgresTestUrl = Boolean(process.env.DATABASE_URL_TEST && process.env.DATABASE_URL_TEST.startsWith('postgres'));

describe.skipIf(!hasPostgresTestUrl)('Additive Canonical Foundation & Existing-Data Backfill (Checkpoint 1A)', () => {
  const testUrl = process.env.DATABASE_URL_TEST || '';

  let pool: pg.Pool;
  let app: FastifyInstance;

  // Dedicated owned IDs for test isolation (zero blanket table DELETEs)
  const TEST_COMPETITION_IDS = ['comp_c1a_test_39'];
  const TEST_SEASON_IDS = ['comp_c1a_test_39_2026'];
  const TEST_TEAM_IDS = ['team_c1a_test_33', 'team_c1a_test_42', 'team_c1a_dup'];
  const TEST_FIXTURE_IDS = ['fix_c1a_test_1001'];
  const KNOWN_PROVIDER_IDS = ['c1a_prov_42', 'c1a_prov_1001', '42'];

  async function cleanupOwnedRecords(clientOrPool: pg.Pool | pg.PoolClient) {
    // 1. Delete aliases owned by test entities
    await clientOrPool.query(`
      DELETE FROM "fixture_provider_aliases"
      WHERE "canonical_fixture_id" IN (
        SELECT "canonical_id" FROM "fixtures" WHERE "id" = ANY($1) AND "canonical_id" IS NOT NULL
      ) OR "provider_id" = ANY($2);
    `, [TEST_FIXTURE_IDS, KNOWN_PROVIDER_IDS]);

    await clientOrPool.query(`
      DELETE FROM "team_provider_aliases"
      WHERE "canonical_team_id" IN (
        SELECT "canonical_id" FROM "teams" WHERE "id" = ANY($1) AND "canonical_id" IS NOT NULL
      ) OR "provider_id" = ANY($2);
    `, [TEST_TEAM_IDS, KNOWN_PROVIDER_IDS]);

    await clientOrPool.query(`
      DELETE FROM "competition_provider_aliases"
      WHERE "canonical_competition_id" IN (
        SELECT "canonical_id" FROM "competitions" WHERE "id" = ANY($1) AND "canonical_id" IS NOT NULL
      ) OR "provider_id" = ANY($2);
    `, [TEST_COMPETITION_IDS, KNOWN_PROVIDER_IDS]);

    // 2. Delete fixture-associated child records in FK-safe order
    await clientOrPool.query(`
      DELETE FROM "lineup_players"
      WHERE "lineup_id" IN (SELECT "id" FROM "lineups" WHERE "fixture_id" = ANY($1));
    `, [TEST_FIXTURE_IDS]);
    await clientOrPool.query('DELETE FROM "lineups" WHERE "fixture_id" = ANY($1);', [TEST_FIXTURE_IDS]);
    await clientOrPool.query('DELETE FROM "events" WHERE "fixture_id" = ANY($1);', [TEST_FIXTURE_IDS]);
    await clientOrPool.query('DELETE FROM "fixture_statistics" WHERE "fixture_id" = ANY($1);', [TEST_FIXTURE_IDS]);
    await clientOrPool.query('DELETE FROM "standings" WHERE "competition_id" = ANY($1);', [TEST_COMPETITION_IDS]);

    // 3. Delete fixtures
    await clientOrPool.query('DELETE FROM "fixtures" WHERE "id" = ANY($1);', [TEST_FIXTURE_IDS]);

    // 4. Delete teams
    await clientOrPool.query('DELETE FROM "teams" WHERE "id" = ANY($1);', [TEST_TEAM_IDS]);

    // 5. Delete competition seasons
    await clientOrPool.query(
      'DELETE FROM "competition_seasons" WHERE "id" = ANY($1) OR "competition_id" = ANY($2);',
      [TEST_SEASON_IDS, TEST_COMPETITION_IDS]
    );

    // 6. Delete competitions
    await clientOrPool.query('DELETE FROM "competitions" WHERE "id" = ANY($1);', [TEST_COMPETITION_IDS]);
  }

  beforeAll(async () => {
    pool = new Pool({
      connectionString: testUrl,
      max: 5,
    });

    const checkRes = await pool.query('SELECT current_database() AS db_name;');
    const dbName = checkRes.rows[0]?.db_name;
    if (dbName !== 'footballtray_test') {
      throw new Error(`CRITICAL TEST SAFETY VIOLATION: Connected to '${dbName}' instead of 'footballtray_test'.`);
    }

    await cleanupOwnedRecords(pool);
    app = await buildServer();
  });

  afterAll(async () => {
    await cleanupOwnedRecords(pool);
    await app.close();
    await pool.end();
  });

  beforeEach(async () => {
    await cleanupOwnedRecords(pool);
  });

  // -------------------------------------------------------------------------
  // Part A: Real PostgreSQL Integration Tests (footballtray_test)
  // -------------------------------------------------------------------------
  describe('Part A: Real PostgreSQL Integration Tests', () => {
    it('A.1: Migration creates exactly the three NOT NULL canonical_id columns (0004+)', async () => {
      const colRes = await pool.query<{ table_name: string; column_name: string; data_type: string; is_nullable: string }>(`
        SELECT table_name, column_name, data_type, is_nullable
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND column_name = 'canonical_id'
        ORDER BY table_name;
      `);

      expect(colRes.rows).toHaveLength(3);
      const tables = colRes.rows.map(r => r.table_name);
      expect(tables).toEqual(['competitions', 'fixtures', 'teams']);

      for (const row of colRes.rows) {
        expect(row.data_type).toBe('text');
        expect(row.is_nullable).toBe('NO');
      }
    });

    it('A.2: Migration creates exactly the three typed alias tables and no season_provider_aliases', async () => {
      const tableRes = await pool.query<{ table_name: string }>(`
        SELECT table_name
        FROM information_schema.tables
        WHERE table_schema = 'public'
          AND table_name LIKE '%_provider_aliases'
        ORDER BY table_name;
      `);

      const aliasTables = tableRes.rows.map(r => r.table_name);
      expect(aliasTables).toEqual([
        'competition_provider_aliases',
        'fixture_provider_aliases',
        'team_provider_aliases',
      ]);
      expect(aliasTables).not.toContain('season_provider_aliases');
    });

    it('A.3: competition_seasons does NOT have a canonical_id column', async () => {
      const colRes = await pool.query(`
        SELECT column_name
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'competition_seasons'
          AND column_name = 'canonical_id';
      `);
      expect(colRes.rows).toHaveLength(0);
    });

    it('A.4: Canonical FK integrity is enforced on alias tables', async () => {
      // Attempt to insert an alias with non-existent canonical ID -> must fail foreign key check
      await expect(
        pool.query(`
          INSERT INTO "competition_provider_aliases"
          ("canonical_competition_id", "provider", "provider_id", "first_seen_at", "last_seen_at")
          VALUES ('ft_comp_00000000000000000000000000', 'api_football', '999999', NOW(), NOW());
        `)
      ).rejects.toThrow(/violates foreign key constraint/);
    });

    it('A.5: Alias natural identity UNIQUE(provider, provider_id) is enforced', async () => {
      const canId = generateCanonicalId('team');
      await pool.query(`
        INSERT INTO "teams" ("id", "canonical_id", "name", "short_name")
        VALUES ('team_c1a_dup', $1, 'Duplicate Test Team', 'DUP');
      `, [canId]);

      await pool.query(`
        INSERT INTO "team_provider_aliases"
        ("canonical_team_id", "provider", "provider_id", "first_seen_at", "last_seen_at")
        VALUES ($1, 'api_football', '42', NOW(), NOW());
      `, [canId]);

      // Inserting duplicate (provider, provider_id) must fail with unique/PK violation
      await expect(
        pool.query(`
          INSERT INTO "team_provider_aliases"
          ("canonical_team_id", "provider", "provider_id", "first_seen_at", "last_seen_at")
          VALUES ($1, 'api_football', '42', NOW(), NOW());
        `, [canId])
      ).rejects.toThrow(/violates unique constraint.*provider_id_pk|violates primary key/);
    });

    it('A.6: Physical legacy PKs, FKs, and relationships remain unchanged', async () => {
      const compCanId = generateCanonicalId('comp');
      const homeCanId = generateCanonicalId('team');
      const awayCanId = generateCanonicalId('team');
      const fixCanId = generateCanonicalId('fix');

      await pool.query(
        'INSERT INTO "competitions" ("id", "canonical_id", "name") VALUES ($1, $2, $3);',
        ['comp_c1a_test_39', compCanId, 'Premier League']
      );
      await pool.query(
        'INSERT INTO "competition_seasons" ("id", "competition_id", "year", "display_year", "start_date", "end_date", "is_current") VALUES ($1, $2, 2026, $3, NOW(), NOW(), true);',
        ['comp_c1a_test_39_2026', 'comp_c1a_test_39', '2026/27']
      );
      await pool.query(
        'INSERT INTO "teams" ("id", "canonical_id", "name", "short_name") VALUES ($1, $2, $3, $4);',
        ['team_c1a_test_33', homeCanId, 'Manchester United', 'MUN']
      );
      await pool.query(
        'INSERT INTO "teams" ("id", "canonical_id", "name", "short_name") VALUES ($1, $2, $3, $4);',
        ['team_c1a_test_42', awayCanId, 'Arsenal', 'ARS']
      );
      await pool.query(
        'INSERT INTO "fixtures" ("id", "canonical_id", "competition_id", "season_id", "home_team_id", "away_team_id", "kickoff_time", "state", "home_score", "away_score") VALUES ($1, $2, $3, $4, $5, $6, NOW(), $7, 2, 1);',
        ['fix_c1a_test_1001', fixCanId, 'comp_c1a_test_39', 'comp_c1a_test_39_2026', 'team_c1a_test_33', 'team_c1a_test_42', 'LIVE']
      );

      const comp = await pool.query('SELECT id, canonical_id FROM "competitions" WHERE id = $1;', ['comp_c1a_test_39']);
      expect(comp.rows[0].id).toBe('comp_c1a_test_39');
      expect(comp.rows[0].canonical_id).toBe(compCanId);

      const fix = await pool.query(
        'SELECT id, canonical_id, competition_id, home_team_id, away_team_id FROM "fixtures" WHERE id = $1;',
        ['fix_c1a_test_1001']
      );
      expect(fix.rows[0].id).toBe('fix_c1a_test_1001');
      expect(fix.rows[0].canonical_id).toBe(fixCanId);
      expect(fix.rows[0].competition_id).toBe('comp_c1a_test_39');
      expect(fix.rows[0].home_team_id).toBe('team_c1a_test_33');
      expect(fix.rows[0].away_team_id).toBe('team_c1a_test_42');
    });

    it('A.7: GET /api/tray returns legacy IDs and never leaks canonical_id or ft_*', async () => {
      const compCanId = generateCanonicalId('comp');
      const homeCanId = generateCanonicalId('team');
      const awayCanId = generateCanonicalId('team');
      const fixCanId = generateCanonicalId('fix');

      await pool.query(
        'INSERT INTO "competitions" ("id", "canonical_id", "name", "code") VALUES ($1, $2, $3, $4);',
        ['comp_c1a_test_39', compCanId, 'Premier League', 'PL']
      );
      await pool.query(
        'INSERT INTO "competition_seasons" ("id", "competition_id", "year", "display_year", "start_date", "end_date", "is_current") VALUES ($1, $2, 2026, $3, NOW(), NOW(), true);',
        ['comp_c1a_test_39_2026', 'comp_c1a_test_39', '2026/27']
      );
      await pool.query(
        'INSERT INTO "teams" ("id", "canonical_id", "name", "short_name", "logo_url") VALUES ($1, $2, $3, $4, $5);',
        ['team_c1a_test_33', homeCanId, 'Manchester United', 'MUN', 'https://media.api-sports.io/football/teams/33.png']
      );
      await pool.query(
        'INSERT INTO "teams" ("id", "canonical_id", "name", "short_name", "logo_url") VALUES ($1, $2, $3, $4, $5);',
        ['team_c1a_test_42', awayCanId, 'Arsenal', 'ARS', 'https://media.api-sports.io/football/teams/42.png']
      );
      await pool.query(
        'INSERT INTO "fixtures" ("id", "canonical_id", "competition_id", "season_id", "home_team_id", "away_team_id", "kickoff_time", "state", "home_score", "away_score") VALUES ($1, $2, $3, $4, $5, $6, NOW(), $7, 2, 1);',
        ['fix_c1a_test_1001', fixCanId, 'comp_c1a_test_39', 'comp_c1a_test_39_2026', 'team_c1a_test_33', 'team_c1a_test_42', 'LIVE']
      );

      const res = await app.inject({
        method: 'GET',
        url: '/api/tray',
      });

      expect(res.statusCode).toBe(200);
      const envelope = JSON.parse(res.body);

      const match = envelope.data.matches.find((m: any) => m.id === 'fix_c1a_test_1001');
      expect(match).toBeDefined();
      expect(match.id).toBe('fix_c1a_test_1001');
      expect(match.competition.id).toBe('comp_c1a_test_39');
      expect(match.homeTeam.id).toBe('team_c1a_test_33');
      expect(match.awayTeam.id).toBe('team_c1a_test_42');

      const matchStr = JSON.stringify(match);
      expect(matchStr).not.toContain('canonical_id');
      expect(matchStr).not.toContain('canonicalId');
      expect(matchStr).not.toContain('ft_');
    });

    it('A.8: GET /api/match/:id resolves legacy fixture ID and never leaks canonical_id or ft_*', async () => {
      const compCanId = generateCanonicalId('comp');
      const homeCanId = generateCanonicalId('team');
      const awayCanId = generateCanonicalId('team');
      const fixCanId = generateCanonicalId('fix');

      await pool.query(
        'INSERT INTO "competitions" ("id", "canonical_id", "name", "code") VALUES ($1, $2, $3, $4);',
        ['comp_c1a_test_39', compCanId, 'Premier League', 'PL']
      );
      await pool.query(
        'INSERT INTO "competition_seasons" ("id", "competition_id", "year", "display_year", "start_date", "end_date", "is_current") VALUES ($1, $2, 2026, $3, NOW(), NOW(), true);',
        ['comp_c1a_test_39_2026', 'comp_c1a_test_39', '2026/27']
      );
      await pool.query(
        'INSERT INTO "teams" ("id", "canonical_id", "name", "short_name", "logo_url") VALUES ($1, $2, $3, $4, $5);',
        ['team_c1a_test_33', homeCanId, 'Manchester United', 'MUN', 'https://media.api-sports.io/football/teams/33.png']
      );
      await pool.query(
        'INSERT INTO "teams" ("id", "canonical_id", "name", "short_name", "logo_url") VALUES ($1, $2, $3, $4, $5);',
        ['team_c1a_test_42', awayCanId, 'Arsenal', 'ARS', 'https://media.api-sports.io/football/teams/42.png']
      );
      await pool.query(
        'INSERT INTO "fixtures" ("id", "canonical_id", "competition_id", "season_id", "home_team_id", "away_team_id", "kickoff_time", "state", "home_score", "away_score") VALUES ($1, $2, $3, $4, $5, $6, NOW(), $7, 2, 1);',
        ['fix_c1a_test_1001', fixCanId, 'comp_c1a_test_39', 'comp_c1a_test_39_2026', 'team_c1a_test_33', 'team_c1a_test_42', 'LIVE']
      );

      const res = await app.inject({
        method: 'GET',
        url: '/api/match/fix_c1a_test_1001',
      });

      expect(res.statusCode).toBe(200);
      const envelope = JSON.parse(res.body);

      expect(envelope.data.id).toBe('fix_c1a_test_1001');
      expect(envelope.data.competition.id).toBe('comp_c1a_test_39');
      expect(envelope.data.homeTeam.id).toBe('team_c1a_test_33');
      expect(envelope.data.awayTeam.id).toBe('team_c1a_test_42');

      const bodyStr = JSON.stringify(envelope);
      expect(bodyStr).not.toContain('canonical_id');
      expect(bodyStr).not.toContain('canonicalId');
      expect(bodyStr).not.toContain('ft_');
    });
  });

  // -------------------------------------------------------------------------
  // Part B: Canonical ID Generator Unit Tests
  // -------------------------------------------------------------------------
  describe('Part B: Canonical ID Generator Unit Tests', () => {
    it('B.1: Exact prefix + 26-character Crockford Base32 ULID payload', () => {
      const compId = generateCanonicalId('comp');
      const teamId = generateCanonicalId('team');
      const fixId = generateCanonicalId('fix');

      expect(compId).toMatch(/^ft_comp_[0123456789ABCDEFGHJKMNPQRSTVWXYZ]{26}$/);
      expect(teamId).toMatch(/^ft_team_[0123456789ABCDEFGHJKMNPQRSTVWXYZ]{26}$/);
      expect(fixId).toMatch(/^ft_fix_[0123456789ABCDEFGHJKMNPQRSTVWXYZ]{26}$/);

      expect(compId.length).toBe('ft_comp_'.length + CANONICAL_PAYLOAD_LENGTH);
      expect(teamId.length).toBe('ft_team_'.length + CANONICAL_PAYLOAD_LENGTH);
      expect(fixId.length).toBe('ft_fix_'.length + CANONICAL_PAYLOAD_LENGTH);
    });

    it('B.2: Payload strictly excludes ambiguous characters I, L, O, U and lowercase', () => {
      const forbiddenRegex = /[ILOUilou]/;
      for (let i = 0; i < 50; i++) {
        const id = generateCanonicalId('fix');
        const payload = id.slice('ft_fix_'.length);
        expect(forbiddenRegex.test(payload)).toBe(false);
      }
    });

    it('B.3: Two IDs generated at timestamps T and T+1 sort in timestamp order', () => {
      const fixedEntropy = Buffer.alloc(10, 0x55);
      const idAtT = generateCanonicalId('comp', { timestamp: 1774000000000, entropy: fixedEntropy });
      const idAtTPlus1 = generateCanonicalId('comp', { timestamp: 1774000000001, entropy: fixedEntropy });

      expect(idAtT < idAtTPlus1).toBe(true);

      const time1 = encodeTime(1000);
      const time2 = encodeTime(1001);
      expect(time1 < time2).toBe(true);
    });

    it('B.4: Deterministic test injection produces reproducible output', () => {
      const fixedTimestamp = 1774000000000;
      const fixedEntropy = Buffer.from([0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef, 0x10, 0x20]);

      const id1 = generateCanonicalId('team', { timestamp: fixedTimestamp, entropy: fixedEntropy });
      const id2 = generateCanonicalId('team', { timestamp: fixedTimestamp, entropy: fixedEntropy });

      expect(id1).toBe(id2);
      expect(id1).toMatch(/^ft_team_[0123456789ABCDEFGHJKMNPQRSTVWXYZ]{26}$/);
    });

    it('B.5: Production path uses cryptographically secure entropy with zero collisions', () => {
      const set = new Set<string>();
      const count = 100;
      for (let i = 0; i < count; i++) {
        set.add(generateCanonicalId('comp'));
      }
      expect(set.size).toBe(count);
    });

    it('B.6: ID recognizer identifies API-Football IDs and rejects nonconforming/mock IDs', () => {
      expect(isRecognizedApiFootballId('comp_39', 'comp')).toBe('39');
      expect(isRecognizedApiFootballId('team_529', 'team')).toBe('529');
      expect(isRecognizedApiFootballId('fix_10425', 'fix')).toBe('10425');

      expect(isRecognizedApiFootballId('mock_comp_1', 'comp')).toBeNull();
      expect(isRecognizedApiFootballId('comp_mock', 'comp')).toBeNull();
      expect(isRecognizedApiFootballId('mock_live_123', 'fix')).toBeNull();
      expect(isRecognizedApiFootballId('team_custom_barca', 'team')).toBeNull();
    });
  });

  // -------------------------------------------------------------------------
  // Part C: Isolated Backfill Behavior Suite (In-Memory FakeDatabase)
  // -------------------------------------------------------------------------
  describe('Part C: Isolated Backfill Behavior Suite (In-Memory FakeDatabase)', () => {
    it('C.1: Missing expectedDatabaseName throws before any SQL mutation', async () => {
      const fake = new FakeDatabase('footballtray_test');
      const client = fake.createClient();

      // @ts-expect-error - testing missing options
      await expect(backfillCanonical(client)).rejects.toThrow(
        /backfillCanonical failed closed: expectedDatabaseName option is required/
      );

      // @ts-expect-error - testing empty options
      await expect(backfillCanonical(client, {})).rejects.toThrow(
        /backfillCanonical failed closed: expectedDatabaseName option is required/
      );

      expect(fake.executedQueries).toHaveLength(0);
    });

    it('C.2: Wrong expectedDatabaseName checks current_database() and throws before BEGIN/mutation', async () => {
      const fake = new FakeDatabase('footballtray_test');
      const client = fake.createClient();

      fake.competitions.set('comp_39', { id: 'comp_39', canonical_id: null });

      await expect(
        backfillCanonical(client, { expectedDatabaseName: 'footballtray_prod' })
      ).rejects.toThrow(
        /target database guard violation: expected 'footballtray_prod', but connected to 'footballtray_test'/
      );

      expect(fake.executedQueries).not.toContain('BEGIN');
      expect(fake.executedQueries.some(q => q.startsWith('UPDATE') || q.startsWith('INSERT'))).toBe(false);
      expect(fake.competitions.get('comp_39')?.canonical_id).toBeNull();
    });

    it('C.3: Correct expected database proceeds with transaction and mutations', async () => {
      const fake = new FakeDatabase('footballtray_test');
      const client = fake.createClient();

      fake.competitions.set('comp_39', { id: 'comp_39', canonical_id: null });

      const report = await backfillCanonical(client, { expectedDatabaseName: 'footballtray_test' });
      expect(report.competitions.canonicalIdsAssigned).toBe(1);
      expect(report.competitions.aliasesCreated).toBe(1);

      expect(fake.executedQueries[0]).toBe('SELECT current_database()');
      expect(fake.executedQueries[1]).toBe('BEGIN');
      expect(fake.executedQueries[fake.executedQueries.length - 1]).toBe('COMMIT');
      expect(fake.competitions.get('comp_39')?.canonical_id).toMatch(/^ft_comp_[0123456789ABCDEFGHJKMNPQRSTVWXYZ]{26}$/);
    });

    it('C.4: Rows with canonical_id NULL receive ft_comp_/ft_team_/ft_fix_ IDs', async () => {
      const fake = new FakeDatabase('footballtray_test');
      const client = fake.createClient();

      fake.competitions.set('comp_39', { id: 'comp_39', canonical_id: null });
      fake.teams.set('team_33', { id: 'team_33', canonical_id: null });
      fake.fixtures.set('fix_1001', { id: 'fix_1001', canonical_id: null });

      const report = await backfillCanonical(client, { expectedDatabaseName: 'footballtray_test' });

      expect(report.competitions.canonicalIdsAssigned).toBe(1);
      expect(report.teams.canonicalIdsAssigned).toBe(1);
      expect(report.fixtures.canonicalIdsAssigned).toBe(1);

      expect(fake.competitions.get('comp_39')?.canonical_id).toMatch(/^ft_comp_[0123456789ABCDEFGHJKMNPQRSTVWXYZ]{26}$/);
      expect(fake.teams.get('team_33')?.canonical_id).toMatch(/^ft_team_[0123456789ABCDEFGHJKMNPQRSTVWXYZ]{26}$/);
      expect(fake.fixtures.get('fix_1001')?.canonical_id).toMatch(/^ft_fix_[0123456789ABCDEFGHJKMNPQRSTVWXYZ]{26}$/);
    });

    it('C.5: Rows with existing canonical_id are preserved and never replaced', async () => {
      const fake = new FakeDatabase('footballtray_test');
      const client = fake.createClient();

      const existingCanId = 'ft_comp_EXISTING1234567890ABCDEF';
      fake.competitions.set('comp_39', { id: 'comp_39', canonical_id: existingCanId });

      const report = await backfillCanonical(client, { expectedDatabaseName: 'footballtray_test' });
      expect(report.competitions.canonicalIdsAssigned).toBe(0);
      expect(fake.competitions.get('comp_39')?.canonical_id).toBe(existingCanId);
    });

    it('C.6: Recognized legacy IDs (comp_39, team_529, fix_1570390) receive api_football aliases', async () => {
      const fake = new FakeDatabase('footballtray_test');
      const client = fake.createClient();

      fake.competitions.set('comp_39', { id: 'comp_39', canonical_id: null });
      fake.teams.set('team_529', { id: 'team_529', canonical_id: null });
      fake.fixtures.set('fix_1570390', { id: 'fix_1570390', canonical_id: null });

      const report = await backfillCanonical(client, { expectedDatabaseName: 'footballtray_test' });
      expect(report.competitions.aliasesCreated).toBe(1);
      expect(report.teams.aliasesCreated).toBe(1);
      expect(report.fixtures.aliasesCreated).toBe(1);

      expect(fake.competitionAliases.get('api_football:39')?.provider_id).toBe('39');
      expect(fake.teamAliases.get('api_football:529')?.provider_id).toBe('529');
      expect(fake.fixtureAliases.get('api_football:1570390')?.provider_id).toBe('1570390');
    });

    it('C.7: Nonconforming mock IDs do NOT receive api_football aliases', async () => {
      const fake = new FakeDatabase('footballtray_test');
      const client = fake.createClient();

      fake.competitions.set('mock_comp_dev', { id: 'mock_comp_dev', canonical_id: null });
      fake.teams.set('team_custom_barca', { id: 'team_custom_barca', canonical_id: null });
      fake.fixtures.set('fix_mock_live_001', { id: 'fix_mock_live_001', canonical_id: null });

      const report = await backfillCanonical(client, { expectedDatabaseName: 'footballtray_test' });

      expect(report.competitions.canonicalIdsAssigned).toBe(1);
      expect(report.teams.canonicalIdsAssigned).toBe(1);
      expect(report.fixtures.canonicalIdsAssigned).toBe(1);

      expect(report.competitions.aliasesCreated).toBe(0);
      expect(report.teams.aliasesCreated).toBe(0);
      expect(report.fixtures.aliasesCreated).toBe(0);

      expect(fake.competitionAliases.size).toBe(0);
      expect(fake.teamAliases.size).toBe(0);
      expect(fake.fixtureAliases.size).toBe(0);
    });

    it('C.8: Rerun on same state is idempotent and creates zero duplicate aliases or IDs', async () => {
      const fake = new FakeDatabase('footballtray_test');
      const client = fake.createClient();

      fake.competitions.set('comp_39', { id: 'comp_39', canonical_id: null });
      fake.teams.set('team_33', { id: 'team_33', canonical_id: null });
      fake.fixtures.set('fix_1001', { id: 'fix_1001', canonical_id: null });

      const pass1 = await backfillCanonical(client, { expectedDatabaseName: 'footballtray_test' });
      expect(pass1.competitions.canonicalIdsAssigned).toBe(1);
      expect(pass1.competitions.aliasesCreated).toBe(1);

      const compCan1 = fake.competitions.get('comp_39')?.canonical_id;

      const pass2 = await backfillCanonical(client, { expectedDatabaseName: 'footballtray_test' });
      expect(pass2.competitions.canonicalIdsAssigned).toBe(0);
      expect(pass2.competitions.aliasesCreated).toBe(0);
      expect(pass2.teams.canonicalIdsAssigned).toBe(0);
      expect(pass2.teams.aliasesCreated).toBe(0);
      expect(pass2.fixtures.canonicalIdsAssigned).toBe(0);
      expect(pass2.fixtures.aliasesCreated).toBe(0);

      expect(fake.competitions.get('comp_39')?.canonical_id).toBe(compCan1);
    });

    it('C.9: Existing alias pointing to SAME canonical ID succeeds as idempotent no-op', async () => {
      const fake = new FakeDatabase('footballtray_test');
      const client = fake.createClient();

      const existingCanId = 'ft_comp_MATCHING1234567890ABCDEF';
      fake.competitions.set('comp_39', { id: 'comp_39', canonical_id: existingCanId });
      fake.competitionAliases.set('api_football:39', {
        canonical_id: existingCanId,
        provider: 'api_football',
        provider_id: '39',
      });

      const report = await backfillCanonical(client, { expectedDatabaseName: 'footballtray_test' });
      expect(report.competitions.canonicalIdsAssigned).toBe(0);
      expect(report.competitions.aliasesCreated).toBe(0);
      expect(fake.competitionAliases.get('api_football:39')?.canonical_id).toBe(existingCanId);
    });

    it('C.10: Existing alias pointing to DIFFERENT canonical ID throws for comp, team, fixture', async () => {
      // 1. Competition conflict
      {
        const fake = new FakeDatabase('footballtray_test');
        const client = fake.createClient();

        fake.competitions.set('comp_39', { id: 'comp_39', canonical_id: 'ft_comp_NEW111111111111111111111' });
        fake.competitionAliases.set('api_football:39', {
          canonical_id: 'ft_comp_OTHER22222222222222222222',
          provider: 'api_football',
          provider_id: '39',
        });

        await expect(
          backfillCanonical(client, { expectedDatabaseName: 'footballtray_test' })
        ).rejects.toThrow(/Conflicting provider alias detected in "competition_provider_aliases"/);
      }

      // 2. Team conflict
      {
        const fake = new FakeDatabase('footballtray_test');
        const client = fake.createClient();

        fake.teams.set('team_33', { id: 'team_33', canonical_id: 'ft_team_NEW111111111111111111111' });
        fake.teamAliases.set('api_football:33', {
          canonical_id: 'ft_team_OTHER22222222222222222222',
          provider: 'api_football',
          provider_id: '33',
        });

        await expect(
          backfillCanonical(client, { expectedDatabaseName: 'footballtray_test' })
        ).rejects.toThrow(/Conflicting provider alias detected in "team_provider_aliases"/);
      }

      // 3. Fixture conflict
      {
        const fake = new FakeDatabase('footballtray_test');
        const client = fake.createClient();

        fake.fixtures.set('fix_1001', { id: 'fix_1001', canonical_id: 'ft_fix_NEW111111111111111111111' });
        fake.fixtureAliases.set('api_football:1001', {
          canonical_id: 'ft_fix_OTHER22222222222222222222',
          provider: 'api_football',
          provider_id: '1001',
        });

        await expect(
          backfillCanonical(client, { expectedDatabaseName: 'footballtray_test' })
        ).rejects.toThrow(/Conflicting provider alias detected in "fixture_provider_aliases"/);
      }
    });

    it('C.11: Conflict after earlier assignments/inserts triggers ROLLBACK with zero committed partial state', async () => {
      const fake = new FakeDatabase('footballtray_test');
      const client = fake.createClient();

      // Entity A: competition without canonical ID
      fake.competitions.set('comp_39', { id: 'comp_39', canonical_id: null });

      // Entity B: team with pre-existing conflicting alias
      fake.teams.set('team_33', { id: 'team_33', canonical_id: null });
      fake.teamAliases.set('api_football:33', {
        canonical_id: 'ft_team_PREEXISTING_CONFLICT_ID',
        provider: 'api_football',
        provider_id: '33',
      });

      // Execute: comp_39 is processed first, but team_33 conflict causes throw and ROLLBACK
      await expect(
        backfillCanonical(client, { expectedDatabaseName: 'footballtray_test' })
      ).rejects.toThrow(/Conflicting provider alias detected in "team_provider_aliases"/);

      // Verify ROLLBACK was executed
      expect(fake.executedQueries).toContain('ROLLBACK');

      // Verify Entity A canonical_id was restored to null via snapshot rollback
      expect(fake.competitions.get('comp_39')?.canonical_id).toBeNull();
      expect(fake.competitionAliases.has('api_football:39')).toBe(false);
    });
  });
});
