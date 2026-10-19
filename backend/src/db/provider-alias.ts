import { eq, and, or } from 'drizzle-orm';
import { db, schema } from './index.js';

export type AliasEntityType = 'competition' | 'team' | 'fixture';

export type DbExecutor = typeof db | Parameters<Parameters<typeof db.transaction>[0]>[0];

export const API_FOOTBALL_FIXTURE_REGEX = /^fix_(\d+)$/;
export const API_FOOTBALL_COMPETITION_REGEX = /^comp_(\d+)$/;
export const API_FOOTBALL_TEAM_REGEX = /^team_(\d+)$/;

/**
 * Normalizes an API-Football entity ID to its pure provider ID (numeric string),
 * extracting the number from prefix formats like 'fix_123', 'comp_456', or 'team_789'.
 * If the input is already pure numeric or custom, it is returned without modification.
 */
export function extractProviderId(
  id: string | number,
  type?: 'comp' | 'team' | 'fix'
): string {
  const str = String(id).trim();
  if (type === 'fix' || (!type && str.startsWith('fix_'))) {
    const match = str.match(API_FOOTBALL_FIXTURE_REGEX);
    return match ? match[1] : str.replace(/^fix_/, '');
  }
  if (type === 'comp' || (!type && str.startsWith('comp_'))) {
    const match = str.match(API_FOOTBALL_COMPETITION_REGEX);
    return match ? match[1] : str.replace(/^comp_/, '');
  }
  if (type === 'team' || (!type && str.startsWith('team_'))) {
    const match = str.match(API_FOOTBALL_TEAM_REGEX);
    return match ? match[1] : str.replace(/^team_/, '');
  }
  return str;
}

/**
 * Resolves a canonical fixture ID from a provider ID or legacy ID using the fixture_provider_aliases table.
 */
export async function resolveFixtureCanonicalIdByAlias(
  executor: DbExecutor,
  idOrAlias: string,
  provider?: string
): Promise<string | null> {
  const trimmed = idOrAlias.trim();
  const providerId = extractProviderId(trimmed, 'fix');

  const conditions = [
    or(
      eq(schema.fixtureProviderAliases.providerId, trimmed),
      eq(schema.fixtureProviderAliases.providerId, providerId)
    ),
  ];

  if (provider) {
    conditions.push(eq(schema.fixtureProviderAliases.provider, provider));
  }

  const alias = await executor.query.fixtureProviderAliases.findFirst({
    where: and(...conditions),
  });

  return alias?.canonicalFixtureId ?? null;
}

/**
 * Ensures that a provider alias is mapped to the canonical entity in PostgreSQL.
 *
 * Invariant behavior (Checkpoint 1A & 1B specification):
 * - Case 1: Alias does not exist -> creates it with first_seen_at = NOW(), last_seen_at = NOW() (returns true).
 * - Case 2: Alias exists and points to SAME canonical entity -> idempotent no-op (updates last_seen_at = NOW(), returns false).
 * - Case 3: Alias exists and points to DIFFERENT canonical entity -> throws Error (triggers transaction rollback / fail-closed).
 */
