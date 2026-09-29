import { ilike, or } from 'drizzle-orm';
import { db, schema } from '../db/index.js';
import {
  V2CatalogTeam,
  V2CatalogCompetition,
  formatV2CatalogTeam,
  formatV2CatalogCompetition,
} from '../api/v2-dto.js';

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
          ilike(schema.teams.name, searchPattern),
          ilike(schema.teams.shortName, searchPattern),
          ilike(schema.teams.code, searchPattern),
          ilike(schema.teams.country, searchPattern)
        )
      )
      .limit(clampedLimit * 2);

    // Deduplicate by normalized name (lowercased trimmed name)
    // Prioritize provider teams (id starting with team_\d+) and merge missing country/logo metadata
    const deduplicatedMap = new Map<string, typeof rows[0]>();

    for (const row of rows) {
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
   * Special characters (%, _) are escaped before applying wildcards.
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
          ilike(schema.competitions.name, searchPattern),
          ilike(schema.competitions.code, searchPattern),
          ilike(schema.competitions.country, searchPattern)
        )
      )
      .limit(clampedLimit);

    return rows.map(formatV2CatalogCompetition);
  }
}
