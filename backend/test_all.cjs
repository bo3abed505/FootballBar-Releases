const { pool } = require('./dist/db/index.js');
const queries = [
  [`INSERT INTO api_request_ledger (id, provider, endpoint, operation, priority, success, http_status, response_time_ms, cost_units, requested_at, cycle_id, competition_id) VALUES ($1, $2, $3, $4, $5, false, 0, 0, 1, NOW(), $6, $7)`, ['ledg', 'api', '/a', 'DISCOVER_SCHEDULE', 'NORMAL', null, null]],
  [`SELECT * FROM provider_daily_budget WHERE provider = $1 AND date_utc = $2 FOR UPDATE`, ['mock', '2026-10-05']],
  [`SELECT COALESCE(SUM(cost_units), 0)::int as ledger_consumed FROM api_request_ledger WHERE provider = $1 AND requested_at >= $2::date AND requested_at < ($2::date + INTERVAL '1 day')`, ['mock', '2026-10-05']],
  [`SELECT COALESCE(SUM(cost_units), 0)::int as ledger_consumed_yesterday FROM api_request_ledger WHERE provider = $1 AND requested_at >= $2::date AND requested_at < ($2::date + INTERVAL '1 day')`, ['mock', '2026-10-05']],
  [`INSERT INTO provider_daily_budget (provider, date_utc, consumed_calls, remaining_calls, is_exhausted) VALUES ($1, $2, $3, $4, false)`, ['mock', '2026-10-05', 0, 100]],
  [`SELECT COUNT(*) as recent_count FROM api_request_ledger WHERE provider = $1 AND requested_at >= NOW() - INTERVAL '1 minute'`, ['mock']],
  [`UPDATE api_request_ledger SET http_status = $1, response_time_ms = $2, success = $3, provider_daily_remaining = $4, provider_minute_remaining = $5, error_message = $6 WHERE id = $7`, [200, 100, true, 99, 9, null, 'ledg']],
  [`SELECT COUNT(*) as cnt FROM api_request_ledger WHERE provider = $1 AND operation = $2 AND cycle_id = $3`, ['mock', 'op', 'cyc']],
  [`SELECT MAX(requested_at) as requested_at FROM api_request_ledger WHERE provider = $1 AND operation = $2`, ['mock', 'op']],
  [`SELECT operation, COUNT(*) as c FROM api_request_ledger WHERE provider = 'system' AND cycle_id = $1 GROUP BY operation`, ['cyc']],
];

async function run() {
  const c = await pool.connect();
  for (const [q, p] of queries) {
    try {
      await c.query(q, p);
      console.log('OK:', q.substring(0, 50));
    } catch (e) {
      console.error('ERROR:', q.substring(0, 50), e.message);
    }
  }
  c.release();
}
run();
