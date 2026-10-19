import Database, { type Database as SQLiteDatabase } from 'better-sqlite3';
import * as dotenv from 'dotenv';
import path from 'node:path';
import fs from 'node:fs';

dotenv.config();

export const EXPECTED_TABLES = [
  'acquisition_jobs',
  'api_request_ledger',
  'competition_provider_aliases',
  'competition_seasons',
  'competitions',
  'events',
  'fixture_provider_aliases',
  'fixture_statistics',
  'fixtures',
  'installation_settings',
  'installations',
  'lineup_players',
  'lineups',
  'provider_coverage',
  'provider_daily_budget',
  'scheduler_cycles',
  'standings',
  'team_provider_aliases',
  'teams',
] as const;

function resolveDatabasePath(): string {
  let rawUrl: string | undefined = process.env.DATABASE_URL || './local.db';
  let dbPath = rawUrl.trim();
  if (dbPath.startsWith('file:')) {
    dbPath = dbPath.slice(5);
  }
  if (dbPath.startsWith('sqlite://')) {
    dbPath = dbPath.slice(9);
  } else if (dbPath.startsWith('sqlite:')) {
    dbPath = dbPath.slice(7);
  }

  const resolved = path.isAbsolute(dbPath) ? dbPath : path.resolve(process.cwd(), dbPath);
  const dir = path.dirname(resolved);
  if (!fs.existsSync(dir)) {
    fs.mkdirSync(dir, { recursive: true });
  }
  return resolved;
}

