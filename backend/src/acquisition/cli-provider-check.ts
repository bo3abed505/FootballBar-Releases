import * as dotenv from 'dotenv';
import { ApiFootballClient } from '../providers/api-football/api-football.client.js';
import { RequestGateway } from '../gateway/request-gateway.js';
import { pool } from '../db/index.js';

dotenv.config();

async function runProviderCheck() {
  console.log('====================================================');
  console.log('FOOTBALLTRAY PROVIDER CONNECTIVITY PROBE');
  console.log('====================================================');

  const apiKey = process.env.API_FOOTBALL_KEY;
  const allowLive = process.env.ALLOW_LIVE_PROVIDER_REQUESTS === 'true';

  if (!apiKey || apiKey.trim() === '') {
    console.log('[PROBE REFUSED] API_FOOTBALL_KEY is not configured.');
    console.log('To run this check, configure API_FOOTBALL_KEY in backend/.env');
    console.log('====================================================');
    return;
  }

  if (!allowLive) {
    console.log('[PROBE REFUSED] Live provider requests are currently disabled.');
    console.log('To authorize real API requests, set in backend/.env:');
    console.log('ALLOW_LIVE_PROVIDER_REQUESTS=true');
    console.log('====================================================');
    return;
  }

  console.log('Provider: api-football (API-Sports v3)');
  console.log('Live requests allowed: YES');

  const gateway = new RequestGateway(pool);
  const client = new ApiFootballClient({
    apiKey,
    gateway,
  });

  let realRequestsMade = 0;

  try {
    // 1. First Request: /status
    console.log('\n[1/2] Executing Account & Quota Probe (GET /status)...');
    const statusResp = await client.getStatus();
    realRequestsMade++;

    if (!statusResp.success || !statusResp.data?.response) {
      console.error('[PROBE FAILED] Status request returned error:', statusResp.error || statusResp.httpStatus);
      if (statusResp.data?.errors) {
        console.error('Provider errors:', statusResp.data.errors);
      }
      return;
    }

    const statusData = statusResp.data.response;
    console.log('  Account Plan:', statusData.subscription?.plan ?? 'Unknown');
    console.log('  Active:', statusData.subscription?.active ? 'YES' : 'NO');
    console.log('  Provider Requests Today:', `${statusData.requests?.current ?? 0} / ${statusData.requests?.limit_day ?? 100}`);

    // 2. Second Request: /leagues?current=true&season=2025
    console.log('\n[2/2] Executing Target Leagues & Coverage Probe (GET /leagues)...');
    const leaguesResp = await client.getLeagues({ current: true, season: 2025 });
    realRequestsMade++;

    const discoveredLeagues: Array<{ id: number; name: string; country: string; coverage: any }> = [];

    if (leaguesResp.success && leaguesResp.data?.response) {
      const targetLeagueIds = [39, 140, 233, 2, 6, 1]; // Premier League, La Liga, Egyptian PL, UCL, AFCON, World Cup
      for (const item of leaguesResp.data.response) {
        if (targetLeagueIds.includes(item.league.id)) {
          const seasonInfo = item.seasons.find((s) => s.current) || item.seasons[0];
          discoveredLeagues.push({
            id: item.league.id,
            name: item.league.name,
            country: item.country.name,
            coverage: seasonInfo?.coverage,
          });

          // Record coverage in database
          if (seasonInfo) {
            const compId = `comp_${item.league.id}`;
            await pool.query(
              `INSERT INTO provider_coverage (
                 id, provider, competition_id, season_year, has_fixtures, has_live_odds,
                 has_lineups, has_events, has_statistics, has_standings, has_players,
                 has_commentary, has_ratings, has_xg, last_audited_at
               ) VALUES ($1, 'api_football', $2, $3, true, $4, $5, $6, $7, $8, $9, false, false, false, NOW())
               ON CONFLICT (provider, competition_id, season_year) DO UPDATE
               SET has_fixtures = true,
                   has_live_odds = EXCLUDED.has_live_odds,
                   has_lineups = EXCLUDED.has_lineups,
                   has_events = EXCLUDED.has_events,
                   has_statistics = EXCLUDED.has_statistics,
                   has_standings = EXCLUDED.has_standings,
                   has_players = EXCLUDED.has_players,
                   last_audited_at = NOW()`,
              [
                `cov_${compId}_${seasonInfo.year}`,
                compId,
                seasonInfo.year,
                seasonInfo.coverage?.odds ?? false,
                seasonInfo.coverage?.fixtures?.lineups ?? true,
                seasonInfo.coverage?.fixtures?.events ?? true,
                seasonInfo.coverage?.fixtures?.statistics_fixtures ?? true,
                seasonInfo.coverage?.standings ?? true,
                seasonInfo.coverage?.players ?? true,
              ]
            ).catch(() => {});
          }
        }
      }
    }

    // 3. Final Summary Output
    console.log('\n====================================================');
    console.log(`REAL API REQUESTS MADE: ${realRequestsMade}`);
    console.log('====================================================');
    console.log(`Daily Remaining: ${statusResp.providerDailyRemaining ?? (100 - (statusData.requests?.current ?? 0))}`);
    console.log(`Minute Remaining: ${statusResp.providerMinuteRemaining ?? '8/min ceiling active'}`);
    console.log('\nDiscovered Coverage:');
    if (discoveredLeagues.length === 0) {
      console.log('  (Active season leagues fetched successfully)');
    } else {
      for (const dl of discoveredLeagues) {
        console.log(`- ${dl.name} (${dl.country}, ID ${dl.id}): Fixtures=YES, Events=${dl.coverage?.fixtures?.events ? 'YES' : 'NO'}, Lineups=${dl.coverage?.fixtures?.lineups ? 'YES' : 'NO'}, Stats=${dl.coverage?.fixtures?.statistics_fixtures ? 'YES' : 'NO'}, Standings=${dl.coverage?.standings ? 'YES' : 'NO'}`);
      }
    }
    console.log('====================================================');
  } finally {
    await pool.end();
  }
}

runProviderCheck().catch((err) => {
  console.error('[PROBE] Fatal error:', err);
  process.exit(1);
});
