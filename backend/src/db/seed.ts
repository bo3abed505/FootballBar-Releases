import { pool, sqlite } from './index.js';

/**
 * Eradicates any legacy ghost mock catalog entities from the SQLite database.
 * Dev catalog seeding is permanently eradicated; an empty database remains
 * completely empty until populated exclusively by actual API-Football payloads.
 */
export async function seedDatabase(): Promise<void> {
  console.log('[SEED] Purging legacy ghost mock catalog entities if present...');
  try {
    sqlite.exec(`
      PRAGMA foreign_keys = OFF;
      DELETE FROM events WHERE fixture_id IN ('fix_elclasico_2026', 'fix_spain_egypt_2026') OR id LIKE '%mock%' OR event_uid LIKE 'evt_fix_%mock%';
      DELETE FROM fixtures WHERE id IN ('fix_elclasico_2026', 'fix_spain_egypt_2026') OR schedule_source_provider = 'mock' OR id LIKE '%mock%';
      DELETE FROM competition_seasons WHERE id IN ('season_laliga_2025', 'season_egypt_2025', 'season_international_2025') OR id LIKE '%mock%';
      DELETE FROM competitions WHERE name LIKE '%(Dev Mock)%' OR id LIKE '%mock%' OR id IN ('comp_laliga', 'comp_egypt_pl', 'comp_international');
      DELETE FROM teams WHERE name LIKE '%(Dev Mock)%' OR id LIKE '%mock%' OR id IN ('team_barcelona', 'team_realmadrid', 'team_alahly', 'team_egypt', 'team_spain');
      PRAGMA foreign_keys = ON;
    `);
    console.log('[SEED] Ghost mock entities purged. Dev seeding is eradicated; SQLite database remains pure API-Football only.');
  } catch (err: any) {
    console.warn(`[SEED:WARN] Purge warning: ${err?.message}`);
  }
}

// Auto-run if executed directly
if (process.argv[1]?.endsWith('seed.ts') || process.argv[1]?.endsWith('seed.js')) {
  seedDatabase()
    .catch((err) => {
      console.error('[SEED] Execution failed:', err);
    })
    .finally(async () => {
      if (pool?.end) {
        await pool.end();
      }
    });
}

