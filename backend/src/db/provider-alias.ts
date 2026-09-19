import { eq, and } from 'drizzle-orm';
import { db, schema } from './index.js';

export type AliasEntityType = 'competition' | 'team' | 'fixture';

export type DbExecutor = typeof db | Parameters<Parameters<typeof db.transaction>[0]>[0];

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
