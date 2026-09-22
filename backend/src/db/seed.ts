import { db, schema, pool } from './index.js';
import { generateCanonicalId } from './canonical-id.js';

export async function seedDatabase() {
  console.log('[SEED] Starting development seed...');

  // 1. Competitions
  console.log('[SEED] Seeding competitions...');
  await db.insert(schema.competitions).values([
    {
      id: 'comp_laliga',
      canonicalId: generateCanonicalId('comp'),
      name: 'La Liga (Dev Mock)',
      code: 'ESP_L1',
      type: 'league',
      country: 'Spain',
      logoUrl: 'https://media.api-sports.io/football/leagues/140.png',
    },
    {
      id: 'comp_egypt_pl',
      canonicalId: generateCanonicalId('comp'),
      name: 'Egyptian Premier League (Dev Mock)',
      code: 'EGY_PL',
      type: 'league',
      country: 'Egypt',
      logoUrl: 'https://media.api-sports.io/football/leagues/233.png',
    },
    {
      id: 'comp_international',
      canonicalId: generateCanonicalId('comp'),
      name: 'International Friendly (Dev Mock)',
      code: 'INT_FRN',
      type: 'cup',
      country: 'World',
      logoUrl: 'https://media.api-sports.io/football/leagues/10.png',
    },
  ]).onConflictDoNothing();

  // 2. Competition Seasons
  console.log('[SEED] Seeding competition seasons...');
  await db.insert(schema.competitionSeasons).values([
    {
      id: 'season_laliga_2025',
      competitionId: 'comp_laliga',
      year: 2025,
      displayYear: '2025/2026',
      startDate: new Date('2025-08-15T00:00:00Z'),
      endDate: new Date('2026-05-24T00:00:00Z'),
      isCurrent: true,
    },
    {
      id: 'season_egypt_2025',
      competitionId: 'comp_egypt_pl',
      year: 2025,
      displayYear: '2025/2026',
      startDate: new Date('2025-09-01T00:00:00Z'),
      endDate: new Date('2026-06-30T00:00:00Z'),
      isCurrent: true,
    },
    {
      id: 'season_international_2025',
      competitionId: 'comp_international',
      year: 2025,
      displayYear: '2025/2026',
      startDate: new Date('2025-01-01T00:00:00Z'),
      endDate: new Date('2025-12-31T00:00:00Z'),
      isCurrent: true,
    },
  ]).onConflictDoNothing();

  // 3. Teams (Barcelona, Real Madrid, Al Ahly, Egypt, Spain)
  console.log('[SEED] Seeding teams...');
  await db.insert(schema.teams).values([
    {
      id: 'team_barcelona',
      canonicalId: generateCanonicalId('team'),
      name: 'FC Barcelona',
      shortName: 'BAR',
      code: 'FCB',
      country: 'Spain',
      logoUrl: 'https://media.api-sports.io/football/teams/529.png',
      founded: 1899,
      venueName: 'Spotify Camp Nou',
    },
    {
      id: 'team_realmadrid',
      canonicalId: generateCanonicalId('team'),
      name: 'Real Madrid',
      shortName: 'RMA',
      code: 'RMA',
      country: 'Spain',
      logoUrl: 'https://media.api-sports.io/football/teams/541.png',
      founded: 1902,
      venueName: 'Santiago Bernabéu',
    },
    {
      id: 'team_alahly',
      canonicalId: generateCanonicalId('team'),
      name: 'Al Ahly SC',
      shortName: 'AHL',
      code: 'AHL',
      country: 'Egypt',
      logoUrl: 'https://media.api-sports.io/football/teams/1029.png',
      founded: 1907,
      venueName: 'Cairo International Stadium',
    },
    {
      id: 'team_egypt',
      canonicalId: generateCanonicalId('team'),
      name: 'Egypt',
      shortName: 'EGY',
      code: 'EGY',
      country: 'Egypt',
      logoUrl: 'https://media.api-sports.io/football/teams/32.png',
      founded: 1921,
      venueName: 'Cairo International Stadium',
    },
    {
      id: 'team_spain',
      canonicalId: generateCanonicalId('team'),
      name: 'Spain',
      shortName: 'ESP',
      code: 'ESP',
      country: 'Spain',
      logoUrl: 'https://media.api-sports.io/football/teams/9.png',
      founded: 1913,
      venueName: 'Estadio Metropolitano',
    },
  ]).onConflictDoNothing();

  // 4. Fixtures (El Clásico & Spain vs Egypt)
  console.log('[SEED] Seeding fixtures...');
  await db.insert(schema.fixtures).values([
    {
      id: 'fix_elclasico_2026',
      canonicalId: generateCanonicalId('fix'),
      competitionId: 'comp_laliga',
      seasonId: 'season_laliga_2025',
      homeTeamId: 'team_barcelona',
      awayTeamId: 'team_realmadrid',
      round: '[DEV MOCK] Regular Season - Round 26',
      kickoffTime: new Date(Date.now() - 45 * 60 * 1000), // Kickoff 45 mins ago
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
      canonicalId: generateCanonicalId('fix'),
      competitionId: 'comp_international',
      seasonId: 'season_international_2025',
      homeTeamId: 'team_spain',
      awayTeamId: 'team_egypt',
      round: '[DEV MOCK] International Friendly',
      kickoffTime: new Date(Date.now() + 4 * 60 * 60 * 1000), // Upcoming in 4 hours
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

  // 5. Events for El Clásico
  console.log('[SEED] Seeding match events...');
  await db.insert(schema.events).values([
    {
      id: 'evt_elclasico_1',
      eventUid: 'evt_fix_elclasico_2026_1_0_PeriodStart_init',
      fixtureId: 'fix_elclasico_2026',
      teamId: 'team_barcelona',
      type: 'PeriodStart',
      minute: 1,
      extraMinute: 0,
      description: '[DEV MOCK] First half kicks off',
    },
    {
      id: 'evt_elclasico_2',
      eventUid: 'evt_fix_elclasico_2026_17_0_Goal_bar10',
      fixtureId: 'fix_elclasico_2026',
      teamId: 'team_barcelona',
      type: 'Goal',
      minute: 17,
      extraMinute: 0,
      playerName: 'L. Martinez (Dev Player)',
      playerId: 'ply_bar_10',
      assistPlayerName: 'P. Gonzalez (Dev Player)',
      assistPlayerId: 'ply_bar_8',
      description: '[DEV MOCK] Goal! L. Martinez slots it into the bottom corner.',
    },
    {
      id: 'evt_elclasico_3',
      eventUid: 'evt_fix_elclasico_2026_35_0_YellowCard_rma5',
      fixtureId: 'fix_elclasico_2026',
      teamId: 'team_realmadrid',
      type: 'YellowCard',
      minute: 35,
      extraMinute: 0,
      playerName: 'V. Silva (Dev Player)',
      playerId: 'ply_rma_5',
      description: '[DEV MOCK] Yellow card for tactical foul',
    },
    {
      id: 'evt_elclasico_4',
      eventUid: 'evt_fix_elclasico_2026_45_0_PeriodEnd_ht',
      fixtureId: 'fix_elclasico_2026',
      teamId: 'team_barcelona',
      type: 'PeriodEnd',
      minute: 45,
      extraMinute: 0,
      description: '[DEV MOCK] Half-time whistle',
    },
    {
      id: 'evt_elclasico_5',
      eventUid: 'evt_fix_elclasico_2026_46_0_PeriodStart_2h',
      fixtureId: 'fix_elclasico_2026',
      teamId: 'team_realmadrid',
      type: 'PeriodStart',
      minute: 46,
      extraMinute: 0,
      description: '[DEV MOCK] Second half kicks off',
    },
    {
      id: 'evt_elclasico_6',
      eventUid: 'evt_fix_elclasico_2026_72_0_Goal_rma9',
      fixtureId: 'fix_elclasico_2026',
      teamId: 'team_realmadrid',
      type: 'Goal',
      minute: 72,
      extraMinute: 0,
      playerName: 'K. Becker (Dev Player)',
      playerId: 'ply_rma_9',
      assistPlayerName: 'R. Hernandez (Dev Player)',
      assistPlayerId: 'ply_rma_7',
      description: '[DEV MOCK] Goal! K. Becker powers a header past the keeper.',
    },
  ]).onConflictDoNothing();

  console.log('[SEED] Database seeded successfully with development mock fixtures!');
}

// Auto-run if executed directly
if (process.argv[1]?.endsWith('seed.ts') || process.argv[1]?.endsWith('seed.js')) {
  seedDatabase()
    .catch((err) => {
      console.error('[SEED] Seed failed:', err);
    })
    .finally(async () => {
      await pool.end();
    });
}
