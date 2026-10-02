import Database, { type Database as SQLiteDatabase } from 'better-sqlite3';
import { drizzle } from 'drizzle-orm/better-sqlite3';
import * as schema from './schema.js';
import * as dotenv from 'dotenv';
import path from 'node:path';
import fs from 'node:fs';

dotenv.config();

export const isTestEnvironment = process.env.NODE_ENV === 'test' || Boolean(process.env.VITEST);

function resolveDatabasePath(): string {
  let rawUrl: string | undefined;
  if (isTestEnvironment) {
    rawUrl = process.env.DATABASE_URL_TEST || process.env.DATABASE_URL || './local_test.db';
  } else {
    rawUrl = process.env.DATABASE_URL || './local.db';
  }

  let dbPath = rawUrl.trim();
  if (dbPath.startsWith('file:')) {
    dbPath = dbPath.slice(5);
  }
  if (dbPath.startsWith('sqlite://')) {
    dbPath = dbPath.slice(9);
  } else if (dbPath.startsWith('sqlite:')) {
    dbPath = dbPath.slice(7);
  }

  if (dbPath === ':memory:') {
    return ':memory:';
  }

  const resolved = path.isAbsolute(dbPath) ? dbPath : path.resolve(process.cwd(), dbPath);
  const dir = path.dirname(resolved);
  if (!fs.existsSync(dir)) {
    fs.mkdirSync(dir, { recursive: true });
  }
  return resolved;
}

export const databasePath = resolveDatabasePath();
export const databaseUrl = `file:${databasePath}`;

export const sqlite: SQLiteDatabase = new Database(databasePath);
sqlite.pragma('journal_mode = WAL');
sqlite.pragma('foreign_keys = ON');
sqlite.pragma('busy_timeout = 5000');

export const db = drizzle(sqlite, { schema });

/**
 * Checks if the configured SQLite database is reachable and operational.
 * Returns true if connected, false otherwise without throwing.
 */
export async function checkDatabaseConnection(): Promise<{ connected: boolean; latencyMs?: number; error?: string }> {
  const start = Date.now();
  try {
    sqlite.prepare('SELECT 1').get();
    return { connected: true, latencyMs: Date.now() - start };
  } catch (err: any) {
    return {
      connected: false,
      error: err?.message ?? 'SQLite database connection failed',
      latencyMs: Date.now() - start,
    };
  }
}

/**
 * Safe adapter for any legacy scripts/services calling pool lifecycle or queries.
 * Rips out pg/pg-pool completely while ensuring graceful SQLite query execution.
 */
function executeSqliteQuery(queryText: string, params: any[] = []) {
  let sql = queryText
    .replace(/::int/g, '')
    .replace(/::text/g, '')
    .replace(/\s+FOR\s+UPDATE/gi, '')
    .replace(/NOW\(\)/gi, "strftime('%s', 'now') * 1000")
    .replace(/current_database\(\)/gi, "'footballtray_test' as current_database")
    .replace(/\$\d+/g, '?');

  const trimmed = sql.trim().toUpperCase();
  if (trimmed.startsWith('SELECT') || trimmed.startsWith('WITH') || trimmed.startsWith('PRAGMA')) {
    const rows = sqlite.prepare(sql).all(...params);
    return { rows, rowCount: rows.length };
  } else {
    const info = sqlite.prepare(sql).run(...params);
    return { rows: [], rowCount: info.changes };
  }
}

export const pool = {
  connect: async () => ({
    query: async (sqlText: string, params: any[] = []) => executeSqliteQuery(sqlText, params),
    release: () => {},
  }),
  query: async (sqlText: string, params: any[] = []) => executeSqliteQuery(sqlText, params),
  end: async () => {
    try {
      sqlite.close();
    } catch {
      // ignore if already closed
    }
  },
  on: () => {},
};

export { schema };
