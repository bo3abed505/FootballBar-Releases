import pg from 'pg';
import path from 'node:path';
import fs from 'node:fs';
import { fileURLToPath } from 'node:url';
import process from 'node:process';
import { drizzle } from 'drizzle-orm/node-postgres';
import { migrate } from 'drizzle-orm/node-postgres/migrator';
import { readMigrationFiles } from 'drizzle-orm/migrator';

const { Pool } = pg;

export type MigrationState = 'NO_DB_CHANGE' | 'BASELINE_COMMITTED' | 'MIGRATION_COMMITTED';

export class MigrationExecutionError extends Error {
  constructor(
    message: string,
    public readonly cause?: unknown,
    public readonly migrationState: MigrationState = 'NO_DB_CHANGE'
  ) {
    super(message);
    this.name = 'MigrationExecutionError';
  }
}

export class PostMigrationVerificationError extends Error {
  constructor(
    message: string,
    public readonly cause?: unknown,
    public readonly migrationState: MigrationState = 'MIGRATION_COMMITTED'
  ) {
    super(message);
    this.name = 'PostMigrationVerificationError';
  }
}

export interface MigrationRunnerOptions {
  connectionString?: string;
  expectedDatabaseName: string;
  baselineLegacySchema?: boolean;
  migrationsFolder?: string;
}

export interface MigrationRunnerResult {
  appliedCount: number;
  latestMigrationTimestamp: string;
  latestMigrationHash: string;
  verified: boolean;
  database: string;
  state: MigrationState;
}

export const EXPECTED_LEGACY_TABLES = [
  'acquisition_jobs',
  'api_request_ledger',
  'competition_seasons',
  'competitions',
  'events',
  'fixture_statistics',
  'fixtures',
  'installation_settings',
  'installations',
  'lineup_players',
  'lineups',
  'provider_coverage',
  'provider_daily_budget',
  'standings',
  'teams',
] as const;

export type LegacyTable = (typeof EXPECTED_LEGACY_TABLES)[number];

export interface ColumnInvariant {
  type: string;
  nullable: boolean;
}

