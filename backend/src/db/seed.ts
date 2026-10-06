import { db, schema, pool, sqlite } from './index.js';
import { generateCanonicalId } from './canonical-id.js';

export async function seedDatabase() {
  const allowDevSeeding = process.env.ALLOW_DEV_SEEDING === 'true';
  const isProduction = process.env.NODE_ENV === 'production';

  if (isProduction || !allowDevSeeding) {
    console.log('[SEED] Seeding skipped: DEV catalog seeding is strictly disabled in production and defaults to disabled unless ALLOW_DEV_SEEDING=true.');
    return;
  }

  console.log('[SEED] Purging ghost mock catalog entities...');
  sqlite.exec(`
    DELETE FROM competitions WHERE name LIKE '%(Dev Mock)%' OR id IN ('comp_laliga', 'comp_egypt_pl', 'comp_international');
    DELETE FROM competition_seasons WHERE id IN ('season_laliga_2025', 'season_egypt_2025', 'season_international_2025');
    DELETE FROM teams WHERE name LIKE '%(Dev Mock)%' OR id IN ('team_barcelona', 'team_realmadrid', 'team_alahly', 'team_egypt', 'team_spain');
  `);

  console.log('[SEED] Starting catalog seed...');

  // 1. Competitions
  console.log('[SEED] Seeding competitions...');
  await db.insert(schema.competitions).values([
    {
      id: 'comp_laliga',
      canonicalId: generateCanonicalId('comp'),
      name: 'La Liga',
      code: 'ESP_L1',
      type: 'league',
      country: 'Spain',
      logoUrl: 'https://media.api-sports.io/football/leagues/140.png',
    },
    {
      id: 'comp_egypt_pl',
      canonicalId: generateCanonicalId('comp'),
      name: 'Egyptian Premier League',
      code: 'EGY_PL',
      type: 'league',
      country: 'Egypt',
      logoUrl: 'https://media.api-sports.io/football/leagues/233.png',
    },
    {
      id: 'comp_international',
      canonicalId: generateCanonicalId('comp'),
      name: 'International Friendly',
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

  console.log('[SEED] Database seeded successfully with pure catalog data (competitions, seasons, teams)!');
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
