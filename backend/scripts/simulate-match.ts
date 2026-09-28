import pg from 'pg';
import * as dotenv from 'dotenv';
import * as fs from 'fs';
import * as path from 'path';
import { fileURLToPath } from 'url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

// 1. Resolve Target Database URL (production default for WinUI 3 end-to-end testing)
function resolveDatabaseUrl(cliDb?: string): string {
  if (cliDb && cliDb.trim().length > 0) {
    return cliDb.trim();
  }

  const prodLocalPath = path.resolve(__dirname, '../.env.production.local');
  if (fs.existsSync(prodLocalPath)) {
    const parsed = dotenv.parse(fs.readFileSync(prodLocalPath));
    if (parsed.DATABASE_URL) return parsed.DATABASE_URL;
  }

  if (process.env.DATABASE_URL && process.env.DATABASE_URL.trim().length > 0) {
    return process.env.DATABASE_URL.trim();
  }

  const defaultEnvPath = path.resolve(__dirname, '../.env');
  if (fs.existsSync(defaultEnvPath)) {
    const parsed = dotenv.parse(fs.readFileSync(defaultEnvPath));
    if (parsed.DATABASE_URL) return parsed.DATABASE_URL;
  }

  return 'postgresql://postgres:postgres@localhost:5432/footballtray_prod';
}

const MOCK_FIXTURE_ID = 'fix_mock_9999';

async function purgeMockData(client: pg.PoolClient): Promise<number> {
  const res = await client.query('DELETE FROM fixtures WHERE id = $1', [MOCK_FIXTURE_ID]);
  return res.rowCount ?? 0;
}