export const EXPECTED_LEGACY_COLUMNS: Record<LegacyTable, Record<string, ColumnInvariant>> = {
  acquisition_jobs: {
    id: { type: 'text', nullable: false },
    job_type: { type: 'text', nullable: false },
    status: { type: 'text', nullable: false },
    target_id: { type: 'text', nullable: true },
    priority: { type: 'integer', nullable: false },
    attempts: { type: 'integer', nullable: false },
    last_error: { type: 'text', nullable: true },
    scheduled_at: { type: 'timestamp with time zone', nullable: false },
    executed_at: { type: 'timestamp with time zone', nullable: true },
    created_at: { type: 'timestamp with time zone', nullable: false },
  },
  api_request_ledger: {
    id: { type: 'text', nullable: false },
    provider: { type: 'text', nullable: false },
    endpoint: { type: 'text', nullable: false },
    http_status: { type: 'integer', nullable: false },
    response_time_ms: { type: 'integer', nullable: false },
    cost_units: { type: 'integer', nullable: false },
    requested_at: { type: 'timestamp with time zone', nullable: false },
    error_message: { type: 'text', nullable: true },
    // 0001 columns
    operation: { type: 'text', nullable: false },
    priority: { type: 'text', nullable: false },
    success: { type: 'boolean', nullable: false },
    provider_daily_remaining: { type: 'integer', nullable: true },
    provider_minute_remaining: { type: 'integer', nullable: true },
  },
  competition_seasons: {
    id: { type: 'text', nullable: false },
    competition_id: { type: 'text', nullable: false },
    year: { type: 'integer', nullable: false },
    display_year: { type: 'text', nullable: false },
    start_date: { type: 'timestamp with time zone', nullable: false },
    end_date: { type: 'timestamp with time zone', nullable: false },
    is_current: { type: 'boolean', nullable: false },
    created_at: { type: 'timestamp with time zone', nullable: false },
  },
  competitions: {
    id: { type: 'text', nullable: false },
    name: { type: 'text', nullable: false },
    code: { type: 'text', nullable: true },
    type: { type: 'text', nullable: true },
    country: { type: 'text', nullable: true },
    logo_url: { type: 'text', nullable: true },
    created_at: { type: 'timestamp with time zone', nullable: false },
    updated_at: { type: 'timestamp with time zone', nullable: false },
  },
  events: {
    id: { type: 'text', nullable: false },
    event_uid: { type: 'text', nullable: false },
    fixture_id: { type: 'text', nullable: false },
    team_id: { type: 'text', nullable: false },
    type: { type: 'text', nullable: false },
    minute: { type: 'integer', nullable: false },
    extra_minute: { type: 'integer', nullable: false },
    player_name: { type: 'text', nullable: true },
    player_id: { type: 'text', nullable: true },
    assist_player_name: { type: 'text', nullable: true },
    assist_player_id: { type: 'text', nullable: true },
    description: { type: 'text', nullable: true },
    timestamp: { type: 'timestamp with time zone', nullable: false },
  },
  fixture_statistics: {
    id: { type: 'text', nullable: false },
    fixture_id: { type: 'text', nullable: false },
    team_id: { type: 'text', nullable: false },
    shots_on_goal: { type: 'integer', nullable: true },
    shots_off_goal: { type: 'integer', nullable: true },
    total_shots: { type: 'integer', nullable: true },
    possession_percent: { type: 'integer', nullable: true },
    fouls: { type: 'integer', nullable: true },
    corner_kicks: { type: 'integer', nullable: true },
    offsides: { type: 'integer', nullable: true },
    yellow_cards: { type: 'integer', nullable: true },
    red_cards: { type: 'integer', nullable: true },
    goalkeeper_saves: { type: 'integer', nullable: true },
    total_passes: { type: 'integer', nullable: true },
    passes_accurate: { type: 'integer', nullable: true },
    updated_at: { type: 'timestamp with time zone', nullable: false },
  },
  fixtures: {
    id: { type: 'text', nullable: false },
    competition_id: { type: 'text', nullable: false },
    season_id: { type: 'text', nullable: false },
    home_team_id: { type: 'text', nullable: false },
    away_team_id: { type: 'text', nullable: false },
    round: { type: 'text', nullable: true },
    kickoff_time: { type: 'timestamp with time zone', nullable: false },
    state: { type: 'text', nullable: false },
    elapsed_minute: { type: 'integer', nullable: false },
    extra_minute: { type: 'integer', nullable: false },
    home_score: { type: 'integer', nullable: false },
    away_score: { type: 'integer', nullable: false },
    home_score_ht: { type: 'integer', nullable: true },
    away_score_ht: { type: 'integer', nullable: true },
    home_score_et: { type: 'integer', nullable: true },
    away_score_et: { type: 'integer', nullable: true },
    home_score_pen: { type: 'integer', nullable: true },
    away_score_pen: { type: 'integer', nullable: true },
    venue: { type: 'text', nullable: true },
    referee: { type: 'text', nullable: true },
    last_polled_at: { type: 'timestamp with time zone', nullable: true },
    created_at: { type: 'timestamp with time zone', nullable: false },
    updated_at: { type: 'timestamp with time zone', nullable: false },
  },
  installation_settings: {
    id: { type: 'text', nullable: false },
    installation_id: { type: 'text', nullable: false },
    favorite_team_id: { type: 'text', nullable: true },
    favorite_competition_id: { type: 'text', nullable: true },
    enable_spoilers: { type: 'boolean', nullable: false },
    polling_interval_seconds: { type: 'integer', nullable: false },
    updated_at: { type: 'timestamp with time zone', nullable: false },
  },
  installations: {
    id: { type: 'text', nullable: false },
    installation_id: { type: 'text', nullable: false },
    client_version: { type: 'text', nullable: true },
    platform: { type: 'text', nullable: false },
    last_active_at: { type: 'timestamp with time zone', nullable: false },
    created_at: { type: 'timestamp with time zone', nullable: false },
  },
  lineup_players: {
    id: { type: 'text', nullable: false },
    lineup_id: { type: 'text', nullable: false },
    player_id: { type: 'text', nullable: false },
    name: { type: 'text', nullable: false },
    number: { type: 'integer', nullable: true },
    position: { type: 'text', nullable: true },
    grid: { type: 'text', nullable: true },
    is_starter: { type: 'boolean', nullable: false },
  },
  lineups: {
    id: { type: 'text', nullable: false },
    fixture_id: { type: 'text', nullable: false },
    team_id: { type: 'text', nullable: false },
    formation: { type: 'text', nullable: true },
    coach_name: { type: 'text', nullable: true },
    coach_id: { type: 'text', nullable: true },
    created_at: { type: 'timestamp with time zone', nullable: false },
  },
  provider_coverage: {
    id: { type: 'text', nullable: false },
    provider: { type: 'text', nullable: false },
    competition_id: { type: 'text', nullable: false },
    season_year: { type: 'integer', nullable: false },
    has_live_odds: { type: 'boolean', nullable: false },
    has_lineups: { type: 'boolean', nullable: false },
    has_events: { type: 'boolean', nullable: false },
    has_statistics: { type: 'boolean', nullable: false },
    has_standings: { type: 'boolean', nullable: false },
    has_players: { type: 'boolean', nullable: false },
    last_audited_at: { type: 'timestamp with time zone', nullable: true },
    // 0001 columns
    has_fixtures: { type: 'boolean', nullable: false },
    has_commentary: { type: 'boolean', nullable: false },
    has_ratings: { type: 'boolean', nullable: false },
    has_xg: { type: 'boolean', nullable: false },
  },
  provider_daily_budget: {
    id: { type: 'text', nullable: false },
    provider: { type: 'text', nullable: false },
    budget_date: { type: 'date', nullable: false },
    allocated_calls: { type: 'integer', nullable: false },
    consumed_calls: { type: 'integer', nullable: false },
    remaining_calls: { type: 'integer', nullable: false },
    is_exhausted: { type: 'boolean', nullable: false },
    updated_at: { type: 'timestamp with time zone', nullable: false },
  },
  standings: {
    id: { type: 'text', nullable: false },
    competition_id: { type: 'text', nullable: false },
    season_id: { type: 'text', nullable: false },
    team_id: { type: 'text', nullable: false },
    position: { type: 'integer', nullable: false },
    played: { type: 'integer', nullable: false },
    won: { type: 'integer', nullable: false },
    drawn: { type: 'integer', nullable: false },
    lost: { type: 'integer', nullable: false },
    goals_for: { type: 'integer', nullable: false },
    goals_against: { type: 'integer', nullable: false },
    goal_difference: { type: 'integer', nullable: false },
    points: { type: 'integer', nullable: false },
    form: { type: 'text', nullable: true },
    description: { type: 'text', nullable: true },
    updated_at: { type: 'timestamp with time zone', nullable: false },
  },
  teams: {
    id: { type: 'text', nullable: false },
    name: { type: 'text', nullable: false },
    short_name: { type: 'text', nullable: false },
    code: { type: 'text', nullable: true },
    country: { type: 'text', nullable: true },
    logo_url: { type: 'text', nullable: true },
    founded: { type: 'integer', nullable: true },
    venue_name: { type: 'text', nullable: true },
    created_at: { type: 'timestamp with time zone', nullable: false },
    updated_at: { type: 'timestamp with time zone', nullable: false },
  },
};

