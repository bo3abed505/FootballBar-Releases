import { drizzle } from 'drizzle-orm/node-postgres';
import pg from 'pg';
import * as schema from './schema.js';
import * as dotenv from 'dotenv';

dotenv.config();

const { Pool } = pg;

export const isTestEnvironment = process.env.NODE_ENV === 'test' || Boolean(process.env.VITEST);

function resolveDatabaseUrl(): string {
  if (isTestEnvironment) {
    const testUrl = process.env.DATABASE_URL_TEST;
    if (!testUrl || testUrl.trim().length === 0) {
      throw new Error(
        '[SAFETY] Integration tests require DATABASE_URL_TEST to be defined in .env or environment.\n' +
        'Automated tests are strictly forbidden from running against the development database.'
      );
    }
    // Safety check: ensure test URL is not pointing to the default dev database
    if (testUrl.includes('/footballtray?') || testUrl.endsWith('/footballtray')) {
      throw new Error(
        '[SAFETY] DATABASE_URL_TEST must point to a dedicated test database (e.g. footballtray_test), not the primary development database.'
      );
    }
    return testUrl.trim();
  }

  const devUrl = process.env.DATABASE_URL;
  if (!devUrl || devUrl.trim().length === 0) {
    throw new Error('[DB] DATABASE_URL is not defined in environment or .env');
  }
  return devUrl.trim();
}

export const databaseUrl = resolveDatabaseUrl();

export const pool = new Pool({
  connectionString: databaseUrl,
  max: 10,
  idleTimeoutMillis: 30000,
  connectionTimeoutMillis: 5000,
});

// Suppress unhandled error crashes from idle clients
pool.on('error', (err) => {
  console.error('[DB] Unexpected idle client error:', err.message);
});

export const db = drizzle(pool, { schema });

/**
 * Checks if the configured PostgreSQL database is reachable.
 * Returns true if connected, false otherwise without throwing.
 */
export async function checkDatabaseConnection(): Promise<{ connected: boolean; latencyMs?: number; error?: string }> {
  const start = Date.now();
  try {
    const client = await pool.connect();
    try {
      await client.query('SELECT 1');
      return { connected: true, latencyMs: Date.now() - start };
    } finally {
      client.release();
    }
  } catch (err: any) {
    return {
      connected: false,
      error: err?.message ?? 'Database connection failed',
      latencyMs: Date.now() - start,
    };
  }
}

export { schema };
