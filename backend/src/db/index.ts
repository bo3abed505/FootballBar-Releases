import { drizzle } from 'drizzle-orm/node-postgres';
import pg from 'pg';
import * as schema from './schema.js';
import * as dotenv from 'dotenv';

dotenv.config();

const { Pool } = pg;

export const databaseUrl = process.env.DATABASE_URL || 'postgresql://postgres:postgres@localhost:5432/footballtray?schema=public';

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
 * Checks if the PostgreSQL database is reachable.
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