export const EXPECTED_LEGACY_INDEXES = [
  'jobs_status_scheduled_idx',
  'ledger_requested_at_idx',
  'comp_season_unique_idx',
  'events_fixture_idx',
  'events_uid_idx',
  'stats_fixture_team_idx',
  'fixtures_state_idx',
  'fixtures_kickoff_idx',
  'fixtures_home_team_idx',
  'fixtures_away_team_idx',
  'lineup_players_lineup_idx',
  'lineup_fixture_team_idx',
  'provider_comp_season_idx',
  'budget_provider_date_idx',
  'standings_comp_season_team_idx',
  'standings_comp_season_pos_idx',
  'ledger_provider_requested_idx', // 0001 index
];

export const EXPECTED_PHASE2A_INDEXES = [
  'scheduler_cycles_started_idx',
  'scheduler_cycles_action_idx',
  'scheduler_cycles_pruning_idx',
  'ledger_cycle_id_idx',
  'ledger_competition_id_idx',
];

/**
 * Deterministically verifies that the existing database schema materially represents
 * migrations 0000 + 0001 and that Phase 2A (0002) objects remain strictly absent.
 *
 * Checks:
 * - All 15 expected 0000 tables exist
 * - All expected columns for all 15 tables exist with exact type and nullability
 * - Primary key constraints on 'id' exist for all 15 tables
 * - All 17 expected legacy indexes (0000 + 0001) exist
 * - Table 'scheduler_cycles' is absent
 * - Columns 'cycle_id' and 'competition_id' on 'api_request_ledger' are absent
 * - All Phase 2A indexes are absent
 *
 * Must fail closed and throw BEFORE any baseline metadata transaction begins.
 */