export async function ensureProviderAlias(
  executor: DbExecutor,
  entityType: AliasEntityType,
  canonicalId: string,
  provider: string,
  providerId: string
): Promise<boolean> {
  if (entityType === 'competition') {
    const insertRes = await executor
      .insert(schema.competitionProviderAliases)
      .values({
        canonicalCompetitionId: canonicalId,
        provider,
        providerId,
      })
      .onConflictDoNothing()
      .returning();

    if (insertRes.length > 0) {
      return true; // Case 1: fresh alias created
    }

    const existing = await executor.query.competitionProviderAliases.findFirst({
      where: and(
        eq(schema.competitionProviderAliases.provider, provider),
        eq(schema.competitionProviderAliases.providerId, providerId)
      ),
    });

    if (!existing) {
      throw new Error(`Failed to resolve existing alias in competition_provider_aliases for ${provider}:${providerId}`);
    }

    if (existing.canonicalCompetitionId !== canonicalId) {
      throw new Error(
        `Conflicting provider alias detected in competition_provider_aliases for ${provider}:${providerId}: existing alias points to "${existing.canonicalCompetitionId}", but ingestion attempted mapping to "${canonicalId}".`
      );
    }

    // Touch lastSeenAt on repeat ingestion
    await executor
      .update(schema.competitionProviderAliases)
      .set({ lastSeenAt: new Date() })
      .where(
        and(
          eq(schema.competitionProviderAliases.provider, provider),
          eq(schema.competitionProviderAliases.providerId, providerId)
        )
      );

    return false; // Case 2: Idempotent no-op
  }

  if (entityType === 'team') {
    const insertRes = await executor
      .insert(schema.teamProviderAliases)
      .values({
        canonicalTeamId: canonicalId,
        provider,
        providerId,
      })
      .onConflictDoNothing()
      .returning();

    if (insertRes.length > 0) {
      return true;
    }

    const existing = await executor.query.teamProviderAliases.findFirst({
      where: and(
        eq(schema.teamProviderAliases.provider, provider),
        eq(schema.teamProviderAliases.providerId, providerId)
      ),
    });

    if (!existing) {
      throw new Error(`Failed to resolve existing alias in team_provider_aliases for ${provider}:${providerId}`);
    }

    if (existing.canonicalTeamId !== canonicalId) {
      throw new Error(
        `Conflicting provider alias detected in team_provider_aliases for ${provider}:${providerId}: existing alias points to "${existing.canonicalTeamId}", but ingestion attempted mapping to "${canonicalId}".`
      );
    }

    await executor
      .update(schema.teamProviderAliases)
      .set({ lastSeenAt: new Date() })
      .where(
        and(
          eq(schema.teamProviderAliases.provider, provider),
          eq(schema.teamProviderAliases.providerId, providerId)
        )
      );

    return false;
  }

  if (entityType === 'fixture') {
    const insertRes = await executor
      .insert(schema.fixtureProviderAliases)
      .values({
        canonicalFixtureId: canonicalId,
        provider,
        providerId,
      })
      .onConflictDoNothing()
      .returning();

    if (insertRes.length > 0) {
      return true;
    }

    const existing = await executor.query.fixtureProviderAliases.findFirst({
      where: and(
        eq(schema.fixtureProviderAliases.provider, provider),
        eq(schema.fixtureProviderAliases.providerId, providerId)
      ),
    });

    if (!existing) {
      throw new Error(`Failed to resolve existing alias in fixture_provider_aliases for ${provider}:${providerId}`);
    }

    if (existing.canonicalFixtureId !== canonicalId) {
      throw new Error(
        `Conflicting provider alias detected in fixture_provider_aliases for ${provider}:${providerId}: existing alias points to "${existing.canonicalFixtureId}", but ingestion attempted mapping to "${canonicalId}".`
      );
    }

    await executor
      .update(schema.fixtureProviderAliases)
      .set({ lastSeenAt: new Date() })
      .where(
        and(
          eq(schema.fixtureProviderAliases.provider, provider),
          eq(schema.fixtureProviderAliases.providerId, providerId)
        )
      );

    return false;
  }

  throw new Error(`Unsupported alias entity type: ${entityType}`);
}

import { FOTMOB_COMPETITION_MAP, CANONICAL_COMPETITION_MAP } from '../acquisition/priority-competitions.js';

/**
 * Seeds or updates FotMob provider competition aliases for all priority competitions.
 */
export async function seedFotmobCompetitionAliases(executor: DbExecutor = db): Promise<number> {
  let seeded = 0;
  for (const [apiIdStr, fotmobId] of Object.entries(FOTMOB_COMPETITION_MAP)) {
    const apiId = parseInt(apiIdStr, 10);
    const canonicalId = CANONICAL_COMPETITION_MAP[apiId];
    if (!canonicalId) continue;

    const existingComp = await executor.query.competitions.findFirst({
      where: or(
        eq(schema.competitions.canonicalId, canonicalId),
        eq(schema.competitions.id, `comp_${apiId}`)
      ),
    });

    const targetCanonicalId = existingComp?.canonicalId || canonicalId;

    try {
      const created = await ensureProviderAlias(
        executor,
        'competition',
        targetCanonicalId,
        'fotmob',
        String(fotmobId)
      );
      if (created) seeded++;
    } catch {
      // Non-fatal if alias already mapped or conflict
    }
  }
  return seeded;
}
