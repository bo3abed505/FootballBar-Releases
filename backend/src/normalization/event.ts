import { ProviderEvent } from '../providers/football-provider.js';
import type { schema } from '../db/index.js';

export type CanonicalEvent = typeof schema.events.$inferInsert;

export function normalizeEvent(raw: ProviderEvent): CanonicalEvent {
  return {
    id: raw.id.trim(),
    eventUid: raw.eventUid.trim(),
    fixtureId: raw.fixtureId.trim(),
    teamId: raw.teamId.trim(),
    type: raw.type.trim(),
    minute: Math.max(0, raw.minute),
    extraMinute: Math.max(0, raw.extraMinute || 0),
    playerName: raw.playerName?.trim() || null,
    playerId: raw.playerId?.trim() || null,
    assistPlayerName: raw.assistPlayerName?.trim() || null,
    assistPlayerId: raw.assistPlayerId?.trim() || null,
    description: raw.description?.trim() || null,
    timestamp: raw.timestamp ? new Date(raw.timestamp) : new Date(),
  };
}
