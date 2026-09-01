import type { schema } from '../db/index.js';

export type CanonicalLineup = typeof schema.lineups.$inferInsert;
export type CanonicalLineupPlayer = typeof schema.lineupPlayers.$inferInsert;

export function normalizeLineup(raw: {
  id: string;
  fixtureId: string;
  teamId: string;
  formation?: string;
  coachName?: string;
  coachId?: string;
}): CanonicalLineup {
  return {
    id: raw.id.trim(),
    fixtureId: raw.fixtureId.trim(),
    teamId: raw.teamId.trim(),
    formation: raw.formation?.trim() || null,
    coachName: raw.coachName?.trim() || null,
    coachId: raw.coachId?.trim() || null,
  };
}
