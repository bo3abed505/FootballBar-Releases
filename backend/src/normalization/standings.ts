import { ProviderStanding, ProviderStandingRow } from '../providers/football-provider.js';
import type { schema } from '../db/index.js';

export type CanonicalStanding = typeof schema.standings.$inferInsert;

export function normalizeStandingRow(
  competitionId: string,
  seasonId: string,
  raw: ProviderStandingRow
): CanonicalStanding {
  return {
    id: `std_${competitionId}_${seasonId}_${raw.teamId}`,
    competitionId: competitionId.trim(),
    seasonId: seasonId.trim(),
    teamId: raw.teamId.trim(),
    position: raw.position,
    played: raw.played,
    won: raw.won,
    drawn: raw.drawn,
    lost: raw.lost,
    goalsFor: raw.goalsFor,
    goalsAgainst: raw.goalsAgainst,
    goalDifference: raw.goalsFor - raw.goalsAgainst,
    points: raw.points,
    form: raw.form?.trim() || null,
    updatedAt: new Date(),
  };
}

export function normalizeStandings(standing: ProviderStanding): CanonicalStanding[] {
  return standing.rows.map((row) =>
    normalizeStandingRow(standing.competitionId, standing.seasonId, row)
  );
}
