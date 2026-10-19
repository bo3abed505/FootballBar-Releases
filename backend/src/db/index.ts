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
    rawUrl = process.env.DATABASE_URL_TEST || './local_test.db';
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

try {
  sqlite.exec('ALTER TABLE fixture_statistics ADD COLUMN expected_goals REAL;');
} catch {
  // column already exists or table not yet created
}

try {
  sqlite.exec('ALTER TABLE fixtures ADD COLUMN finalized_at INTEGER;');
} catch {
  // column already exists or table not yet created
}

export const db = drizzle(sqlite, { schema });

// Wrap db.transaction to support async transaction callbacks seamlessly with better-sqlite3
(db as any).transaction = async (cb: (tx: any) => any) => {
  if (sqlite.inTransaction) {
    return await cb(db);
  }
  sqlite.prepare('BEGIN').run();
  try {
    const res = await cb(db);
    if (sqlite.inTransaction) {
      sqlite.prepare('COMMIT').run();
    }
    return res;
  } catch (err) {
    if (sqlite.inTransaction) {
      sqlite.prepare('ROLLBACK').run();
    }
    throw err;
  }
};

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

function parseSqlDate(str: string): number | null {
  let ts = Date.parse(str);
  if (!isNaN(ts)) return ts;
  const normalized = str.replace(/([+-]\d{2})$/, '$1:00');
  ts = Date.parse(normalized);
  if (!isNaN(ts)) return ts;
  ts = Date.parse(normalized.replace(' ', 'T'));
  if (!isNaN(ts)) return ts;
  return null;
}

function sanitizeSqliteParam(val: any): any {
  if (val === undefined || val === null) {
    return null;
  }
  if (val instanceof Date) {
    return val.getTime();
  }
  if (typeof val === 'boolean') {
    return val ? 1 : 0;
  }
  if (typeof val === 'number' || typeof val === 'bigint' || Buffer.isBuffer(val)) {
    return val;
  }
  if (typeof val === 'string') {
    const trimmed = val.trim();
    if (/^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}/.test(trimmed)) {
      const ts = parseSqlDate(trimmed);
      if (ts !== null) {
        return ts;
      }
    }
    return val;
  }
  if (typeof val === 'object') {
    return JSON.stringify(val);
  }
  return String(val);
}

/**
 * Safe adapter for any legacy scripts/services calling pool lifecycle or queries.
 * Maps PostgreSQL $1, $2, ... placeholders, array binds, and type conversions to SQLite.
 */