const TABLE_SCHEMAS: string[] = [
  `CREATE TABLE "competitions" (
    "id" text PRIMARY KEY NOT NULL,
    "canonical_id" text NOT NULL,
    "name" text NOT NULL,
    "code" text,
    "type" text DEFAULT 'league',
    "country" text,
    "logo_url" text,
    "created_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL,
    "updated_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL
  );`,

  `CREATE TABLE "competition_seasons" (
    "id" text PRIMARY KEY NOT NULL,
    "competition_id" text NOT NULL,
    "year" integer NOT NULL,
    "display_year" text NOT NULL,
    "start_date" integer NOT NULL,
    "end_date" integer NOT NULL,
    "is_current" integer DEFAULT true NOT NULL,
    "created_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL,
    FOREIGN KEY ("competition_id") REFERENCES "competitions"("id") ON UPDATE no action ON DELETE cascade
  );`,

  `CREATE TABLE "teams" (
    "id" text PRIMARY KEY NOT NULL,
    "canonical_id" text NOT NULL,
    "name" text NOT NULL,
    "short_name" text NOT NULL,
    "code" text,
    "country" text,
    "logo_url" text,
    "founded" integer,
    "venue_name" text,
    "created_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL,
    "updated_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL
  );`,

  `CREATE TABLE "fixtures" (
    "id" text PRIMARY KEY NOT NULL,
    "canonical_id" text NOT NULL,
    "competition_id" text NOT NULL,
    "season_id" text NOT NULL,
    "home_team_id" text NOT NULL,
    "away_team_id" text NOT NULL,
    "round" text,
    "kickoff_time" integer NOT NULL,
    "state" text DEFAULT 'PREMATCH' NOT NULL,
    "elapsed_minute" integer DEFAULT 0 NOT NULL,
    "extra_minute" integer DEFAULT 0 NOT NULL,
    "home_score" integer DEFAULT 0 NOT NULL,
    "away_score" integer DEFAULT 0 NOT NULL,
    "home_score_ht" integer,
    "away_score_ht" integer,
    "home_score_et" integer,
    "away_score_et" integer,
    "home_score_pen" integer,
    "away_score_pen" integer,
    "venue" text,
    "referee" text,
    "last_polled_at" integer,
    "finalized_at" integer,
    "live_source_provider" text,
    "live_observed_at" integer,
    "schedule_source_provider" text,
    "schedule_observed_at" integer,
    "created_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL,
    "updated_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL,
    FOREIGN KEY ("competition_id") REFERENCES "competitions"("id") ON UPDATE no action ON DELETE no action,
    FOREIGN KEY ("season_id") REFERENCES "competition_seasons"("id") ON UPDATE no action ON DELETE no action,
    FOREIGN KEY ("home_team_id") REFERENCES "teams"("id") ON UPDATE no action ON DELETE no action,
    FOREIGN KEY ("away_team_id") REFERENCES "teams"("id") ON UPDATE no action ON DELETE no action
  );`,

  `CREATE TABLE "events" (
    "id" text PRIMARY KEY NOT NULL,
    "event_uid" text NOT NULL,
    "fixture_id" text NOT NULL,
    "team_id" text NOT NULL,
    "type" text NOT NULL,
    "minute" integer NOT NULL,
    "extra_minute" integer DEFAULT 0 NOT NULL,
    "player_name" text,
    "player_id" text,
    "assist_player_name" text,
    "assist_player_id" text,
    "description" text,
    "timestamp" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL,
    FOREIGN KEY ("fixture_id") REFERENCES "fixtures"("id") ON UPDATE no action ON DELETE cascade,
    FOREIGN KEY ("team_id") REFERENCES "teams"("id") ON UPDATE no action ON DELETE no action
  );`,

  `CREATE TABLE "lineups" (
    "id" text PRIMARY KEY NOT NULL,
    "fixture_id" text NOT NULL,
    "team_id" text NOT NULL,
    "formation" text,
    "coach_name" text,
    "coach_id" text,
    "created_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL,
    FOREIGN KEY ("fixture_id") REFERENCES "fixtures"("id") ON UPDATE no action ON DELETE cascade,
    FOREIGN KEY ("team_id") REFERENCES "teams"("id") ON UPDATE no action ON DELETE no action
  );`,

  `CREATE TABLE "lineup_players" (
    "id" text PRIMARY KEY NOT NULL,
    "lineup_id" text NOT NULL,
    "player_id" text NOT NULL,
    "name" text NOT NULL,
    "number" integer,
    "position" text,
    "grid" text,
    "is_starter" integer DEFAULT true NOT NULL,
    FOREIGN KEY ("lineup_id") REFERENCES "lineups"("id") ON UPDATE no action ON DELETE cascade
  );`,

  `CREATE TABLE "fixture_statistics" (
    "id" text PRIMARY KEY NOT NULL,
    "fixture_id" text NOT NULL,
    "team_id" text NOT NULL,
    "shots_on_goal" integer,
    "shots_off_goal" integer,
    "total_shots" integer,
    "possession_percent" integer,
    "fouls" integer,
    "corner_kicks" integer,
    "offsides" integer,
    "yellow_cards" integer,
    "red_cards" integer,
    "goalkeeper_saves" integer,
    "total_passes" integer,
    "passes_accurate" integer,
    "expected_goals" real,
    "updated_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL,
    FOREIGN KEY ("fixture_id") REFERENCES "fixtures"("id") ON UPDATE no action ON DELETE cascade,
    FOREIGN KEY ("team_id") REFERENCES "teams"("id") ON UPDATE no action ON DELETE no action
  );`,

  `CREATE TABLE "standings" (
    "id" text PRIMARY KEY NOT NULL,
    "competition_id" text NOT NULL,
    "season_id" text NOT NULL,
    "team_id" text NOT NULL,
    "position" integer NOT NULL,
    "played" integer DEFAULT 0 NOT NULL,
    "won" integer DEFAULT 0 NOT NULL,
    "drawn" integer DEFAULT 0 NOT NULL,
    "lost" integer DEFAULT 0 NOT NULL,
    "goals_for" integer DEFAULT 0 NOT NULL,
    "goals_against" integer DEFAULT 0 NOT NULL,
    "goal_difference" integer DEFAULT 0 NOT NULL,
    "points" integer DEFAULT 0 NOT NULL,
    "form" text,
    "description" text,
    "updated_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL,
    FOREIGN KEY ("competition_id") REFERENCES "competitions"("id") ON UPDATE no action ON DELETE no action,
    FOREIGN KEY ("season_id") REFERENCES "competition_seasons"("id") ON UPDATE no action ON DELETE no action,
    FOREIGN KEY ("team_id") REFERENCES "teams"("id") ON UPDATE no action ON DELETE no action
  );`,

  `CREATE TABLE "provider_coverage" (
    "id" text PRIMARY KEY NOT NULL,
    "provider" text DEFAULT 'api_football' NOT NULL,
    "competition_id" text NOT NULL,
    "season_year" integer NOT NULL,
    "has_fixtures" integer DEFAULT true NOT NULL,
    "has_live_odds" integer DEFAULT false NOT NULL,
    "has_lineups" integer DEFAULT true NOT NULL,
    "has_events" integer DEFAULT true NOT NULL,
    "has_statistics" integer DEFAULT true NOT NULL,
    "has_standings" integer DEFAULT true NOT NULL,
    "has_players" integer DEFAULT true NOT NULL,
    "has_commentary" integer DEFAULT false NOT NULL,
    "has_ratings" integer DEFAULT false NOT NULL,
    "has_xg" integer DEFAULT false NOT NULL,
    "last_audited_at" integer,
    FOREIGN KEY ("competition_id") REFERENCES "competitions"("id") ON UPDATE no action ON DELETE no action
  );`,

  `CREATE TABLE "provider_daily_budget" (
    "id" text PRIMARY KEY NOT NULL,
    "provider" text DEFAULT 'api_football' NOT NULL,
    "budget_date" text NOT NULL,
    "allocated_calls" integer DEFAULT 100 NOT NULL,
    "consumed_calls" integer DEFAULT 0 NOT NULL,
    "remaining_calls" integer DEFAULT 100 NOT NULL,
    "is_exhausted" integer DEFAULT false NOT NULL,
    "updated_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL
  );`,

  `CREATE TABLE "api_request_ledger" (
    "id" text PRIMARY KEY NOT NULL,
    "cycle_id" text,
    "competition_id" text,
    "provider" text DEFAULT 'api_football' NOT NULL,
    "endpoint" text NOT NULL,
    "operation" text DEFAULT 'unknown' NOT NULL,
    "priority" text DEFAULT 'NORMAL' NOT NULL,
    "success" integer DEFAULT true NOT NULL,
    "http_status" integer NOT NULL,
    "response_time_ms" integer NOT NULL,
    "cost_units" integer DEFAULT 1 NOT NULL,
    "provider_daily_remaining" integer,
    "provider_minute_remaining" integer,
    "requested_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL,
    "error_message" text
  );`,

  `CREATE TABLE "acquisition_jobs" (
    "id" text PRIMARY KEY NOT NULL,
    "job_type" text NOT NULL,
    "status" text DEFAULT 'pending' NOT NULL,
    "target_id" text,
    "priority" integer DEFAULT 0 NOT NULL,
    "attempts" integer DEFAULT 0 NOT NULL,
    "last_error" text,
    "scheduled_at" integer NOT NULL,
    "executed_at" integer,
    "created_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL
  );`,

  `CREATE TABLE "installations" (
    "id" text PRIMARY KEY NOT NULL,
    "installation_id" text NOT NULL,
    "client_version" text,
    "platform" text DEFAULT 'Windows 11' NOT NULL,
    "last_active_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL,
    "created_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL
  );`,

  `CREATE TABLE "installation_settings" (
    "id" text PRIMARY KEY NOT NULL,
    "installation_id" text NOT NULL,
    "favorite_team_id" text,
    "favorite_competition_id" text,
    "enable_spoilers" integer DEFAULT true NOT NULL,
    "polling_interval_seconds" integer DEFAULT 60 NOT NULL,
    "updated_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL,
    FOREIGN KEY ("installation_id") REFERENCES "installations"("installation_id") ON UPDATE no action ON DELETE cascade,
    FOREIGN KEY ("favorite_team_id") REFERENCES "teams"("id") ON UPDATE no action ON DELETE no action,
    FOREIGN KEY ("favorite_competition_id") REFERENCES "competitions"("id") ON UPDATE no action ON DELETE no action
  );`,

  `CREATE TABLE "scheduler_cycles" (
    "id" text PRIMARY KEY NOT NULL,
    "action" text NOT NULL,
    "priority" text DEFAULT 'NORMAL' NOT NULL,
    "reason" text NOT NULL,
    "target_competition_ids" text,
    "started_at" integer NOT NULL,
    "completed_at" integer,
    "duration_ms" integer,
    "outcome" text,
    "provider_requests_count" integer DEFAULT 0 NOT NULL,
    "provider_requests_cost" integer DEFAULT 0 NOT NULL,
    "error_type" text,
    "error_message" text,
    "fixtures_discovered" integer DEFAULT 0 NOT NULL,
    "fixtures_updated" integer DEFAULT 0 NOT NULL,
    "created_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL
  );`,

  `CREATE TABLE "competition_provider_aliases" (
    "canonical_competition_id" text NOT NULL,
    "provider" text NOT NULL,
    "provider_id" text NOT NULL,
    "first_seen_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL,
    "last_seen_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL,
    PRIMARY KEY("provider", "provider_id"),
    FOREIGN KEY ("canonical_competition_id") REFERENCES "competitions"("canonical_id") ON UPDATE no action ON DELETE cascade
  );`,

  `CREATE TABLE "team_provider_aliases" (
    "canonical_team_id" text NOT NULL,
    "provider" text NOT NULL,
    "provider_id" text NOT NULL,
    "first_seen_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL,
    "last_seen_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL,
    PRIMARY KEY("provider", "provider_id"),
    FOREIGN KEY ("canonical_team_id") REFERENCES "teams"("canonical_id") ON UPDATE no action ON DELETE cascade
  );`,

  `CREATE TABLE "fixture_provider_aliases" (
    "canonical_fixture_id" text NOT NULL,
    "provider" text NOT NULL,
    "provider_id" text NOT NULL,
    "first_seen_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL,
    "last_seen_at" integer DEFAULT (cast((julianday('now') - 2440587.5)*86400000 as integer)) NOT NULL,
    PRIMARY KEY("provider", "provider_id"),
    FOREIGN KEY ("canonical_fixture_id") REFERENCES "fixtures"("canonical_id") ON UPDATE no action ON DELETE cascade
  );`,
];

