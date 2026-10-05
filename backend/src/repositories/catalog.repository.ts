import { like, or } from 'drizzle-orm';
import { db, schema } from '../db/index.js';
import {
  V2CatalogTeam,
  V2CatalogCompetition,
  formatV2CatalogTeam,
  formatV2CatalogCompetition,
} from '../api/v2-dto.js';
import {
  PRIORITY_COMPETITIONS,
  CANONICAL_COMPETITION_MAP,
} from '../acquisition/priority-competitions.js';

/**
 * Escapes SQL LIKE / ILIKE wildcard characters (%, _) and backslash (\)
 * to prevent wildcard injection and entire-table dumps.
 */
export function escapeLikeWildcards(input: string): string {
  return input.replace(/([\\%_])/g, '\\$1');
}

export class CatalogRepository {
  /**
   * Performs case-insensitive partial search for teams across name, shortName, and code.
   * Special characters (%, _) are escaped before applying wildcards.
   */
  async searchTeams(query: string, limit: number = 20): Promise<V2CatalogTeam[]> {
    const trimmed = query.trim();
    if (!trimmed) {
      return [];
    }

    const sanitized = escapeLikeWildcards(trimmed);
    const searchPattern = `%${sanitized}%`;
    const clampedLimit = Math.min(Math.max(limit, 1), 50);

    const rows = await db
      .select({
        canonicalId: schema.teams.canonicalId,
        id: schema.teams.id,
        name: schema.teams.name,
        shortName: schema.teams.shortName,
        code: schema.teams.code,
        country: schema.teams.country,
        logoUrl: schema.teams.logoUrl,
      })
      .from(schema.teams)
      .where(
        or(
          like(schema.teams.name, searchPattern),
          like(schema.teams.shortName, searchPattern),
          like(schema.teams.code, searchPattern),
          like(schema.teams.country, searchPattern)
        )
      )
      .limit(clampedLimit * 2);

    // Deduplicate by normalized name (lowercased trimmed name)
    // Prioritize provider teams (id starting with team_\d+) and merge missing country/logo metadata
    const deduplicatedMap = new Map<string, typeof rows[0]>();

    for (const row of rows) {
      if (row.name && row.name.toLowerCase().includes('(dev mock)')) {
        continue;
      }
      const normKey = row.name.trim().toLowerCase();
      const existing = deduplicatedMap.get(normKey);

      if (!existing) {
        deduplicatedMap.set(normKey, { ...row });
        continue;
      }

      const isCurrentProvider = /^team_\d+$/.test(row.id);
      const isExistingProvider = /^team_\d+$/.test(existing.id);

      if (isCurrentProvider && !isExistingProvider) {
        deduplicatedMap.set(normKey, {
          ...row,
          country: row.country || existing.country,
          logoUrl: row.logoUrl || existing.logoUrl,
          code: row.code || existing.code,
        });
      } else if (!isCurrentProvider && isExistingProvider) {
        if (!existing.country && row.country) existing.country = row.country;
        if (!existing.logoUrl && row.logoUrl) existing.logoUrl = row.logoUrl;
        if (!existing.code && row.code) existing.code = row.code;
      } else {
        if (!existing.country && row.country) {
          existing.country = row.country;
        }
        if (!existing.logoUrl && row.logoUrl) {
          existing.logoUrl = row.logoUrl;
        }
      }
    }

    const deduplicatedRows = Array.from(deduplicatedMap.values()).slice(0, clampedLimit);
    return deduplicatedRows.map(formatV2CatalogTeam);
  }

  /**
   * Performs case-insensitive partial search for competitions across name and code.
   * Merges database competitions with curated priority global competitions to guarantee
   * full coverage for world leagues (e.g. Premier League, Champions League, La Liga, Serie A).
   */
  async searchCompetitions(query: string, limit: number = 20): Promise<V2CatalogCompetition[]> {
    const trimmed = query.trim();
    if (!trimmed) {
      return [];
    }

    const sanitized = escapeLikeWildcards(trimmed);
    const searchPattern = `%${sanitized}%`;
    const clampedLimit = Math.min(Math.max(limit, 1), 50);

    const rows = await db
      .select({
        canonicalId: schema.competitions.canonicalId,
        id: schema.competitions.id,
        name: schema.competitions.name,
        code: schema.competitions.code,
        country: schema.competitions.country,
        logoUrl: schema.competitions.logoUrl,
      })
      .from(schema.competitions)
      .where(
        or(
          like(schema.competitions.name, searchPattern),
          like(schema.competitions.code, searchPattern),
          like(schema.competitions.country, searchPattern)
        )
      )
      .limit(clampedLimit);

    const resultsMap = new Map<string, V2CatalogCompetition>();

    // 1. Add database matches
    for (const row of rows) {
      if (row.name && row.name.toLowerCase().includes('(dev mock)')) {
        continue;
      }
      const formatted = formatV2CatalogCompetition(row);
      const normKey = formatted.name.trim().toLowerCase();
      resultsMap.set(normKey, formatted);
    }

    // 2. Add matching priority competitions
    const lowerQuery = trimmed.toLowerCase();
    const matchingPriority = PRIORITY_COMPETITIONS.filter((prio) => {
      const name = prio.name.toLowerCase();
      const code = (prio.code || '').toLowerCase();
      const country = (prio.country || '').toLowerCase();
      return name.includes(lowerQuery) || code.includes(lowerQuery) || country.includes(lowerQuery);
    });

    for (const prio of matchingPriority) {
      const normKey = prio.name.trim().toLowerCase();
      const existing = resultsMap.get(normKey);

      const canonicalId = CANONICAL_COMPETITION_MAP[prio.id] || `ft_comp_${prio.id}`;
      const legacyId = `comp_${prio.id}`;
      const logoUrl = `https://media.api-sports.io/football/leagues/${prio.id}.png`;

      if (!existing) {
        resultsMap.set(normKey, {
          id: canonicalId,
          legacyId,
          name: prio.name,
          code: prio.code || null,
          country: prio.country || null,
          logo: logoUrl,
          logoUrl,
        });
      } else {
        // Enhance existing if missing canonical or logo/code metadata
        if (CANONICAL_COMPETITION_MAP[prio.id] && (!existing.id || existing.id.startsWith('comp_'))) {
          existing.id = CANONICAL_COMPETITION_MAP[prio.id];
        }
        if (!existing.legacyId) existing.legacyId = legacyId;
        if (!existing.code && prio.code) existing.code = prio.code;
        if (!existing.country && prio.country) existing.country = prio.country;
        if (!existing.logoUrl) existing.logoUrl = logoUrl;
        if (!existing.logo) existing.logo = logoUrl;
      }
    }

    return Array.from(resultsMap.values()).slice(0, clampedLimit);
  }
}

