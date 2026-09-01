import { ProviderCompetition, ProviderSeason, ProviderTeam, ProviderFixture } from '../providers/football-provider.js';
import type { schema } from '../db/index.js';

export type CanonicalCompetition = typeof schema.competitions.$inferInsert;
export type CanonicalSeason = typeof schema.competitionSeasons.$inferInsert;
export type CanonicalTeam = typeof schema.teams.$inferInsert;
export type CanonicalFixture = typeof schema.fixtures.$inferInsert;

export function normalizeCompetition(raw: ProviderCompetition): CanonicalCompetition {
  return {
    id: raw.id.trim(),
    name: raw.name.trim(),
    code: raw.code?.trim() || null,
    type: raw.type?.trim() || 'league',
    country: raw.country?.trim() || null,
    logoUrl: raw.logoUrl?.trim() || null,
  };
}

export function normalizeSeason(raw: ProviderSeason): CanonicalSeason {
  return {
    id: raw.id.trim(),
    competitionId: raw.competitionId.trim(),
    year: raw.year,
    displayYear: raw.displayYear.trim(),
    startDate: new Date(raw.startDate),
    endDate: new Date(raw.endDate),
    isCurrent: raw.isCurrent ?? true,
  };
}

export function normalizeTeam(raw: ProviderTeam): CanonicalTeam {
  return {
    id: raw.id.trim(),
    name: raw.name.trim(),
    shortName: raw.shortName.trim(),
    code: raw.code?.trim() || null,
    country: raw.country?.trim() || null,
    logoUrl: raw.logoUrl?.trim() || null,
    venueName: raw.venueName?.trim() || null,
  };
}

export function normalizeFixture(raw: ProviderFixture): CanonicalFixture {
  return {
    id: raw.id.trim(),
    competitionId: raw.competitionId.trim(),
    seasonId: raw.seasonId.trim(),
    homeTeamId: raw.homeTeamId.trim(),
    awayTeamId: raw.awayTeamId.trim(),
    round: raw.round?.trim() || null,
    kickoffTime: new Date(raw.kickoffTime),
    state: raw.state.trim().toUpperCase(),
    elapsedMinute: Math.max(0, raw.elapsedMinute),
    extraMinute: Math.max(0, raw.extraMinute || 0),
    homeScore: Math.max(0, raw.score.home),
    awayScore: Math.max(0, raw.score.away),
    homeScoreHt: raw.score.homeHt ?? null,
    awayScoreHt: raw.score.awayHt ?? null,
    homeScoreEt: raw.score.homeEt ?? null,
    awayScoreEt: raw.score.awayEt ?? null,
    homeScorePen: raw.score.homePen ?? null,
    awayScorePen: raw.score.awayPen ?? null,
    venue: raw.venue?.trim() || null,
    referee: raw.referee?.trim() || null,
    lastPolledAt: new Date(),
  };
}
