/**
 * seed-catalog.ts
 *
 * Standalone offline catalog seeding utility for FootballTray.
 * Queries API-Football's GET /leagues endpoint (1 request) or falls back
 * to a comprehensive curated global catalog of top ~150-200 competitions.
 *
 * Persists competitions into schema.competitions, maps canonical IDs into
 * schema.competitionProviderAliases, and records current seasons in schema.competitionSeasons.
 *
 * Execution:
 *   npx tsx src/acquisition/seed-catalog.ts
 */

import * as dotenv from 'dotenv';
dotenv.config();

import { eq, and } from 'drizzle-orm';
import { pool, db, schema } from '../db/index.js';
import { ApiFootballClient } from '../providers/api-football/api-football.client.js';
import { generateCanonicalId } from '../db/canonical-id.js';
import { ensureProviderAlias } from '../db/provider-alias.js';

interface SeedCompetitionItem {
  id: number;
  name: string;
  type: string;
  logo: string;
  country: string;
  code: string | null;
  seasonYear?: number;
}

/**
 * Curated list of high-priority competitions across all continents
 * to ensure top-tier search coverage regardless of API response filtering.
 */
const PRIORITY_COMPETITIONS: Array<{ id: number; name: string; country: string; type: 'league' | 'cup'; code: string }> = [
  // European Top Flights & Cups
  { id: 39, name: 'Premier League', country: 'England', type: 'league', code: 'PL' },
  { id: 40, name: 'Championship', country: 'England', type: 'league', code: 'CHA' },
  { id: 45, name: 'FA Cup', country: 'England', type: 'cup', code: 'FAC' },
  { id: 48, name: 'EFL Cup', country: 'England', type: 'cup', code: 'EFL' },
  { id: 528, name: 'Community Shield', country: 'England', type: 'cup', code: 'CS' },
  { id: 140, name: 'La Liga', country: 'Spain', type: 'league', code: 'PD' },
  { id: 141, name: 'Segunda Division', country: 'Spain', type: 'league', code: 'SD' },
  { id: 143, name: 'Copa del Rey', country: 'Spain', type: 'cup', code: 'CDR' },
  { id: 556, name: 'Supercopa de España', country: 'Spain', type: 'cup', code: 'SCE' },
  { id: 135, name: 'Serie A', country: 'Italy', type: 'league', code: 'SA' },
  { id: 136, name: 'Serie B', country: 'Italy', type: 'league', code: 'SB' },
  { id: 137, name: 'Coppa Italia', country: 'Italy', type: 'cup', code: 'CIT' },
  { id: 547, name: 'Supercoppa Italiana', country: 'Italy', type: 'cup', code: 'SCI' },
  { id: 78, name: 'Bundesliga', country: 'Germany', type: 'league', code: 'BL1' },
  { id: 79, name: '2. Bundesliga', country: 'Germany', type: 'league', code: 'BL2' },
  { id: 81, name: 'DFB-Pokal', country: 'Germany', type: 'cup', code: 'DFB' },
  { id: 529, name: 'DFL-Supercup', country: 'Germany', type: 'cup', code: 'GSC' },
  { id: 61, name: 'Ligue 1', country: 'France', type: 'league', code: 'FL1' },
  { id: 62, name: 'Ligue 2', country: 'France', type: 'league', code: 'FL2' },
  { id: 66, name: 'Coupe de France', country: 'France', type: 'cup', code: 'CDF' },
  { id: 526, name: 'Trophée des Champions', country: 'France', type: 'cup', code: 'TDC' },
  { id: 88, name: 'Eredivisie', country: 'Netherlands', type: 'league', code: 'ERE' },
  { id: 90, name: 'KNVB Beker', country: 'Netherlands', type: 'cup', code: 'KNVB' },
  { id: 94, name: 'Primeira Liga', country: 'Portugal', type: 'league', code: 'PPL' },
  { id: 96, name: 'Taça de Portugal', country: 'Portugal', type: 'cup', code: 'TDP' },
  { id: 144, name: 'Belgian Pro League', country: 'Belgium', type: 'league', code: 'JPL' },
  { id: 147, name: 'Belgian Cup', country: 'Belgium', type: 'cup', code: 'BEC' },
  { id: 203, name: 'Süper Lig', country: 'Turkey', type: 'league', code: 'TSL' },
  { id: 206, name: 'Turkish Cup', country: 'Turkey', type: 'cup', code: 'TRC' },
  { id: 179, name: 'Scottish Premiership', country: 'Scotland', type: 'league', code: 'SP' },
  { id: 181, name: 'Scottish Cup', country: 'Scotland', type: 'cup', code: 'SC' },
  { id: 207, name: 'Swiss Super League', country: 'Switzerland', type: 'league', code: 'SSL' },
  { id: 218, name: 'Austrian Bundesliga', country: 'Austria', type: 'league', code: 'OBL' },
  { id: 119, name: 'Danish Superliga', country: 'Denmark', type: 'league', code: 'DSL' },
  { id: 103, name: 'Eliteserien', country: 'Norway', type: 'league', code: 'ELI' },
  { id: 113, name: 'Allsvenskan', country: 'Sweden', type: 'league', code: 'ALL' },
  { id: 197, name: 'Super League 1', country: 'Greece', type: 'league', code: 'GSL' },
  { id: 106, name: 'Ekstraklasa', country: 'Poland', type: 'league', code: 'EKS' },
  { id: 345, name: 'Czech First League', country: 'Czech Republic', type: 'league', code: 'CFL' },
  { id: 210, name: 'HNL', country: 'Croatia', type: 'league', code: 'HNL' },
  { id: 283, name: 'SuperLiga', country: 'Serbia', type: 'league', code: 'SRB' },
  { id: 332, name: 'Ukrainian Premier League', country: 'Ukraine', type: 'league', code: 'UPL' },

  // Middle East & Africa
  { id: 233, name: 'Egyptian Premier League', country: 'Egypt', type: 'league', code: 'EPL' },
  { id: 234, name: 'Egypt Cup', country: 'Egypt', type: 'cup', code: 'EGC' },
  { id: 546, name: 'Egyptian Super Cup', country: 'Egypt', type: 'cup', code: 'ESC' },
  { id: 307, name: 'Saudi Pro League', country: 'Saudi Arabia', type: 'league', code: 'SPL' },
  { id: 308, name: 'King\'s Cup', country: 'Saudi Arabia', type: 'cup', code: 'KIC' },
  { id: 541, name: 'Saudi Super Cup', country: 'Saudi Arabia', type: 'cup', code: 'SSC' },
  { id: 301, name: 'UAE Pro League', country: 'United Arab Emirates', type: 'league', code: 'UAE' },
  { id: 305, name: 'Qatar Stars League', country: 'Qatar', type: 'league', code: 'QSL' },
  { id: 200, name: 'Botola Pro', country: 'Morocco', type: 'league', code: 'BOT' },
  { id: 202, name: 'Ligue 1', country: 'Tunisia', type: 'league', code: 'TUN' },
  { id: 187, name: 'Ligue 1', country: 'Algeria', type: 'league', code: 'ALG' },
  { id: 288, name: 'Premier Soccer League', country: 'South Africa', type: 'league', code: 'PSL' },

  // Americas
  { id: 253, name: 'Major League Soccer', country: 'USA', type: 'league', code: 'MLS' },
  { id: 257, name: 'US Open Cup', country: 'USA', type: 'cup', code: 'USOC' },
  { id: 846, name: 'Leagues Cup', country: 'World', type: 'cup', code: 'LC' },
  { id: 262, name: 'Liga MX', country: 'Mexico', type: 'league', code: 'LMX' },
  { id: 71, name: 'Serie A', country: 'Brazil', type: 'league', code: 'BSA' },
  { id: 72, name: 'Serie B', country: 'Brazil', type: 'league', code: 'BSB' },
  { id: 73, name: 'Copa do Brasil', country: 'Brazil', type: 'cup', code: 'CDB' },
  { id: 128, name: 'Liga Profesional Argentina', country: 'Argentina', type: 'league', code: 'ARG' },
  { id: 130, name: 'Copa Argentina', country: 'Argentina', type: 'cup', code: 'CARG' },
  { id: 239, name: 'Categoría Primera A', country: 'Colombia', type: 'league', code: 'COL' },
  { id: 265, name: 'Primera División', country: 'Chile', type: 'league', code: 'CHI' },
  { id: 268, name: 'Primera División', country: 'Uruguay', type: 'league', code: 'URU' },

  // Asia & Oceania
  { id: 98, name: 'J1 League', country: 'Japan', type: 'league', code: 'J1' },
  { id: 292, name: 'K League 1', country: 'South Korea', type: 'league', code: 'K1' },
  { id: 188, name: 'A-League', country: 'Australia', type: 'league', code: 'AL' },

  // International & Continental Club Tournaments
  { id: 2, name: 'UEFA Champions League', country: 'World', type: 'cup', code: 'UCL' },
  { id: 3, name: 'UEFA Europa League', country: 'World', type: 'cup', code: 'UEL' },
  { id: 848, name: 'UEFA Europa Conference League', country: 'World', type: 'cup', code: 'UECL' },
  { id: 531, name: 'UEFA Super Cup', country: 'World', type: 'cup', code: 'USC' },
  { id: 12, name: 'CAF Champions League', country: 'World', type: 'cup', code: 'CAFCL' },
  { id: 20, name: 'CAF Confederation Cup', country: 'World', type: 'cup', code: 'CAFCC' },
  { id: 412, name: 'CAF Super Cup', country: 'World', type: 'cup', code: 'CAFSC' },
  { id: 17, name: 'AFC Champions League', country: 'World', type: 'cup', code: 'AFCCL' },
  { id: 849, name: 'AFC Champions League Two', country: 'World', type: 'cup', code: 'ACL2' },
  { id: 16, name: 'CONCACAF Champions Cup', country: 'World', type: 'cup', code: 'CCC' },
  { id: 13, name: 'Copa Libertadores', country: 'World', type: 'cup', code: 'LIB' },
  { id: 11, name: 'Copa Sudamericana', country: 'World', type: 'cup', code: 'SUD' },
  { id: 538, name: 'Recopa Sudamericana', country: 'World', type: 'cup', code: 'RSUD' },
  { id: 15, name: 'FIFA Club World Cup', country: 'World', type: 'cup', code: 'CWC' },

  // International National Team Tournaments
  { id: 1, name: 'FIFA World Cup', country: 'World', type: 'cup', code: 'WC' },
  { id: 4, name: 'UEFA European Championship', country: 'World', type: 'cup', code: 'EURO' },
  { id: 960, name: 'UEFA European Championship - Qualification', country: 'World', type: 'cup', code: 'EQ' },
  { id: 6, name: 'Africa Cup of Nations', country: 'World', type: 'cup', code: 'AFCON' },
  { id: 34, name: 'Africa Cup of Nations - Qualification', country: 'World', type: 'cup', code: 'AFCON_Q' },
  { id: 36, name: 'Africa Cup of Nations - Qualification', country: 'World', type: 'cup', code: 'AFCON_Q' },
  { id: 9, name: 'Copa America', country: 'World', type: 'cup', code: 'CA' },
  { id: 7, name: 'Asian Cup', country: 'World', type: 'cup', code: 'AC' },
  { id: 22, name: 'CONCACAF Gold Cup', country: 'World', type: 'cup', code: 'CGC' },
  { id: 5, name: 'UEFA Nations League', country: 'World', type: 'cup', code: 'UNL' },
  { id: 536, name: 'CONCACAF Nations League', country: 'World', type: 'cup', code: 'CNL' },
  { id: 32, name: 'World Cup - Qualification Europe', country: 'World', type: 'cup', code: 'WCQE' },
  { id: 29, name: 'World Cup - Qualification Africa', country: 'World', type: 'cup', code: 'WCQA' },
  { id: 30, name: 'World Cup - Qualification South America', country: 'World', type: 'cup', code: 'WCQSA' },
  { id: 31, name: 'World Cup - Qualification Asia', country: 'World', type: 'cup', code: 'WCQAS' },
  { id: 33, name: 'World Cup - Qualification CONCACAF', country: 'World', type: 'cup', code: 'WCQC' },
];

