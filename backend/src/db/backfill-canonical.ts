import pg from 'pg';
import dotenv from 'dotenv';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import process from 'node:process';
import { generateCanonicalId } from './canonical-id.js';

dotenv.config();

const { Pool } = pg;

export interface EntityBackfillResult {
  totalRows: number;
  canonicalIdsAssigned: number;
  aliasesCreated: number;
}

export interface BackfillReport {
  competitions: EntityBackfillResult;
  teams: EntityBackfillResult;
  fixtures: EntityBackfillResult;
}

export interface BackfillCanonicalOptions {
  expectedDatabaseName: string;
}

export const API_FOOTBALL_COMPETITION_REGEX = /^comp_(\d+)$/;
export const API_FOOTBALL_TEAM_REGEX = /^team_(\d+)$/;
export const API_FOOTBALL_FIXTURE_REGEX = /^fix_(\d+)$/;

export function isRecognizedApiFootballId(id: string, type: 'comp' | 'team' | 'fix'): string | null {
  let match: RegExpMatchArray | null = null;
  if (type === 'comp') {
    match = id.match(API_FOOTBALL_COMPETITION_REGEX);
  } else if (type === 'team') {
    match = id.match(API_FOOTBALL_TEAM_REGEX);
  } else if (type === 'fix') {
    match = id.match(API_FOOTBALL_FIXTURE_REGEX);
  }
  return match ? match[1] : null;
}

/**
 * Ensures that a provider alias is mapped to the canonical entity.
 *
 * Invariant behavior:
 * - Case 1: Alias does not exist -> creates it (returns true).
 * - Case 2: Alias exists and points to SAME canonical entity -> idempotent no-op (returns false).
 * - Case 3: Alias exists and points to DIFFERENT canonical entity -> throws Error (triggers transaction rollback).
 */
async function ensureProviderAlias(
  client: pg.PoolClient,
  tableName: 'competition_provider_aliases' | 'team_provider_aliases' | 'fixture_provider_aliases',
  canonicalCol: 'canonical_competition_id' | 'canonical_team_id' | 'canonical_fixture_id',
  canonicalId: string,
  provider: string,
  providerId: string
): Promise<boolean> {
  const insertRes = await client.query(
    `INSERT INTO "${tableName}"
     ("${canonicalCol}", "provider", "provider_id", "first_seen_at", "last_seen_at")
     VALUES ($1, $2, $3, NOW(), NOW())
     ON CONFLICT ("provider", "provider_id") DO NOTHING;`,
    [canonicalId, provider, providerId]
  );

  if (insertRes.rowCount && insertRes.rowCount > 0) {
    return true; // Case 1: fresh alias created
  }

  // Alias already existed. Verify target canonical identity.
  const existingRes = await client.query<{ [key: string]: string }>(
    `SELECT "${canonicalCol}" FROM "${tableName}" WHERE "provider" = $1 AND "provider_id" = $2;`,
    [provider, providerId]
  );

  if (existingRes.rows.length === 0) {
    throw new Error(`Failed to resolve existing alias in "${tableName}" for ${provider}:${providerId}`);
  }

  const existingCanonicalId = existingRes.rows[0][canonicalCol];
  if (existingCanonicalId !== canonicalId) {
    // Case 3: Conflicting alias mapping -> FAIL CLOSED
    throw new Error(
      `Conflicting provider alias detected in "${tableName}" for ${provider}:${providerId}: existing alias points to "${existingCanonicalId}", but current backfill attempted mapping to "${canonicalId}".`
    );
  }

  // Case 2: Idempotent no-op
  return false;
}

/**
 * Safely backfills existing competitions, teams, and fixtures with canonical IDs
 * and creates provider aliases for legacy API-Football formatted IDs.
 *
 * Invariants:
 * - Fail-closed target-database guard: requires explicit options.expectedDatabaseName matching current_database().
 * - Zero external provider calls.
 * - Idempotent and restart-safe: does not overwrite existing canonical IDs.
 * - Alias conflict fail-closed: throws and rolls back if an alias points to a different canonical entity.
 * - Leaves existing physical primary keys and foreign keys unchanged.
 * - Only associates api_football aliases for IDs conforming to comp_<numeric>, team_<numeric>, fix_<numeric>.
 * - Mock/test/custom rows receive canonical IDs but NEVER receive api_football aliases.
 */
