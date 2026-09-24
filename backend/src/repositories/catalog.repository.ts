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
          ilike(schema.teams.code, searchPattern)
        )
      )
      .limit(clampedLimit);

    return rows.map(formatV2CatalogTeam);
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
          ilike(schema.competitions.code, searchPattern)
        )
      )
      .limit(clampedLimit);

    return rows.map(formatV2CatalogCompetition);
  }
}
