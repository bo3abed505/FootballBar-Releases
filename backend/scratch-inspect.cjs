// Temporary read-only DB inspection script (Strike 13). Delete after use.
const Database = require('better-sqlite3');
const db = new Database(process.argv[2] || 'local.db', { readonly: true });
const q = (label, sql) => {
  try { console.log(`\n== ${label}`); console.table(db.prepare(sql).all()); }
  catch (e) { console.log(`  ERR: ${e.message}`); }
};
q('tables', "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name");
q('row counts', `SELECT 'competitions' t, COUNT(*) n FROM competitions UNION ALL SELECT 'teams', COUNT(*) FROM teams UNION ALL SELECT 'fixtures', COUNT(*) FROM fixtures UNION ALL SELECT 'api_request_ledger', COUNT(*) FROM api_request_ledger UNION ALL SELECT 'scheduler_cycles', COUNT(*) FROM scheduler_cycles`);
q('mock competitions', "SELECT * FROM competitions WHERE name LIKE '%Mock%' OR id LIKE '%mock%' OR id LIKE 'ft_%' LIMIT 20");
q('mock teams', "SELECT id, name FROM teams WHERE name LIKE '%Mock%' OR id LIKE '%mock%' LIMIT 20");
q('competitions sample', 'SELECT * FROM competitions LIMIT 15');
q('ledger last 15', 'SELECT * FROM api_request_ledger ORDER BY requested_at DESC LIMIT 15');
q('ledger by endpoint/status', 'SELECT endpoint, status_code, success, COUNT(*) n FROM api_request_ledger GROUP BY 1,2,3');
q('scheduler_cycles last 10', 'SELECT * FROM scheduler_cycles ORDER BY rowid DESC LIMIT 10');
q('budget', 'SELECT * FROM provider_daily_budget ORDER BY rowid DESC LIMIT 5');
q('fixtures sample', 'SELECT * FROM fixtures ORDER BY rowid DESC LIMIT 5');
