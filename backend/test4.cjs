const Database = require('better-sqlite3');
const db = new Database(':memory:');
db.prepare('CREATE TABLE api_request_ledger (id, provider, endpoint, operation, priority, success, http_status, response_time_ms, cost_units, requested_at, cycle_id, competition_id);').run();
const sql = "INSERT INTO api_request_ledger (id, provider, endpoint, operation, priority, success, http_status, response_time_ms, cost_units, requested_at, cycle_id, competition_id) VALUES (?, ?, ?, ?, ?, false, 0, 0, 1, (strftime('%s', 'now') * 1000), ?, ?)";
const params = ['ledg_123', 'mock', '/schedule', 'DISCOVER_SCHEDULE', 'NORMAL', null, null];
try {
  db.prepare(sql).run(...params);
  console.log('Success INSERT');
} catch (e) {
  console.log('Error INSERT:', e);
}

const sql2 = "SELECT COUNT(*) as n, SUM(cost_units) as costs FROM api_request_ledger WHERE provider = ? AND requested_at >= ? AND requested_at < (? + 86400000)";
const params2 = ['mock', '2026-10-05', '2026-10-05'];
try {
  db.prepare(sql2).all(...params2);
  console.log('Success SELECT 1');
} catch (e) {
  console.log('Error SELECT 1:', e);
}

const sql3 = `UPDATE api_request_ledger
       SET http_status = ?,
           response_time_ms = ?,
           success = ?,
           provider_daily_remaining = ?,
           provider_minute_remaining = ?,
           error_message = ?
       WHERE id = ?`;
const params3 = [200, 150, 1, 99, 9, null, 'ledg_123'];
db.prepare('ALTER TABLE api_request_ledger ADD COLUMN provider_daily_remaining;').run();
db.prepare('ALTER TABLE api_request_ledger ADD COLUMN provider_minute_remaining;').run();
db.prepare('ALTER TABLE api_request_ledger ADD COLUMN error_message;').run();
try {
  db.prepare(sql3).run(...params3);
  console.log('Success UPDATE');
} catch (e) {
  console.log('Error UPDATE:', e);
}
