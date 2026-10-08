import Database from 'better-sqlite3';
import * as dotenv from 'dotenv';
import * as path from 'path';
import * as fs from 'fs';
import { drizzle } from 'drizzle-orm/better-sqlite3';
import * as schema from './schema.js';
import { generateCanonicalId } from './canonical-id.js';

dotenv.config();

async function setupTestDatabase() {
  console.log('====================================================');
  console.log('FOOTBALLTRAY TEST DATABASE SETUP (SQLite)');
  console.log('====================================================');

  const testUrl = process.env.DATABASE_URL_TEST || './local_test.db';
  let dbPath = testUrl.replace(/^(file:|sqlite:\/\/|sqlite:)/, '');
  const resolved = path.isAbsolute(dbPath) ? dbPath : path.resolve(process.cwd(), dbPath);
  const dir = path.dirname(resolved);
  if (!fs.existsSync(dir)) {
    fs.mkdirSync(dir, { recursive: true });
  }

  const sqlite = new Database(resolved);
  sqlite.pragma('journal_mode = WAL');
  sqlite.pragma('foreign_keys = ON');

  try {
    const testDb = drizzle(sqlite, { schema });

    console.log('[TEST_DB_SETUP] Purging any legacy ghost mock catalog entities...');
    sqlite.exec(`
      DELETE FROM fixtures WHERE id LIKE '%mock%' OR id IN ('fix_elclasico_2026', 'fix_spain_egypt_2026');
      DELETE FROM events WHERE id LIKE '%mock%' OR id LIKE '%elclasico%';
      DELETE FROM competitions WHERE name LIKE '%(Dev Mock)%' OR id LIKE '%mock%' OR id IN ('comp_laliga', 'comp_egypt_pl', 'comp_international');
      DELETE FROM competition_seasons WHERE id LIKE '%mock%' OR id IN ('season_laliga_2025', 'season_egypt_2025', 'season_international_2025');
      DELETE FROM teams WHERE name LIKE '%(Dev Mock)%' OR id LIKE '%mock%' OR id IN ('team_barcelona', 'team_realmadrid', 'team_alahly', 'team_egypt', 'team_spain');
    `);

    console.log('====================================================');
    console.log('[TEST_DB_SETUP] TEST DATABASE SETUP COMPLETE (Clean DB)');
    console.log('====================================================');
  } finally {
    sqlite.close();
  }
}

setupTestDatabase().catch((err) => {
  console.error('[TEST_DB_SETUP] Failed:', err);
  process.exit(1);
});