export async function backfillCanonical(
  clientOrPool?: pg.PoolClient | pg.Pool,
  options?: BackfillCanonicalOptions
): Promise<BackfillReport> {
  // Target-database guard: explicit expectedDatabaseName is required
  if (!options || typeof options.expectedDatabaseName !== 'string' || options.expectedDatabaseName.trim() === '') {
    throw new Error(
      'backfillCanonical failed closed: expectedDatabaseName option is required and must be non-empty.'
    );
  }

  const needsClose = !clientOrPool;
  const pool = clientOrPool || new Pool({
    connectionString: process.env.DATABASE_URL,
    max: 2,
  });

  const client = 'connect' in pool && typeof (pool as pg.Pool).connect === 'function'
    ? await (pool as pg.Pool).connect()
    : (pool as pg.PoolClient);

  try {
    // Verify target database identity BEFORE beginning transaction or mutating any data
    const dbCheck = await client.query<{ db_name: string }>('SELECT current_database() AS db_name;');
    const currentDb = dbCheck.rows[0]?.db_name;
    if (!currentDb) {
      throw new Error('backfillCanonical failed closed: unable to determine current_database().');
    }
    if (currentDb !== options.expectedDatabaseName) {
      throw new Error(
        `backfillCanonical target database guard violation: expected '${options.expectedDatabaseName}', but connected to '${currentDb}'.`
      );
    }

    await client.query('BEGIN');

    // 1. Backfill Competitions
    const compRows = await client.query<{ id: string; canonical_id: string | null }>(
      'SELECT id, canonical_id FROM "competitions" ORDER BY id ASC;'
    );
    let compCanonicalAssigned = 0;
    let compAliasesCreated = 0;

    for (const comp of compRows.rows) {
      let canonicalId = comp.canonical_id;
      if (!canonicalId) {
        canonicalId = generateCanonicalId('comp');
        await client.query(
          'UPDATE "competitions" SET canonical_id = $1 WHERE id = $2 AND canonical_id IS NULL;',
          [canonicalId, comp.id]
        );
        compCanonicalAssigned++;
      }

      const providerId = isRecognizedApiFootballId(comp.id, 'comp');
      if (providerId) {
        const created = await ensureProviderAlias(
          client,
          'competition_provider_aliases',
          'canonical_competition_id',
          canonicalId,
          'api_football',
          providerId
        );
        if (created) {
          compAliasesCreated++;
        }
      }
    }

    // 2. Backfill Teams
    const teamRows = await client.query<{ id: string; canonical_id: string | null }>(
      'SELECT id, canonical_id FROM "teams" ORDER BY id ASC;'
    );
    let teamCanonicalAssigned = 0;
    let teamAliasesCreated = 0;

    for (const team of teamRows.rows) {
      let canonicalId = team.canonical_id;
      if (!canonicalId) {
        canonicalId = generateCanonicalId('team');
        await client.query(
          'UPDATE "teams" SET canonical_id = $1 WHERE id = $2 AND canonical_id IS NULL;',
          [canonicalId, team.id]
        );
        teamCanonicalAssigned++;
      }

      const providerId = isRecognizedApiFootballId(team.id, 'team');
      if (providerId) {
        const created = await ensureProviderAlias(
          client,
          'team_provider_aliases',
          'canonical_team_id',
          canonicalId,
          'api_football',
          providerId
        );
        if (created) {
          teamAliasesCreated++;
        }
      }
    }

    // 3. Backfill Fixtures
    const fixRows = await client.query<{ id: string; canonical_id: string | null }>(
      'SELECT id, canonical_id FROM "fixtures" ORDER BY id ASC;'
    );
    let fixCanonicalAssigned = 0;
    let fixAliasesCreated = 0;

    for (const fix of fixRows.rows) {
      let canonicalId = fix.canonical_id;
      if (!canonicalId) {
        canonicalId = generateCanonicalId('fix');
        await client.query(
          'UPDATE "fixtures" SET canonical_id = $1 WHERE id = $2 AND canonical_id IS NULL;',
          [canonicalId, fix.id]
        );
        fixCanonicalAssigned++;
      }

      const providerId = isRecognizedApiFootballId(fix.id, 'fix');
      if (providerId) {
        const created = await ensureProviderAlias(
          client,
          'fixture_provider_aliases',
          'canonical_fixture_id',
          canonicalId,
          'api_football',
          providerId
        );
        if (created) {
          fixAliasesCreated++;
        }
      }
    }

    await client.query('COMMIT');

    return {
      competitions: {
        totalRows: compRows.rows.length,
        canonicalIdsAssigned: compCanonicalAssigned,
        aliasesCreated: compAliasesCreated,
      },
      teams: {
        totalRows: teamRows.rows.length,
        canonicalIdsAssigned: teamCanonicalAssigned,
        aliasesCreated: teamAliasesCreated,
      },
      fixtures: {
        totalRows: fixRows.rows.length,
        canonicalIdsAssigned: fixCanonicalAssigned,
        aliasesCreated: fixAliasesCreated,
      },
    };
  } catch (err) {
    try {
      await client.query('ROLLBACK');
    } catch {
      // Ignore rollback errors if transaction was not started
    }
    throw err;
  } finally {
    if ('connect' in pool && typeof (pool as pg.Pool).connect === 'function') {
      (client as pg.PoolClient).release();
    }
    if (needsClose) {
      await (pool as pg.Pool).end();
    }
  }
}

export function parseCliArgs(argv: string[]): { expectedDatabase?: string } {
  const args: { expectedDatabase?: string } = {};
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === '--expected-database' && i + 1 < argv.length) {
      args.expectedDatabase = argv[i + 1];
      i++;
    } else if (arg.startsWith('--expected-database=')) {
      args.expectedDatabase = arg.slice('--expected-database='.length);
    }
  }
  return args;
}

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
  const cliArgs = parseCliArgs(process.argv.slice(2));
  if (!cliArgs.expectedDatabase) {
    console.error('[BACKFILL] Fatal error: --expected-database <dbName> argument is required.');
    process.exit(1);
  }

  backfillCanonical(undefined, { expectedDatabaseName: cliArgs.expectedDatabase })
    .then((report) => {
      console.log('[BACKFILL] Canonical identity backfill completed successfully:');
      console.log(JSON.stringify(report, null, 2));
      process.exit(0);
    })
    .catch((err) => {
      console.error('[BACKFILL] Fatal error during canonical identity backfill:', err.message);
      process.exit(1);
    });
}