const INDEX_SCHEMAS: string[] = [
  'CREATE UNIQUE INDEX IF NOT EXISTS "budget_provider_date_idx" ON "provider_daily_budget" ("provider","budget_date");',
  'CREATE INDEX IF NOT EXISTS "comp_provider_aliases_canonical_idx" ON "competition_provider_aliases" ("canonical_competition_id");',
  'CREATE UNIQUE INDEX IF NOT EXISTS "comp_season_unique_idx" ON "competition_seasons" ("competition_id","year");',
  'CREATE UNIQUE INDEX IF NOT EXISTS "competitions_canonical_id_unique" ON "competitions" ("canonical_id");',
  'CREATE UNIQUE INDEX IF NOT EXISTS "events_event_uid_unique" ON "events" ("event_uid");',
  'CREATE INDEX IF NOT EXISTS "events_fixture_idx" ON "events" ("fixture_id");',
  'CREATE UNIQUE INDEX IF NOT EXISTS "events_uid_idx" ON "events" ("event_uid");',
  'CREATE INDEX IF NOT EXISTS "fix_provider_aliases_canonical_idx" ON "fixture_provider_aliases" ("canonical_fixture_id");',
  'CREATE INDEX IF NOT EXISTS "fixtures_away_team_idx" ON "fixtures" ("away_team_id");',
  'CREATE UNIQUE INDEX IF NOT EXISTS "fixtures_canonical_id_unique" ON "fixtures" ("canonical_id");',
  'CREATE INDEX IF NOT EXISTS "fixtures_home_team_idx" ON "fixtures" ("home_team_id");',
  'CREATE INDEX IF NOT EXISTS "fixtures_kickoff_idx" ON "fixtures" ("kickoff_time");',
  'CREATE INDEX IF NOT EXISTS "fixtures_state_idx" ON "fixtures" ("state");',
  'CREATE UNIQUE INDEX IF NOT EXISTS "installations_installation_id_unique" ON "installations" ("installation_id");',
  'CREATE INDEX IF NOT EXISTS "jobs_status_scheduled_idx" ON "acquisition_jobs" ("status","scheduled_at");',
  'CREATE INDEX IF NOT EXISTS "ledger_competition_id_idx" ON "api_request_ledger" ("competition_id");',
  'CREATE INDEX IF NOT EXISTS "ledger_cycle_id_idx" ON "api_request_ledger" ("cycle_id");',
  'CREATE INDEX IF NOT EXISTS "ledger_provider_requested_idx" ON "api_request_ledger" ("provider","requested_at");',
  'CREATE INDEX IF NOT EXISTS "ledger_requested_at_idx" ON "api_request_ledger" ("requested_at");',
  'CREATE UNIQUE INDEX IF NOT EXISTS "lineup_fixture_team_idx" ON "lineups" ("fixture_id","team_id");',
  'CREATE INDEX IF NOT EXISTS "lineup_players_lineup_idx" ON "lineup_players" ("lineup_id");',
  'CREATE UNIQUE INDEX IF NOT EXISTS "provider_comp_season_idx" ON "provider_coverage" ("provider","competition_id","season_year");',
  'CREATE INDEX IF NOT EXISTS "scheduler_cycles_action_idx" ON "scheduler_cycles" ("action","started_at");',
  'CREATE INDEX IF NOT EXISTS "scheduler_cycles_pruning_idx" ON "scheduler_cycles" ("outcome","started_at");',
  'CREATE INDEX IF NOT EXISTS "scheduler_cycles_started_idx" ON "scheduler_cycles" ("started_at");',
  'CREATE INDEX IF NOT EXISTS "standings_comp_season_pos_idx" ON "standings" ("competition_id","season_id","position");',
  'CREATE UNIQUE INDEX IF NOT EXISTS "standings_comp_season_team_idx" ON "standings" ("competition_id","season_id","team_id");',
  'CREATE UNIQUE INDEX IF NOT EXISTS "stats_fixture_team_idx" ON "fixture_statistics" ("fixture_id","team_id");',
  'CREATE INDEX IF NOT EXISTS "team_provider_aliases_canonical_idx" ON "team_provider_aliases" ("canonical_team_id");',
  'CREATE UNIQUE INDEX IF NOT EXISTS "teams_canonical_id_unique" ON "teams" ("canonical_id");',
];

