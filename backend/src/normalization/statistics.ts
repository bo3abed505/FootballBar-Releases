import type { schema } from '../db/index.js';

export type CanonicalFixtureStatistics = typeof schema.fixtureStatistics.$inferInsert;

export function normalizeFixtureStatistics(raw: {
  id: string;
  fixtureId: string;
  teamId: string;
  shotsOnGoal?: number;
  shotsOffGoal?: number;
  totalShots?: number;
  possessionPercent?: number;
  fouls?: number;
  cornerKicks?: number;
  offsides?: number;
  yellowCards?: number;
  redCards?: number;
  goalkeeperSaves?: number;
  totalPasses?: number;
  passesAccurate?: number;
}): CanonicalFixtureStatistics {
  return {
    id: raw.id.trim(),
    fixtureId: raw.fixtureId.trim(),
    teamId: raw.teamId.trim(),
    shotsOnGoal: raw.shotsOnGoal ?? null,
    shotsOffGoal: raw.shotsOffGoal ?? null,
    totalShots: raw.totalShots ?? null,
    possessionPercent: raw.possessionPercent ?? null,
    fouls: raw.fouls ?? null,
    cornerKicks: raw.cornerKicks ?? null,
    offsides: raw.offsides ?? null,
    yellowCards: raw.yellowCards ?? null,
    redCards: raw.redCards ?? null,
    goalkeeperSaves: raw.goalkeeperSaves ?? null,
    totalPasses: raw.totalPasses ?? null,
    passesAccurate: raw.passesAccurate ?? null,
    updatedAt: new Date(),
  };
}