function executeSqliteQuery(queryText: string, params: any = []) {
  let sql = queryText
    .replace(/\s+FOR\s+UPDATE/gi, '')
    .replace(/current_database\(\)/gi, "'footballtray_test' as current_database")
    .replace(/\bLEAST\s*\(/gi, 'min(')
    .replace(/\bGREATEST\s*\(/gi, 'max(')
    .replace(/(requested_at\s*[><=]+\s*)(\(?\s*NOW\(\)\s+AT\s+TIME\s+ZONE\s+'[^']+'\s*\)?)::date/gi, "$1(strftime('%s', date('now')) * 1000)")
    .replace(/(\(?\s*NOW\(\)\s+AT\s+TIME\s+ZONE\s+'[^']+'\s*\)?)::date(\s*[-+]\s*INTERVAL)/gi, "(strftime('%s', date('now')) * 1000)$2")
    .replace(/(\(?\s*NOW\(\)\s+AT\s+TIME\s+ZONE\s+'[^']+'\s*\)?)::date/gi, "date('now')")
    .replace(/NOW\(\)::date/gi, "date('now')")
    .replace(/::[a-zA-Z0-9_]+(\[\])?/g, '')
    .replace(/INTERVAL\s+'(\d+)\s+([a-zA-Z]+)'/gi, (_, num, unit) => {
      const n = parseInt(num, 10);
      const u = unit.toLowerCase();
      if (u.startsWith('second')) return String(n * 1000);
      if (u.startsWith('minute')) return String(n * 60 * 1000);
      if (u.startsWith('hour')) return String(n * 3600 * 1000);
      if (u.startsWith('day')) return String(n * 86400 * 1000);
      return String(n * 1000);
    })
    .replace(/\s+AT\s+TIME\s+ZONE\s+'[^']+'/gi, '')
    .replace(/NOW\(\)/gi, "(strftime('%s', 'now') * 1000)")
    // Convert SQL date string literals like '2026-09-03 23:59:59+00' to epoch ms integers for SQLite timestamp columns
    .replace(/'(\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}(?::?\d{2})?)?)'/g, (m, dateStr) => {
      const ts = parseSqlDate(dateStr);
      if (ts !== null) {
        return String(ts);
      }
      return m;
    });


  const trimmed = sql.trim().toUpperCase();

  // PostgreSQL session commands (e.g. SET TIME ZONE) are no-ops in SQLite
  if (trimmed.startsWith('SET ')) {
    return { rows: [], rowCount: 0 };
  }

  // SQLite transaction controls using built-in inTransaction check
  if (trimmed === 'BEGIN' || trimmed === 'BEGIN TRANSACTION') {
    if (!sqlite.inTransaction) {
      sqlite.prepare('BEGIN').run();
    }
    return { rows: [], rowCount: 0 };
  }
  if (trimmed === 'COMMIT') {
    if (sqlite.inTransaction) {
      sqlite.prepare('COMMIT').run();
    }
    return { rows: [], rowCount: 0 };
  }
  if (trimmed === 'ROLLBACK') {
    if (sqlite.inTransaction) {
      sqlite.prepare('ROLLBACK').run();
    }
    return { rows: [], rowCount: 0 };
  }

  const mappedParams: any[] = [];
  let hadReplacedParams = false;

  // Unified single-pass regex matching either `= ANY($N)` or standalone `$N`
  const paramPattern = /(?:=\s*ANY\s*\(\s*\$(\d+)\s*\))|(?:\$(\d+))/gi;

  sql = sql.replace(paramPattern, (_match, anyIdxStr, singleIdxStr) => {
    hadReplacedParams = true;
    const numStr = anyIdxStr || singleIdxStr;
    const idx = parseInt(numStr, 10) - 1;
    const val = Array.isArray(params) && idx >= 0 && idx < params.length ? params[idx] : null;

    if (anyIdxStr) {
      // Postgres `= ANY($N)` mapped to SQLite `IN (?, ...)`
      if (Array.isArray(val) && val.length > 0) {
        for (const item of val) {
          mappedParams.push(sanitizeSqliteParam(item));
        }
        return `IN (${val.map(() => '?').join(', ')})`;
      } else {
        return 'IN (NULL)';
      }
    } else {
      // Positional parameter `$N` mapped to SQLite `?`
      if (Array.isArray(val)) {
        if (val.length === 0) {
          return 'NULL';
        }
        for (const item of val) {
          mappedParams.push(sanitizeSqliteParam(item));
        }
        return val.map(() => '?').join(', ');
      } else {
        mappedParams.push(sanitizeSqliteParam(val));
        return '?';
      }
    }
  });

  // Determine bindings: array of positional values or a single object parameter
  let paramsToBind: any = mappedParams;
  if (!hadReplacedParams) {
    if (Array.isArray(params)) {
      paramsToBind = params.map(sanitizeSqliteParam);
    } else if (params !== null && typeof params === 'object') {
      const sanitizedObj: Record<string, any> = {};
      for (const [k, v] of Object.entries(params)) {
        sanitizedObj[k] = sanitizeSqliteParam(v);
      }
      paramsToBind = sanitizedObj;
    }
  }

  const isReader =
    trimmed.startsWith('SELECT') ||
    trimmed.startsWith('WITH') ||
    trimmed.startsWith('PRAGMA') ||
    trimmed.startsWith('EXPLAIN') ||
    sql.toUpperCase().includes(' RETURNING ');

  function normalizeRows(rows: any[]): any[] {
    if (!Array.isArray(rows) || rows.length === 0) return rows;
    for (let i = 0; i < rows.length; i++) {
      const row = rows[i];
      if (row && typeof row === 'object') {
        if ('success' in row && typeof row.success === 'number') {
          row.success = Boolean(row.success);
        }
        if ('is_exhausted' in row && typeof row.is_exhausted === 'number') {
          row.is_exhausted = Boolean(row.is_exhausted);
        }
      }
    }
    return rows;
  }

  const stmt = sqlite.prepare(sql);
  if (Array.isArray(paramsToBind)) {
    if (isReader) {
      const rows = stmt.all(...paramsToBind);
      return { rows: normalizeRows(rows), rowCount: rows.length };
    } else {
      const info = stmt.run(...paramsToBind);
      return { rows: [], rowCount: info.changes };
    }
  } else {
    // Single object parameter binding for better-sqlite3 named parameters
    if (isReader) {
      const rows = stmt.all(paramsToBind);
      return { rows: normalizeRows(rows), rowCount: rows.length };
    } else {
      const info = stmt.run(paramsToBind);
      return { rows: [], rowCount: info.changes };
    }
  }
}


let transactionLock: Promise<void> = Promise.resolve();

async function acquireLock(): Promise<() => void> {
  let release: () => void;
  const nextLock = new Promise<void>((resolve) => {
    release = resolve;
  });
  const currentLock = transactionLock;
  transactionLock = transactionLock.then(() => nextLock);
  await currentLock;
  return release!;
}

export const pool = {
  connect: async () => {
    let hasLock = false;
    let releaseLock: (() => void) | null = null;

    return {
      query: async (sqlText: string, params: any = []) => {
        const trimmed = sqlText.trim().toUpperCase();
        if (trimmed.startsWith('BEGIN')) {
          if (!hasLock) {
            releaseLock = await acquireLock();
            hasLock = true;
          }
        }
        try {
          return executeSqliteQuery(sqlText, params);
        } finally {
          if (trimmed === 'COMMIT' || trimmed === 'ROLLBACK') {
            if (hasLock && releaseLock) {
              hasLock = false;
              const r = releaseLock;
              releaseLock = null;
              r();
            }
          }
        }
      },
      release: () => {
        if (hasLock && releaseLock) {
          hasLock = false;
          const r = releaseLock;
          releaseLock = null;
          r();
        }
      },
    };
  },
  query: async (sqlText: string, params: any = []) => executeSqliteQuery(sqlText, params),
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
