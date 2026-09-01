import pg from 'pg';
import * as dotenv from 'dotenv';
import * as path from 'path';
import { drizzle } from 'drizzle-orm/node-postgres';
import { migrate } from 'drizzle-orm/node-postgres/migrator';
import * as schema from './schema.js';

dotenv.config();

const { Client, Pool } = pg;

async function setupTestDatabase() {
  console.log('====================================================');
  console.log('FOOTBALLTRAY TEST DATABASE SETUP');
  console.log('====================================================');

  const testUrl = process.env.DATABASE_URL_TEST;
  if (!testUrl) {
    throw new Error('[TEST_DB_SETUP] DATABASE_URL_TEST is not configured in .env');
  }

  // Parse connection details to connect to maintenance postgres db
  const url = new URL(testUrl);
  const targetDbName = url.pathname.replace(/^\//, '');
  const username = url.username;
  const password = decodeURIComponent(url.password);
  const host = url.hostname;
  const port = parseInt(url.port || '5432', 10);

  console.log(`[TEST_DB_SETUP] Target Test Database: '${targetDbName}' on ${host}:${port}`);

  // 1. Connect to postgres maintenance db to check / create test DB
  const maintenanceClient = new Client({
    host,
    port,
    user: username,
    password,
    database: 'postgres',
  });

  try {
    await maintenanceClient.connect();
    console.log('[TEST_DB_SETUP] PostgreSQL server is reachable.');

    const checkDb = await maintenanceClient.query(
      `SELECT 1 FROM pg_database WHERE datname = $1`,
      [targetDbName]
    );

    if (checkDb.rows.length === 0) {
      console.log(`[TEST_DB_SETUP] Database '${targetDbName}' not found. Creating database...`);
      await maintenanceClient.query(`CREATE DATABASE "${targetDbName}"`);
      console.log(`[TEST_DB_SETUP] Database '${targetDbName}' created successfully.`);
    } else {
      console.log(`[TEST_DB_SETUP] Database '${targetDbName}' exists.`);
    }
  } finally {
    await maintenanceClient.end();
  }

  // 2. Connect to the test database and run migrations
  console.log('[TEST_DB_SETUP] Applying Drizzle migrations to test database...');
  const testPool = new Pool({
    connectionString: testUrl,
    max: 5,
  });

  try {
    const testDb = drizzle(testPool, { schema });
    const migrationsFolder = path.resolve('drizzle');
    await migrate(testDb, { migrationsFolder });
    console.log('[TEST_DB_SETUP] Migrations successfully applied from:', migrationsFolder);

    // 3. Verify all tables in test DB
    const tablesRes = await testPool.query(`
      SELECT table_name
      FROM information_schema.tables
      WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
      ORDER BY table_name;
    `);
    console.log(`[TEST_DB_SETUP] Verified ${tablesRes.rows.length} tables in '${targetDbName}'.`);

    // 4. Seed baseline test data into test DB
    console.log('[TEST_DB_SETUP] Seeding baseline test data into test database...');
    // Competitions
    await testDb.insert(schema.competitions).values([
      { id: 'comp_laliga', name: 'La Liga (Dev Mock)', code: 'ESP_L1', type: 'league', country: 'Spain', logoUrl: 'https://media.api-sports.io/football/leagues/140.png' },
      { id: 'comp_egypt_pl', name: 'Egyptian Premier League (Dev Mock)', code: 'EGY_PL', type: 'league', country: 'Egypt', logoUrl: 'https://media.api-sports.io/football/leagues/233.png' },
      { id: 'comp_international', name: 'International Friendly (Dev Mock)', code: 'INT_FRN', type: 'cup', country: 'World', logoUrl: 'https://media.api-sports.io/football/leagues/10.png' },
    ]).onConflictDoNothing();

    // Seasons
    await testDb.insert(schema.competitionSeasons).values([
      { id: 'season_laliga_2025', competitionId: 'comp_laliga', year: 2025, displayYear: '2025/2026', startDate: new Date('2025-08-15T00:00:00Z'), endDate: new Date('2026-05-24T00:00:00Z'), isCurrent: true },
      { id: 'season_egypt_2025', competitionId: 'comp_egypt_pl', year: 2025, displayYear: '2025/2026', startDate: new Date('2025-09-01T00:00:00Z'), endDate: new Date('2026-06-30T00:00:00Z'), isCurrent: true },
      { id: 'season_international_2025', competitionId: 'comp_international', year: 2025, displayYear: '2025/2026', startDate: new Date('2025-01-01T00:00:00Z'), endDate: new Date('2025-12-31T00:00:00Z'), isCurrent: true },
    ]).onConflictDoNothing();

    // Teams
    await testDb.insert(schema.teams).values([
      { id: 'team_barcelona', name: 'FC Barcelona', shortName: 'BAR', code: 'FCB', country: 'Spain', logoUrl: 'https://media.api-sports.io/football/teams/529.png' },
      { id: 'team_realmadrid', name: 'Real Madrid', shortName: 'RMA', code: 'RMA', country: 'Spain', logoUrl: 'https://media.api-sports.io/football/teams/541.png' },
      { id: 'team_alahly', name: 'Al Ahly SC', shortName: 'AHL', code: 'AHL', country: 'Egypt', logoUrl: 'https://media.api-sports.io/football/teams/1029.png' },
      { id: 'team_egypt', name: 'Egypt', shortName: 'EGY', code: 'EGY', country: 'Egypt', logoUrl: 'https://media.api-sports.io/football/teams/32.png' },
      { id: 'team_spain', name: 'Spain', shortName: 'ESP', code: 'ESP', country: 'Spain', logoUrl: 'https://media.api-sports.io/football/teams/9.png' },
    ]).onConflictDoNothing();

    // Fixtures
    await testDb.insert(schema.fixtures).values([
      {
        id: 'fix_elclasico_2026',
        competitionId: 'comp_laliga',
        seasonId: 'season_laliga_2025',
        homeTeamId: 'team_barcelona',
        awayTeamId: 'team_realmadrid',
        round: '[DEV MOCK] Regular Season - Round 26',
        kickoffTime: new Date(Date.now() - 45 * 60 * 1000),
        state: 'LIVE',
        elapsedMinute: 72,
        extraMinute: 0,
        homeScore: 1,
        awayScore: 1,
        homeScoreHt: 1,
        awayScoreHt: 0,
        venue: 'Spotify Camp Nou (Mock)',
        referee: 'J. Manzano',
        lastPolledAt: new Date(),
      },
      {
        id: 'fix_spain_egypt_2026',
        competitionId: 'comp_international',
        seasonId: 'season_international_2025',
        homeTeamId: 'team_spain',
        awayTeamId: 'team_egypt',
        round: '[DEV MOCK] International Friendly',
        kickoffTime: new Date(Date.now() + 4 * 60 * 60 * 1000),
        state: 'PREMATCH',
        elapsedMinute: 0,
        extraMinute: 0,
        homeScore: 0,
        awayScore: 0,
        venue: 'Estadio Metropolitano (Mock)',
        referee: 'M. Oliver',
        lastPolledAt: new Date(),
      },
    ]).onConflictDoNothing();

    // Events
    await testDb.insert(schema.events).values([
      { id: 'evt_elclasico_1', eventUid: 'evt_fix_elclasico_2026_1_0_PeriodStart_init', fixtureId: 'fix_elclasico_2026', teamId: 'team_barcelona', type: 'PeriodStart', minute: 1, description: '[DEV MOCK] First half kicks off' },
      { id: 'evt_elclasico_2', eventUid: 'evt_fix_elclasico_2026_17_0_Goal_bar10', fixtureId: 'fix_elclasico_2026', teamId: 'team_barcelona', type: 'Goal', minute: 17, playerName: 'L. Martinez (Dev Player)', playerId: 'ply_bar_10', description: '[DEV MOCK] Goal! L. Martinez slots it into the bottom corner.' },
      { id: 'evt_elclasico_3', eventUid: 'evt_fix_elclasico_2026_35_0_YellowCard_rma5', fixtureId: 'fix_elclasico_2026', teamId: 'team_realmadrid', type: 'YellowCard', minute: 35, playerName: 'V. Silva (Dev Player)', playerId: 'ply_rma_5', description: '[DEV MOCK] Yellow card for tactical foul' },
      { id: 'evt_elclasico_4', eventUid: 'evt_fix_elclasico_2026_45_0_PeriodEnd_ht', fixtureId: 'fix_elclasico_2026', teamId: 'team_barcelona', type: 'PeriodEnd', minute: 45, description: '[DEV MOCK] Half-time whistle' },
      { id: 'evt_elclasico_5', eventUid: 'evt_fix_elclasico_2026_46_0_PeriodStart_2h', fixtureId: 'fix_elclasico_2026', teamId: 'team_realmadrid', type: 'PeriodStart', minute: 46, description: '[DEV MOCK] Second half kicks off' },
      { id: 'evt_elclasico_6', eventUid: 'evt_fix_elclasico_2026_72_0_Goal_rma9', fixtureId: 'fix_elclasico_2026', teamId: 'team_realmadrid', type: 'Goal', minute: 72, playerName: 'K. Becker (Dev Player)', playerId: 'ply_rma_9', description: '[DEV MOCK] Goal! K. Becker powers a header past the keeper.' },
    ]).onConflictDoNothing();

    console.log('====================================================');
    console.log('[TEST_DB_SETUP] TEST DATABASE SETUP COMPLETE');
    console.log('====================================================');
  } finally {
    await testPool.end();
  }
}

setupTestDatabase().catch((err) => {
  console.error('[TEST_DB_SETUP] Failed:', err);
  process.exit(1);
});
