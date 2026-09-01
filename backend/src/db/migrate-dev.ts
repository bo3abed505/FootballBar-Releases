import pg from 'pg';
import * as dotenv from 'dotenv';

dotenv.config();

const { Pool } = pg;

async function migrateDev() {
  const pool = new Pool({
    connectionString: process.env.DATABASE_URL,
  });

  try {
    console.log('[MIGRATE_DEV] Applying schema updates to development database...');

    // 1. Update api_request_ledger columns
    await pool.query(`
      ALTER TABLE api_request_ledger
      ADD COLUMN IF NOT EXISTS operation text DEFAULT 'query' NOT NULL,
      ADD COLUMN IF NOT EXISTS priority text DEFAULT 'NORMAL' NOT NULL,
      ADD COLUMN IF NOT EXISTS success boolean DEFAULT true NOT NULL,
      ADD COLUMN IF NOT EXISTS provider_daily_remaining integer,
      ADD COLUMN IF NOT EXISTS provider_minute_remaining integer;

      CREATE INDEX IF NOT EXISTS ledger_provider_requested_idx
      ON api_request_ledger (provider, requested_at);
    `);

    // 2. Update provider_coverage columns
    await pool.query(`
      ALTER TABLE provider_coverage
      ADD COLUMN IF NOT EXISTS has_fixtures boolean DEFAULT true NOT NULL,
      ADD COLUMN IF NOT EXISTS has_commentary boolean DEFAULT false NOT NULL,
      ADD COLUMN IF NOT EXISTS has_ratings boolean DEFAULT false NOT NULL,
      ADD COLUMN IF NOT EXISTS has_xg boolean DEFAULT false NOT NULL;
    `);

    console.log('[MIGRATE_DEV] Development database schema updated successfully.');
  } finally {
    await pool.end();
  }
}

migrateDev().catch((err) => {
  console.error('[MIGRATE_DEV] Failed:', err);
  process.exit(1);
});
