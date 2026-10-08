const Database = require('better-sqlite3');
const path = require('path');

const dbPath = path.resolve(__dirname, '../../local.db');
console.log(`[PURGE] Opening database at: ${dbPath}`);
const db = new Database(dbPath);

db.pragma('foreign_keys = OFF');

const resEvt = db.prepare("DELETE FROM events WHERE fixture_id IN ('fix_elclasico_2026', 'fix_spain_egypt_2026') OR id LIKE '%mock%' OR event_uid LIKE 'evt_fix_%mock%';").run();
console.log(`[PURGE] Removed ${resEvt.changes} ghost mock events.`);

const resFix = db.prepare("DELETE FROM fixtures WHERE id IN ('fix_elclasico_2026', 'fix_spain_egypt_2026') OR schedule_source_provider = 'mock' OR id LIKE '%mock%';").run();
console.log(`[PURGE] Removed ${resFix.changes} ghost mock fixtures.`);

const resSeasons = db.prepare("DELETE FROM competition_seasons WHERE id IN ('season_laliga_2025', 'season_egypt_2025', 'season_international_2025') OR id LIKE '%mock%';").run();
console.log(`[PURGE] Removed ${resSeasons.changes} ghost mock seasons.`);

const resComps = db.prepare("DELETE FROM competitions WHERE name LIKE '%(Dev Mock)%' OR id IN ('comp_laliga', 'comp_egypt_pl', 'comp_international') OR id LIKE '%mock%';").run();
console.log(`[PURGE] Removed ${resComps.changes} ghost mock competitions.`);

const resTeams = db.prepare("DELETE FROM teams WHERE name LIKE '%(Dev Mock)%' OR id IN ('team_barcelona', 'team_realmadrid', 'team_alahly', 'team_egypt', 'team_spain') OR id LIKE '%mock%';").run();
console.log(`[PURGE] Removed ${resTeams.changes} ghost mock teams.`);

db.pragma('foreign_keys = ON');

console.log('[PURGE] Ghost mock purge complete.');