export function forceResetDatabase(targetDbPath?: string): void {
  const dbPath = targetDbPath || resolveDatabasePath();
  console.log('================================================================');
  console.log(`[FORCE_RESET_DB] INITIATING AGGRESSIVE DATABASE PURGE & REBUILD`);
  console.log(`[FORCE_RESET_DB] Target Database: ${dbPath}`);
  console.log('================================================================');

  const sqlite: SQLiteDatabase = new Database(dbPath);

  try {
    // 1. Temporarily disable foreign keys to allow unconditional DROP of all tables
    sqlite.pragma('foreign_keys = OFF');
    sqlite.pragma('journal_mode = WAL');

    // 2. Discover and drop all existing tables and views
    const existingObjects = sqlite
      .prepare(`SELECT type, name FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%'`)
      .all() as Array<{ type: string; name: string }>;

    console.log(`[FORCE_RESET_DB] Found ${existingObjects.length} existing database objects to drop.`);
    for (const obj of existingObjects) {
      if (obj.type === 'view') {
        sqlite.exec(`DROP VIEW IF EXISTS "${obj.name}";`);
      } else {
        sqlite.exec(`DROP TABLE IF EXISTS "${obj.name}";`);
      }
      console.log(`[FORCE_RESET_DB] Dropped ${obj.type} "${obj.name}".`);
    }

    // 3. Recreate all pristine table schemas
    console.log(`[FORCE_RESET_DB] Recreating ${TABLE_SCHEMAS.length} pristine database tables...`);
    for (const ddl of TABLE_SCHEMAS) {
      sqlite.exec(ddl);
    }

    // 4. Recreate all performance and uniqueness indexes
    console.log(`[FORCE_RESET_DB] Creating ${INDEX_SCHEMAS.length} indexes...`);
    for (const idxDdl of INDEX_SCHEMAS) {
      sqlite.exec(idxDdl);
    }

    // 5. Clean defragmentation & restore strict foreign keys
    sqlite.exec('VACUUM;');
    sqlite.pragma('foreign_keys = ON');

    // 6. Verification: inspect every table and assert 0 rows
    console.log('----------------------------------------------------------------');
    console.log('[FORCE_RESET_DB] VERIFICATION AUDIT (Row counts after reset):');
    let totalRowsAcrossAllTables = 0;
    for (const tableName of EXPECTED_TABLES) {
      const result = sqlite.prepare(`SELECT count(*) as count FROM "${tableName}"`).get() as { count: number };
      const count = result?.count ?? 0;
      totalRowsAcrossAllTables += count;
      console.log(`  - Table "${tableName}": ${count} rows`);
      if (count !== 0) {
        throw new Error(`Integrity violation: table "${tableName}" is not empty (${count} rows found).`);
      }
    }

    console.log('----------------------------------------------------------------');
    console.log(`[FORCE_RESET_DB] SUCCESS: Database completely wiped and rebuilt pristine.`);
    console.log(`[FORCE_RESET_DB] Total rows across all ${EXPECTED_TABLES.length} tables: ${totalRowsAcrossAllTables}`);
    console.log('================================================================');
  } finally {
    sqlite.close();
  }
}

// Direct CLI invocation
if (import.meta.url === `file://${process.argv[1]?.replace(/\\/g, '/')}` || process.argv[1]?.endsWith('force-reset-db.ts')) {
  try {
    forceResetDatabase();
  } catch (err: any) {
    console.error('[FORCE_RESET_DB] FATAL ERROR during reset:', err?.message || err);
    process.exit(1);
  }
}
