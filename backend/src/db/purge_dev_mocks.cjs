const Database = require('better-sqlite3');
const path = require('path');

const dbPath = path.resolve(__dirname, '../../local.db');
console.log(`[PURGE] Opening database at: ${dbPath}`);
const db = new Database(dbPath);

const resComps = db.prepare("DELETE FROM competitions WHERE name LIKE '%(Dev Mock)%';").run();
console.log(`[PURGE] Removed ${resComps.changes} ghost mock competitions.`);

const resTeams = db.prepare("DELETE FROM teams WHERE name LIKE '%(Dev Mock)%';").run();
console.log(`[PURGE] Removed ${resTeams.changes} ghost mock teams.`);

const resFix = db.prepare("DELETE FROM fixtures WHERE id IN ('fix_elclasico_2026', 'fix_spain_egypt_2026') OR schedule_source_provider = 'mock' OR id LIKE 'fix_%mock%';").run();
console.log(`[PURGE] Removed ${resFix.changes} ghost mock fixtures.`);

console.log('[PURGE] Ghost mock purge complete.');