export async function verifyLegacySchema(client: pg.PoolClient): Promise<void> {
  const colRes = await client.query<{
    table_name: string;
    column_name: string;
    data_type: string;
    is_nullable: string;
  }>(`
    SELECT table_name, column_name, data_type, is_nullable
    FROM information_schema.columns
    WHERE table_schema = 'public';
  `);

  const tablesMap = new Map<string, Map<string, { type: string; nullable: string }>>();
  for (const row of colRes.rows) {
    if (!tablesMap.has(row.table_name)) {
      tablesMap.set(row.table_name, new Map());
    }
    tablesMap.get(row.table_name)!.set(row.column_name, {
      type: row.data_type,
      nullable: row.is_nullable,
    });
  }

  // 1. Verify all 15 expected legacy tables exist
  for (const table of EXPECTED_LEGACY_TABLES) {
    if (!tablesMap.has(table)) {
      throw new Error(`[MIGRATE_PROD] Legacy schema verification failed: Missing required table '${table}'.`);
    }
  }

  // 2. Verify all columns, data types, and nullability
  for (const [table, cols] of Object.entries(EXPECTED_LEGACY_COLUMNS)) {
    const tableCols = tablesMap.get(table)!;
    for (const [colName, expected] of Object.entries(cols)) {
      const actual = tableCols.get(colName);
      if (!actual) {
        throw new Error(
          `[MIGRATE_PROD] Legacy schema verification failed: Table '${table}' missing required column '${colName}'.`
        );
      }
      if (actual.type !== expected.type) {
        throw new Error(
          `[MIGRATE_PROD] Legacy schema verification failed: Table '${table}' column '${colName}' type mismatch. Expected '${expected.type}', found '${actual.type}'.`
        );
      }
      const expectedNullable = expected.nullable ? 'YES' : 'NO';
      if (actual.nullable !== expectedNullable) {
        throw new Error(
          `[MIGRATE_PROD] Legacy schema verification failed: Table '${table}' column '${colName}' nullability mismatch. Expected '${expectedNullable}', found '${actual.nullable}'.`
        );
      }
    }
  }

  // 3. Verify primary key constraints on 'id'
  const pkRes = await client.query<{ table_name: string; column_name: string }>(`
    SELECT kcu.table_name, kcu.column_name
    FROM information_schema.table_constraints tc
    JOIN information_schema.key_column_usage kcu
      ON tc.constraint_name = kcu.constraint_name
      AND tc.table_schema = kcu.table_schema
    WHERE tc.constraint_type = 'PRIMARY KEY'
      AND tc.table_schema = 'public';
  `);

  const pksByTable = new Map<string, string[]>();
  for (const row of pkRes.rows) {
    if (!pksByTable.has(row.table_name)) {
      pksByTable.set(row.table_name, []);
    }
    pksByTable.get(row.table_name)!.push(row.column_name);
  }

  for (const table of EXPECTED_LEGACY_TABLES) {
    const pks = pksByTable.get(table) || [];
    if (!pks.includes('id')) {
      throw new Error(
        `[MIGRATE_PROD] Legacy schema verification failed: Table '${table}' missing primary key constraint on 'id'.`
      );
    }
  }

  // 4. Verify all 17 expected legacy indexes
  const idxRes = await client.query<{ indexname: string }>(`
    SELECT indexname FROM pg_indexes WHERE schemaname = 'public';
  `);
  const foundIdxSet = new Set(idxRes.rows.map((r) => r.indexname));

  for (const expectedIdx of EXPECTED_LEGACY_INDEXES) {
    if (!foundIdxSet.has(expectedIdx)) {
      throw new Error(
        `[MIGRATE_PROD] Legacy schema verification failed: Missing required index '${expectedIdx}'.`
      );
    }
  }

  // 5. Require Phase 2A objects to be ABSENT before baseline
  if (tablesMap.has('scheduler_cycles')) {
    throw new Error(
      `[MIGRATE_PROD] Inconsistent schema: 'scheduler_cycles' already exists before Phase 2A migration. Aborting baseline.`
    );
  }

  const ledgerCols = tablesMap.get('api_request_ledger')!;
  if (ledgerCols.has('cycle_id') || ledgerCols.has('competition_id')) {
    throw new Error(
      `[MIGRATE_PROD] Inconsistent schema: 'cycle_id' or 'competition_id' already exists on api_request_ledger before Phase 2A migration. Aborting baseline.`
    );
  }

  for (const p2aIdx of EXPECTED_PHASE2A_INDEXES) {
    if (foundIdxSet.has(p2aIdx)) {
      throw new Error(
        `[MIGRATE_PROD] Inconsistent schema: Phase 2A index '${p2aIdx}' already exists before migration. Aborting baseline.`
      );
    }
  }
}