async function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function main() {
  const args = process.argv.slice(2);
  const isCleanupOnly = args.includes('--cleanup');
  const keepAtEnd = args.includes('--keep');

  const speedArgIdx = args.findIndex((a) => a === '--speed');
  const speedSec = speedArgIdx !== -1 && args[speedArgIdx + 1] ? parseInt(args[speedArgIdx + 1], 10) : 12;

  const dbArgIdx = args.findIndex((a) => a === '--db');
  const cliDb = dbArgIdx !== -1 && args[dbArgIdx + 1] ? args[dbArgIdx + 1] : undefined;

  const matchArgIdx = args.findIndex((a) => a === '--match');
  const matchFile = matchArgIdx !== -1 && args[matchArgIdx + 1] ? args[matchArgIdx + 1] : 'el-clasico.json';

  const dbUrl = resolveDatabaseUrl(cliDb);
  const pool = new pg.Pool({ connectionString: dbUrl });

  const client = await pool.connect();

  try {
    if (isCleanupOnly) {
      console.log(`[Simulator] Connecting to database: ${dbUrl.replace(/:[^:@]+@/, ':***@')}`);
      console.log(`[Simulator] Purging mock fixture '${MOCK_FIXTURE_ID}' from database...`);
      const deleted = await purgeMockData(client);
      console.log(`[Simulator] Clean exit: Purged ${deleted} fixture record(s) and cascading child rows.`);
      return;
    }

    // Load static match scenario
    const payloadPath = path.isAbsolute(matchFile) ? matchFile : path.resolve(__dirname, 'mock-matches', matchFile);
    if (!fs.existsSync(payloadPath)) {
      throw new Error(`Scenario payload not found at: ${payloadPath}`);
    }
    const data = JSON.parse(fs.readFileSync(payloadPath, 'utf8'));

    // Register process signal handlers for clean rollback on Ctrl+C
    let cleanedUp = false;
    const cleanupAndExit = async () => {
      if (!cleanedUp) {
        cleanedUp = true;
        console.log('\n[Simulator] Interrupted (SIGINT/SIGTERM). Cleaning up mock data...');
        try {
          await purgeMockData(client);
          console.log('[Simulator] Mock data cleanly purged.');
        } catch (err: any) {
          console.error('[Simulator] Cleanup error:', err.message);
        } finally {
          client.release();
          await pool.end();
          process.exit(0);
        }
      }
    };
    process.on('SIGINT', cleanupAndExit);
    process.on('SIGTERM', cleanupAndExit);

    console.log('============================================================');
    console.log(' FootballTray Live Match Simulation Engine');
    console.log('============================================================');
    console.log(` Database:   ${dbUrl.replace(/:[^:@]+@/, ':***@')}`);
    console.log(` Fixture ID: ${data.fixture.id}`);
    console.log(` Match:      ${data.fixture.homeTeamId} vs ${data.fixture.awayTeamId}`);
    console.log(` Interval:   ${speedSec}s per lifecycle event`);
    console.log(` Preserved:  ${keepAtEnd ? 'YES (--keep)' : 'NO (auto-purge at FT)'}`);
    console.log('============================================================\n');

    // Step 0: Ensure database is clean of prior runs
    await purgeMockData(client);

    // Step 1: Initial Insert (PREMATCH)
    const kickoffTime = new Date();
    await client.query(
      `INSERT INTO fixtures (
        id, canonical_id, competition_id, season_id, home_team_id, away_team_id,
        state, elapsed_minute, extra_minute, home_score, away_score,
        kickoff_time, venue, referee, created_at, updated_at
      ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, NOW(), NOW())`,
      [
        data.fixture.id,
        data.fixture.canonicalId,
        data.fixture.competitionId,
        data.fixture.seasonId,
        data.fixture.homeTeamId,
        data.fixture.awayTeamId,
        'PREMATCH',
        0,
        0,
        0,
        0,
        kickoffTime,
        data.fixture.venue,
        data.fixture.referee,
      ]
    );

    // Ensure teams have valid logo URLs for crest loading
    if (data.fixture.homeTeamLogoUrl) {
      await client.query(
        `UPDATE teams SET logo_url = $1, updated_at = NOW() WHERE id = $2`,
        [data.fixture.homeTeamLogoUrl, data.fixture.homeTeamId]
      );
    }
    if (data.fixture.awayTeamLogoUrl) {
      await client.query(
        `UPDATE teams SET logo_url = $1, updated_at = NOW() WHERE id = $2`,
        [data.fixture.awayTeamLogoUrl, data.fixture.awayTeamId]
      );
    }

    // Insert Lineups for Visual Pitch preview
    if (Array.isArray(data.lineups)) {
      for (const lineup of data.lineups) {
        const lineupId = `lineup_${data.fixture.id}_${lineup.teamId}`;
        await client.query(
          `INSERT INTO lineups (id, fixture_id, team_id, formation, coach_name, created_at)
           VALUES ($1, $2, $3, $4, $5, NOW())`,
          [lineupId, data.fixture.id, lineup.teamId, lineup.formation, lineup.coachName]
        );

        if (Array.isArray(lineup.startXi)) {
          for (let i = 0; i < lineup.startXi.length; i++) {
            const p = lineup.startXi[i];
            await client.query(
              `INSERT INTO lineup_players (id, lineup_id, player_id, name, number, position, grid, is_starter)
               VALUES ($1, $2, $3, $4, $5, $6, $7, true)`,
              [`lp_${lineupId}_${i + 1}`, lineupId, `ply_mock_${lineup.teamId}_${p.number}`, p.name, p.number, p.position, p.grid]
            );
          }
        }

        if (Array.isArray(lineup.substitutes)) {
          for (let i = 0; i < lineup.substitutes.length; i++) {
            const p = lineup.substitutes[i];
            await client.query(
              `INSERT INTO lineup_players (id, lineup_id, player_id, name, number, position, grid, is_starter)
               VALUES ($1, $2, $3, $4, $5, $6, $7, false)`,
              [`lp_sub_${lineupId}_${i + 1}`, lineupId, `ply_mock_sub_${lineup.teamId}_${p.number}`, p.name, p.number, p.position, p.grid ?? null]
            );
          }
        }
      }
    }

    console.log('[Simulator] Initialized fixture in PREMATCH state with starting lineups and bench.');

    // Step 2: Iterate through match timeline steps
    for (const step of data.timeline) {
      console.log(`\n--> [Step ${step.step}/${data.timeline.length}] ${step.title}`);
      console.log(`    State: ${step.state} | Minute: ${step.elapsed}' | Score: ${step.homeScore} - ${step.awayScore}`);

      // Update Fixture record
      await client.query(
        `UPDATE fixtures
         SET state = $1, elapsed_minute = $2, extra_minute = $3,
             home_score = $4, away_score = $5, home_score_ht = $6, away_score_ht = $7,
             updated_at = NOW()
         WHERE id = $8`,
        [
          step.state,
          step.elapsed,
          step.extra,
          step.homeScore,
          step.awayScore,
          step.homeScoreHt ?? null,
          step.awayScoreHt ?? null,
          data.fixture.id,
        ]
      );

      // Insert Event if present
      if (step.event) {
        const eventUid = `uid_${step.event.id}_${Date.now()}`;
        await client.query(
          `INSERT INTO events (
            id, event_uid, fixture_id, team_id, type, minute, extra_minute,
            player_name, assist_player_name, description, timestamp
          ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, NOW())`,
          [
            step.event.id,
            eventUid,
            data.fixture.id,
            step.event.teamId ?? data.fixture.homeTeamId,
            step.event.type,
            step.event.minute,
            step.event.extraMinute ?? 0,
            step.event.playerName ?? null,
            step.event.assistPlayerName ?? null,
            step.event.description ?? null,
          ]
        );
        console.log(`    Event Inserted: [${step.event.type}] ${step.event.description}`);
      }

      console.log(`    Waiting ${speedSec}s (check WinUI 3 client ticker & audio)...`);
      await sleep(speedSec * 1000);
    }

    console.log('\n============================================================');
    console.log(' Match Simulation Completed Successfully!');
    console.log('============================================================');

    if (!keepAtEnd) {
      console.log('[Simulator] Tearing down mock data to restore pristine database...');
      await purgeMockData(client);
      console.log('[Simulator] Cleaned up.');
    } else {
      console.log('[Simulator] Preserved mock fixture in FINISHED state (--keep enabled).');
      console.log('[Simulator] Run `npm run simulate:match -- --cleanup` when finished testing.');
    }
  } finally {
    client.release();
    await pool.end();
  }
}

main().catch((err) => {
  console.error('[Simulator] Fatal error:', err);
  process.exit(1);
});
