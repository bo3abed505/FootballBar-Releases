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

import { PRIORITY_COMPETITIONS, FOTMOB_COMPETITION_MAP } from './priority-competitions.js';

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

      const fotmobCompId = FOTMOB_COMPETITION_MAP[comp.id];
      if (fotmobCompId) {
        const fotmobAliasCreated = await ensureProviderAlias(tx, 'competition', targetCanonicalId, 'fotmob', String(fotmobCompId));
        if (fotmobAliasCreated) aliasCount++;
      }

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