/**
 * Executes production migrations safely using drizzle-orm/node-postgres/migrator.
 * Includes deterministic legacy baselining, fail-closed safety assertions, dynamic
 * latest migration verification, and transition state tracking.
 */
export async function runProductionMigration(
  options: MigrationRunnerOptions
): Promise<MigrationRunnerResult> {
  let currentState: MigrationState = 'NO_DB_CHANGE';

  const dbUrl = options.connectionString || process.env.DATABASE_URL;
  if (!dbUrl || dbUrl.trim().length === 0) {
    throw new Error('[MIGRATE_PROD] CRITICAL: DATABASE_URL is not defined in environment or options.');
  }

  // 1. Resolve and validate migrations folder & authentic metadata before DB interaction
  const currentDir = path.dirname(fileURLToPath(import.meta.url));
  const defaultMigrationsFolder = path.resolve(currentDir, '../../drizzle');
  const migrationsFolder = options.migrationsFolder || defaultMigrationsFolder;

  if (!fs.existsSync(migrationsFolder)) {
    throw new Error(`[MIGRATE_PROD] CRITICAL: Migrations folder not found at '${migrationsFolder}'`);
  }

  const journalFile = path.join(migrationsFolder, 'meta', '_journal.json');
  if (!fs.existsSync(journalFile)) {
    throw new Error(`[MIGRATE_PROD] CRITICAL: Journal manifest not found at '${journalFile}'`);
  }

  const packagedMigrations = readMigrationFiles({ migrationsFolder });
  if (packagedMigrations.length === 0) {
    throw new Error('[MIGRATE_PROD] CRITICAL: No migration files discovered in migrations folder.');
  }

  // Derive latest packaged migration dynamically from readMigrationFiles()
  const latestPackagedMigration = packagedMigrations[packagedMigrations.length - 1];

  const pool = new Pool({
    connectionString: dbUrl.trim(),
    max: 5,
    connectionTimeoutMillis: 10000,
  });

  let currentDb: string = '';

  try {
    const client = await pool.connect();
    let currentUser: string;

    try {
      // 2. Positive database identity verification
      const dbIdRes = await client.query('SELECT current_database() AS db_name, current_user AS db_user;');
      currentDb = dbIdRes.rows[0]?.db_name;
      currentUser = dbIdRes.rows[0]?.db_user;

      if (!currentDb || currentDb !== options.expectedDatabaseName) {
        throw new Error(
          `[MIGRATE_PROD] CRITICAL SAFETY ABORT: Connected to database '${currentDb}', but expected '${options.expectedDatabaseName}'. ` +
          `Positive database identity verification failed.`
        );
      }

      if (options.expectedDatabaseName === 'footballtray_prod' && currentDb === 'footballtray') {
        throw new Error(
          '[MIGRATE_PROD] CRITICAL SAFETY ABORT: Connected database is the development database (footballtray).'
        );
      }

      console.log(`[MIGRATE_PROD] Verified target database: '${currentDb}' (user: '${currentUser}')`);

      // 3. Inspect current database schema and migration history
      const tableCheckRes = await client.query(
        `SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE';`
      );
      const publicTables: string[] = tableCheckRes.rows.map((r) => r.table_name);

      const drizzleTableCheck = await client.query(
        `SELECT EXISTS (
           SELECT 1 FROM information_schema.tables 
           WHERE table_schema = 'drizzle' AND table_name = '__drizzle_migrations'
         ) AS has_table;`
      );
      const hasDrizzleTable = Boolean(drizzleTableCheck.rows[0]?.has_table);

      let dbHistory: { id: number; hash: string; created_at: string }[] = [];
      if (hasDrizzleTable) {
        const histRes = await client.query(
          `SELECT id, hash, created_at FROM "drizzle"."__drizzle_migrations" ORDER BY created_at ASC;`
        );
        dbHistory = histRes.rows;
      }

      console.log(
        `[MIGRATE_PROD] Schema state: ${publicTables.length} public tables, ${dbHistory.length} recorded Drizzle migrations.`
      );

      // 4. Evaluate migration history states:
      // Case A: Fresh/empty public database
      if (publicTables.length === 0 && dbHistory.length === 0) {
        console.log('[MIGRATE_PROD] Empty database detected. Standard migration from baseline 0000 will be executed.');
      }
      // Case B & C: Populated database with missing or empty Drizzle history
      else if (publicTables.length > 0 && dbHistory.length === 0) {
        if (!options.baselineLegacySchema) {
          throw new Error(
            `[MIGRATE_PROD] CRITICAL: Database '${currentDb}' contains ${publicTables.length} tables but has no Drizzle history in 'drizzle.__drizzle_migrations'. ` +
            `Automatic baselining is disallowed in standard mode to prevent schema corruption. ` +
            `If performing the one-time legacy baseline transition, supply --baseline-legacy-schema.`
          );
        }

        console.log('[MIGRATE_PROD] Legacy baseline mode requested. Performing deterministic legacy schema verification (0000 + 0001)...');

        // Deterministic verification of 0000 + 0001 schema invariants before any writes
        await verifyLegacySchema(client);

        console.log('[MIGRATE_PROD] Legacy schema verified (0000 + 0001 complete, 0002 absent). Seeding authentic baseline metadata in transaction...');

        // Transactional insertion of authentic 0000 and 0001 migration records
        await client.query('BEGIN');
        try {
          await client.query(`CREATE SCHEMA IF NOT EXISTS "drizzle";`);
          await client.query(`
            CREATE TABLE IF NOT EXISTS "drizzle"."__drizzle_migrations" (
              id SERIAL PRIMARY KEY,
              hash text NOT NULL,
              created_at bigint
            );
          `);

          // Insert migration 0000
          await client.query(
            `INSERT INTO "drizzle"."__drizzle_migrations" ("hash", "created_at") VALUES ($1, $2);`,
            [packagedMigrations[0].hash, packagedMigrations[0].folderMillis]
          );

          // Insert migration 0001
          await client.query(
            `INSERT INTO "drizzle"."__drizzle_migrations" ("hash", "created_at") VALUES ($1, $2);`,
            [packagedMigrations[1].hash, packagedMigrations[1].folderMillis]
          );

          await client.query('COMMIT');
          currentState = 'BASELINE_COMMITTED';
          console.log('[MIGRATE_PROD] Transactional baseline metadata committed (0000 and 0001 recorded).');
        } catch (baselineErr) {
          await client.query('ROLLBACK');
          throw new Error(`[MIGRATE_PROD] Failed to seed baseline migration metadata: ${baselineErr}`);
        }
      }
      // Case D: Existing Drizzle migration history
      else if (dbHistory.length > 0) {
        if (options.baselineLegacySchema) {
          throw new Error(
            `[MIGRATE_PROD] CRITICAL: --baseline-legacy-schema was supplied, but drizzle.__drizzle_migrations already contains ${dbHistory.length} migration records. Baseline flag is disallowed when migration history already exists.`
          );
        }

        const latestDbMillis = Number(dbHistory[dbHistory.length - 1].created_at);
        const newestPackagedMillis = Number(latestPackagedMigration.folderMillis);

        if (latestDbMillis > newestPackagedMillis) {
          throw new Error(
            `[MIGRATE_PROD] CRITICAL: Database migration history (${latestDbMillis}) is newer than newest packaged migration (${newestPackagedMillis}). Refusing to run out-of-date release package against newer database.`
          );
        }

        // Validate consistency of existing history records against packaged migrations
        for (const dbRec of dbHistory) {
          const matchingMigration = packagedMigrations.find(
            (m) => m.folderMillis.toString() === dbRec.created_at.toString()
          );

          if (!matchingMigration) {
            throw new Error(
              `[MIGRATE_PROD] CRITICAL: Recorded migration created_at '${dbRec.created_at}' is not recognized in packaged migrations.`
            );
          }

          if (matchingMigration.hash !== dbRec.hash) {
            throw new Error(
              `[MIGRATE_PROD] CRITICAL: Migration hash mismatch for timestamp ${dbRec.created_at}. Database has '${dbRec.hash}', packaged migration has '${matchingMigration.hash}'.`
            );
          }
        }

        if (latestDbMillis === newestPackagedMillis) {
          console.log('[MIGRATE_PROD] Database is already at newest migration timestamp. Running idempotent verification...');
        }
      }
    } finally {
      client.release();
    }

    // 5. Execute Drizzle migrate()
    console.log('[MIGRATE_PROD] Applying pending migrations with Drizzle engine...');
    const db = drizzle(pool);
    try {
      await migrate(db, { migrationsFolder });
      currentState = 'MIGRATION_COMMITTED';
    } catch (migErr) {
      throw new MigrationExecutionError(
        `[MIGRATE_PROD] Drizzle migrate() execution failed. The pending migration transaction was rolled back by PostgreSQL.`,
        migErr,
        currentState
      );
    }

    console.log('[MIGRATE_PROD] Drizzle migrate() completed. Executing post-migration schema verification...');

    // 6. Post-migration verification checks
    const verifyClient = await pool.connect();
    try {
      // 6a. Verify latest recorded migration timestamp and hash dynamically against latestPackagedMigration
      const latestHistRes = await verifyClient.query(
        `SELECT hash, created_at FROM "drizzle"."__drizzle_migrations" ORDER BY created_at DESC LIMIT 1;`
      );
      const latestRec = latestHistRes.rows[0];

      if (!latestRec) {
        throw new PostMigrationVerificationError(
          `[MIGRATE_PROD] Post-migration verification failed: 'drizzle.__drizzle_migrations' is empty after migration.`,
          undefined,
          currentState
        );
      }

      if (latestRec.created_at.toString() !== latestPackagedMigration.folderMillis.toString()) {
        throw new PostMigrationVerificationError(
          `[MIGRATE_PROD] Post-migration verification failed: Expected latest migration timestamp '${latestPackagedMigration.folderMillis}', found '${latestRec.created_at}'.`,
          undefined,
          currentState
        );
      }

      if (latestRec.hash !== latestPackagedMigration.hash) {
        throw new PostMigrationVerificationError(
          `[MIGRATE_PROD] Post-migration verification failed: Expected migration hash '${latestPackagedMigration.hash}', found '${latestRec.hash}'.`,
          undefined,
          currentState
        );
      }

      // 6b. Verify scheduler_cycles table exists (Phase 2A requirement)
      const cyclesTableRes = await verifyClient.query(
        `SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'scheduler_cycles';`
      );
      if (cyclesTableRes.rows.length === 0) {
        throw new PostMigrationVerificationError(
          `[MIGRATE_PROD] Post-migration verification failed: Table 'scheduler_cycles' does not exist in public schema.`,
          undefined,
          currentState
        );
      }

      // 6c. Verify api_request_ledger columns cycle_id and competition_id (and nullable)
      const colsRes = await verifyClient.query(
        `SELECT column_name, is_nullable 
         FROM information_schema.columns 
         WHERE table_name = 'api_request_ledger' AND column_name IN ('cycle_id', 'competition_id');`
      );
      if (colsRes.rows.length !== 2) {
        throw new PostMigrationVerificationError(
          `[MIGRATE_PROD] Post-migration verification failed: Expected 2 columns (cycle_id, competition_id) on api_request_ledger, found ${colsRes.rows.length}.`,
          undefined,
          currentState
        );
      }

      for (const col of colsRes.rows) {
        if (col.is_nullable !== 'YES') {
          throw new PostMigrationVerificationError(
            `[MIGRATE_PROD] Post-migration verification failed: Column '${col.column_name}' on api_request_ledger must be nullable, found '${col.is_nullable}'.`,
            undefined,
            currentState
          );
        }
      }

      // 6d. Verify all 5 Phase 2A indexes exist
      const idxRes = await verifyClient.query(
        `SELECT indexname FROM pg_indexes 
         WHERE tablename IN ('scheduler_cycles', 'api_request_ledger')
           AND indexname = ANY($1::text[]);`,
        [EXPECTED_PHASE2A_INDEXES]
      );
      const foundIdx: string[] = idxRes.rows.map((r) => r.indexname);
      const missingIdx = EXPECTED_PHASE2A_INDEXES.filter((i) => !foundIdx.includes(i));
      if (missingIdx.length > 0) {
        throw new PostMigrationVerificationError(
          `[MIGRATE_PROD] Post-migration verification failed: Missing required Phase 2A indexes: ${missingIdx.join(', ')}`,
          undefined,
          currentState
        );
      }

      console.log('[MIGRATE_PROD] Post-migration verification PASSED: Migration history, tables, columns, and indexes verified.');

      return {
        appliedCount: packagedMigrations.length,
        latestMigrationTimestamp: latestRec.created_at.toString(),
        latestMigrationHash: latestRec.hash,
        verified: true,
        database: currentDb,
        state: currentState,
      };
    } finally {
      verifyClient.release();
    }
  } finally {
    await pool.end();
  }
}