function isCleanCompetition(name: string): boolean {
  const lower = name.toLowerCase();
  const noiseKeywords = [
    'u19', 'u20', 'u21', 'u23', 'youth', 'reserve', 'reserves',
    'amateur', 'regional', 'non league', 'promozione', 'oberliga',
    'landesliga', '3. division', '4. division', 'division 3',
    'division 4', 'play-offs 3/4', 'play-offs 4/5', 'tercera',
    'primera c', 'primera d'
  ];
  return !noiseKeywords.some((kw) => lower.includes(kw));
}

async function seedCatalog() {
  console.log('====================================================');
  console.log('FootballTray — Offline Catalog Seeding Engine');
  console.log('====================================================\n');

  const apiKey = process.env.API_FOOTBALL_KEY || '';
  const allowLive = process.env.ALLOW_LIVE_PROVIDER_REQUESTS === 'true';

  const rawLeagues: SeedCompetitionItem[] = [];
  const priorityIds = new Set(PRIORITY_COMPETITIONS.map((p) => p.id));

  // 1. Fetch live from API-Football if credentials and network permitted
  if (apiKey && allowLive) {
    console.log('[API-FOOTBALL] Fetching active global leagues from GET /leagues?current=true...');
    try {
      const client = new ApiFootballClient({ apiKey });
      const res = await client.getLeagues({ current: true }, 'CRITICAL');

      if (res.success && res.data?.response && Array.isArray(res.data.response)) {
        console.log(`[API-FOOTBALL] Successfully retrieved ${res.data.response.length} leagues from provider.`);

        let apiAccepted = 0;
        for (const item of res.data.response) {
          const isPriority = priorityIds.has(item.league.id);
          const isClean = isCleanCompetition(item.league.name);

          // Accept if priority, or if clean tier-1 / international cup
          if (isPriority || (isClean && (item.league.type === 'Cup' || item.country.name === 'World' || apiAccepted < 150))) {
            const currentSeason = item.seasons.find((s) => s.current) || item.seasons[item.seasons.length - 1];
            rawLeagues.push({
              id: item.league.id,
              name: item.league.name,
              type: item.league.type === 'Cup' ? 'cup' : 'league',
              logo: item.league.logo,
              country: item.country.name || 'World',
              code: item.country.code || item.league.name.slice(0, 3).toUpperCase(),
              seasonYear: currentSeason?.year ?? new Date().getFullYear(),
            });
            apiAccepted++;
          }
        }
        console.log(`[API-FOOTBALL] Filtered to ${rawLeagues.length} high-tier competitions.`);
      } else {
        console.warn(`[API-FOOTBALL] API returned non-success or empty data. Error: ${JSON.stringify(res.data?.errors)}`);
      }
    } catch (apiErr: any) {
      console.warn(`[API-FOOTBALL] Network fetch failed: ${apiErr?.message}. Falling back to priority catalog.`);
    }
  } else {
    console.log('[INFO] ALLOW_LIVE_PROVIDER_REQUESTS is not enabled or API key missing. Using priority offline catalog.');
  }

  // 2. Ensure all PRIORITY_COMPETITIONS are present in the list
  const existingIds = new Set(rawLeagues.map((l) => l.id));
  for (const prio of PRIORITY_COMPETITIONS) {
    if (!existingIds.has(prio.id)) {
      rawLeagues.push({
        id: prio.id,
        name: prio.name,
        type: prio.type,
        logo: `https://media.api-sports.io/football/leagues/${prio.id}.png`,
        country: prio.country,
        code: prio.code,
        seasonYear: new Date().getFullYear(),
      });
      existingIds.add(prio.id);
    }
  }

  console.log(`[CATALOG] Total candidate competitions to seed/update: ${rawLeagues.length}\n`);

  let insertedCount = 0;
  let updatedCount = 0;
  let aliasCount = 0;
  let seasonCount = 0;

  // 3. Batch Process into PostgreSQL
  const now = new Date();
  for (const comp of rawLeagues) {
    const compId = `comp_${comp.id}`;

    await db.transaction(async (tx) => {
      // 1. Check if an alias already exists for this providerId
      const existingAlias = await tx.query.competitionProviderAliases.findFirst({
        where: and(
          eq(schema.competitionProviderAliases.provider, 'api_football'),
          eq(schema.competitionProviderAliases.providerId, String(comp.id))
        ),
      });

      // 2. Check if competition exists by primary ID (compId) or by canonicalId from existingAlias
      let existing = await tx.query.competitions.findFirst({
        where: eq(schema.competitions.id, compId),
      });

      if (!existing && existingAlias) {
        existing = await tx.query.competitions.findFirst({
          where: eq(schema.competitions.canonicalId, existingAlias.canonicalCompetitionId),
        });
      }

      let canonicalId: string;

      if (existing) {
        canonicalId = existing.canonicalId;
        await tx.update(schema.competitions).set({
          name: comp.name,
          code: comp.code || existing.code,
          type: comp.type,
          country: comp.country,
          logoUrl: comp.logo,
          updatedAt: now,
        }).where(eq(schema.competitions.id, existing.id));
        updatedCount++;
      } else {
        canonicalId = existingAlias ? existingAlias.canonicalCompetitionId : generateCanonicalId('comp');
        await tx.insert(schema.competitions).values({
          id: compId,
          canonicalId,
          name: comp.name,
          code: comp.code || comp.name.slice(0, 3).toUpperCase(),
          type: comp.type,
          country: comp.country,
          logoUrl: comp.logo,
          createdAt: now,
          updatedAt: now,
        }).onConflictDoNothing();
        insertedCount++;
      }

      // Map alias in competition_provider_aliases
      const targetCanonicalId = canonicalId;
      const aliasCreated = await ensureProviderAlias(tx, 'competition', targetCanonicalId, 'api_football', String(comp.id));
      if (aliasCreated) aliasCount++;

      // Upsert current season
      const targetCompId = existing ? existing.id : compId;
      const year = comp.seasonYear || now.getFullYear();
      const seasonId = `season_${comp.id}_${year}`;
      const seasonInserted = await tx.insert(schema.competitionSeasons).values({
        id: seasonId,
        competitionId: targetCompId,
        year,
        displayYear: `${year}/${year + 1}`,
        startDate: new Date(`${year}-01-01T00:00:00Z`),
        endDate: new Date(`${year}-12-31T23:59:59Z`),
        isCurrent: true,
        createdAt: now,
      }).onConflictDoNothing().returning();

      if (seasonInserted.length > 0) seasonCount++;
    });
  }

  // 4. Query total database count
  const allComps = await db.select({ count: schema.competitions.id }).from(schema.competitions);

  console.log('----------------------------------------------------');
  console.log('Seed Results:');
  console.log(`  - Competitions Inserted:  ${insertedCount}`);
  console.log(`  - Competitions Updated:   ${updatedCount}`);
  console.log(`  - Aliases Linked:         ${aliasCount}`);
  console.log(`  - Seasons Recorded:       ${seasonCount}`);
  console.log(`  - Total Competitions in Database: ${allComps.length}`);
  console.log('----------------------------------------------------\n');

  console.log('[SUCCESS] Offline catalog seeding complete.');
  await pool.end();
}

seedCatalog().catch(async (err) => {
  console.error('[FATAL] Seeding failed:', err);
  await pool.end().catch(() => {});
  process.exit(1);
});
