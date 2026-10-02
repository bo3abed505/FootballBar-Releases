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

    console.log('[TEST_DB_SETUP] Seeding baseline test data into test database...');
    // Competitions
    await testDb.insert(schema.competitions).values([
      { id: 'comp_laliga', canonicalId: generateCanonicalId('comp'), name: 'La Liga', code: 'ESP_L1', type: 'league', country: 'Spain', logoUrl: 'https://media.api-sports.io/football/leagues/140.png' },
      { id: 'comp_egypt_pl', canonicalId: generateCanonicalId('comp'), name: 'Egyptian Premier League', code: 'EGY_PL', type: 'league', country: 'Egypt', logoUrl: 'https://media.api-sports.io/football/leagues/233.png' },
      { id: 'comp_international', canonicalId: generateCanonicalId('comp'), name: 'International Friendly', code: 'INT_FRN', type: 'cup', country: 'World', logoUrl: 'https://media.api-sports.io/football/leagues/10.png' },
    ]).onConflictDoNothing();

    // Seasons
    await testDb.insert(schema.competitionSeasons).values([
      { id: 'season_laliga_2025', competitionId: 'comp_laliga', year: 2025, displayYear: '2025/2026', startDate: new Date('2025-08-15T00:00:00Z'), endDate: new Date('2026-05-24T00:00:00Z'), isCurrent: true },
      { id: 'season_egypt_2025', competitionId: 'comp_egypt_pl', year: 2025, displayYear: '2025/2026', startDate: new Date('2025-09-01T00:00:00Z'), endDate: new Date('2026-06-30T00:00:00Z'), isCurrent: true },
      { id: 'season_international_2025', competitionId: 'comp_international', year: 2025, displayYear: '2025/2026', startDate: new Date('2025-01-01T00:00:00Z'), endDate: new Date('2025-12-31T00:00:00Z'), isCurrent: true },
    ]).onConflictDoNothing();

    // Teams
    await testDb.insert(schema.teams).values([
      { id: 'team_barcelona', canonicalId: generateCanonicalId('team'), name: 'FC Barcelona', shortName: 'BAR', code: 'FCB', country: 'Spain', logoUrl: 'https://media.api-sports.io/football/teams/529.png' },
      { id: 'team_realmadrid', canonicalId: generateCanonicalId('team'), name: 'Real Madrid', shortName: 'RMA', code: 'RMA', country: 'Spain', logoUrl: 'https://media.api-sports.io/football/teams/541.png' },
      { id: 'team_alahly', canonicalId: generateCanonicalId('team'), name: 'Al Ahly SC', shortName: 'AHL', code: 'AHL', country: 'Egypt', logoUrl: 'https://media.api-sports.io/football/teams/1029.png' },
      { id: 'team_egypt', canonicalId: generateCanonicalId('team'), name: 'Egypt', shortName: 'EGY', code: 'EGY', country: 'Egypt', logoUrl: 'https://media.api-sports.io/football/teams/32.png' },
      { id: 'team_spain', canonicalId: generateCanonicalId('team'), name: 'Spain', shortName: 'ESP', code: 'ESP', country: 'Spain', logoUrl: 'https://media.api-sports.io/football/teams/9.png' },
    ]).onConflictDoNothing();

    // Fixtures
    await testDb.insert(schema.fixtures).values([
      {
        id: 'fix_elclasico_2026',
        canonicalId: generateCanonicalId('fix'),
        competitionId: 'comp_laliga',
        seasonId: 'season_laliga_2025',
        homeTeamId: 'team_barcelona',
        awayTeamId: 'team_realmadrid',
        round: 'Regular Season - Round 26',
        kickoffTime: new Date(Date.now() - 45 * 60 * 1000),
        state: 'LIVE',
        elapsedMinute: 72,
        extraMinute: 0,
        homeScore: 1,
        awayScore: 1,
        homeScoreHt: 1,
        awayScoreHt: 0,
        venue: 'Spotify Camp Nou',
        referee: 'J. Manzano',
        lastPolledAt: new Date(),
      },
      {
        id: 'fix_spain_egypt_2026',
        canonicalId: generateCanonicalId('fix'),
        competitionId: 'comp_international',
        seasonId: 'season_international_2025',
        homeTeamId: 'team_spain',
        awayTeamId: 'team_egypt',
        round: 'International Friendly',
        kickoffTime: new Date(Date.now() + 4 * 60 * 60 * 1000),
        state: 'PREMATCH',
        elapsedMinute: 0,
        extraMinute: 0,
        homeScore: 0,
        awayScore: 0,
        venue: 'Estadio Metropolitano',
        referee: 'M. Oliver',
        lastPolledAt: new Date(),
      },
    ]).onConflictDoNothing();

    // Events
    await testDb.insert(schema.events).values([
      { id: 'evt_elclasico_1', eventUid: 'evt_fix_elclasico_2026_1_0_PeriodStart_init', fixtureId: 'fix_elclasico_2026', teamId: 'team_barcelona', type: 'PeriodStart', minute: 1, description: 'First half kicks off' },
      { id: 'evt_elclasico_2', eventUid: 'evt_fix_elclasico_2026_17_0_Goal_bar10', fixtureId: 'fix_elclasico_2026', teamId: 'team_barcelona', type: 'Goal', minute: 17, playerName: 'L. Martinez', playerId: 'ply_bar_10', description: 'Goal! L. Martinez slots it into the bottom corner.' },
      { id: 'evt_elclasico_3', eventUid: 'evt_fix_elclasico_2026_35_0_YellowCard_rma5', fixtureId: 'fix_elclasico_2026', teamId: 'team_realmadrid', type: 'YellowCard', minute: 35, playerName: 'V. Silva', playerId: 'ply_rma_5', description: 'Yellow card for tactical foul' },
      { id: 'evt_elclasico_4', eventUid: 'evt_fix_elclasico_2026_45_0_PeriodEnd_ht', fixtureId: 'fix_elclasico_2026', teamId: 'team_barcelona', type: 'PeriodEnd', minute: 45, description: 'Half-time whistle' },
      { id: 'evt_elclasico_5', eventUid: 'evt_fix_elclasico_2026_46_0_PeriodStart_2h', fixtureId: 'fix_elclasico_2026', teamId: 'team_realmadrid', type: 'PeriodStart', minute: 46, description: 'Second half kicks off' },
      { id: 'evt_elclasico_6', eventUid: 'evt_fix_elclasico_2026_72_0_Goal_rma9', fixtureId: 'fix_elclasico_2026', teamId: 'team_realmadrid', type: 'Goal', minute: 72, playerName: 'K. Becker', playerId: 'ply_rma_9', description: 'Goal! K. Becker powers a header past the keeper.' },
    ]).onConflictDoNothing();

    console.log('====================================================');
    console.log('[TEST_DB_SETUP] TEST DATABASE SETUP COMPLETE');
    console.log('====================================================');
  } finally {
    sqlite.close();
  }
}

setupTestDatabase().catch((err) => {
  console.error('[TEST_DB_SETUP] Failed:', err);
  process.exit(1);
});