// ---------------------------------------------------------------------------
// CLI Entrypoint
// Production executable entrypoint hardcodes expectedDatabaseName: 'footballtray_prod'.
// Emits machine-readable MIGRATION_STATE=<STATE> protocol marker to stdout.
// ---------------------------------------------------------------------------
function isMainModule(): boolean {
  if (!process.argv[1]) return false;
  try {
    const currentFile = fileURLToPath(import.meta.url);
    const invokedFile = path.resolve(process.argv[1]);
    return currentFile.toLowerCase() === invokedFile.toLowerCase();
  } catch {
    return false;
  }
}

if (isMainModule()) {
  const baselineLegacySchema = process.argv.includes('--baseline-legacy-schema');

  runProductionMigration({
    expectedDatabaseName: 'footballtray_prod',
    baselineLegacySchema,
  })
    .then((result) => {
      console.log('MIGRATION_STATE=MIGRATION_COMMITTED');
      console.log(`[MIGRATE_PROD] Production migration completed successfully against '${result.database}'.`);
      process.exit(0);
    })
    .catch((err) => {
      const state: MigrationState = (err as any)?.migrationState || 'NO_DB_CHANGE';
      console.log(`MIGRATION_STATE=${state}`);
      if (err instanceof PostMigrationVerificationError) {
        console.error('[MIGRATE_PROD] FATAL: Post-migration verification failed after migration transaction committed:', err);
        process.exit(2);
      } else if (err instanceof MigrationExecutionError) {
        console.error(`[MIGRATE_PROD] FATAL: Migration execution failed (state=${state}):`, err);
        process.exit(1);
      } else {
        console.error(`[MIGRATE_PROD] Fatal migration failure before migration was applied (state=${state}):`, err);
        process.exit(1);
      }
    });
}

