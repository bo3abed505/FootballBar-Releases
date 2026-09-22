import { inArray, eq, and, gte, sql } from 'drizzle-orm';
import { db, schema } from '../db/index.js';
import { FootballProvider, FixtureFilter, ProviderFixtureDetail } from '../providers/football-provider.js';
import { AcquisitionService, NormalizedAcquisitionBatch } from './acquisition.service.js';
import { normalizeEvent } from '../normalization/event.js';
import { AcquisitionPolicyEngine } from './acquisition-policy.js';
import { generateCanonicalId } from '../db/canonical-id.js';
import { ensureProviderAlias } from '../db/provider-alias.js';
import { Logger } from '../diagnostics/logger.js';

export interface SyncSummary {
  provider: string;
  fixturesDiscovered: number;
  fixturesInserted: number;
  fixturesUpdated: number;
  eventsInserted: number;
  eventsSkippedDuplicates: number;
  hasChanges: boolean;
}

export class SyncService {
  // Normal regulation match duration threshold after which an absent live match is considered concluded.
  // 90 minutes regulation + 15 minutes halftime = 105 minutes.
  // A match absent from live=all after 105 minutes has almost certainly finished regulation time.
  static readonly SAFE_VANISHED_MATCH_AGE_MS = 105 * 60 * 1000;

  private readonly acquisitionService: AcquisitionService;

  constructor(private readonly provider: FootballProvider) {
    this.acquisitionService = new AcquisitionService(provider);
  }

  /**
   * Executes a full deterministic synchronization cycle against PostgreSQL
   */
  async sync(filter?: FixtureFilter): Promise<SyncSummary> {
    if (filter?.date && !filter?.liveOnly) {
      return this.syncSchedule(filter.date, filter.competitionIds, filter.cycleId);
    }
    const batch = await this.acquisitionService.acquireAll(filter);
    return this.processBatch(batch, filter?.liveOnly);
  }

  /**
   * Processes and persists a normalized acquisition batch into PostgreSQL.
   */
  async processBatch(batch: NormalizedAcquisitionBatch, liveOnly = false): Promise<SyncSummary> {
    const summary: SyncSummary = {
      provider: this.provider.providerName,
      fixturesDiscovered: batch.fixtures.length,
      fixturesInserted: 0,
      fixturesUpdated: 0,
      eventsInserted: 0,
      eventsSkippedDuplicates: 0,
      hasChanges: false,
    };

    // 1. Upsert Competitions (Safe non-destructive merge with canonical identity)
    for (const comp of batch.competitions) {
      const { provider, providerId, ...compValues } = comp;
      await db.transaction(async (tx) => {
        const existing = await tx.query.competitions.findFirst({
          where: eq(schema.competitions.id, comp.id),
        });

        let canonicalId: string;

        if (!existing) {
          canonicalId = comp.canonicalId || generateCanonicalId('comp');
          await tx.insert(schema.competitions).values({
            ...compValues,
            canonicalId,
          });
        } else {
          if (!existing.canonicalId) {
            // Existing row with canonical_id NULL: assign atomically
            const candidateId = generateCanonicalId('comp');
            const updated = await tx
              .update(schema.competitions)
              .set({
                canonicalId: sql`COALESCE(${schema.competitions.canonicalId}, ${candidateId})`,
                updatedAt: new Date(),
              })
              .where(eq(schema.competitions.id, comp.id))
              .returning({ canonicalId: schema.competitions.canonicalId });
            canonicalId = updated[0]?.canonicalId || candidateId;
          } else {
            // Existing canonical_id is preserved exactly
            canonicalId = existing.canonicalId;
          }

          const updatedName = comp.name || existing.name;
          const updatedCode = comp.code ?? existing.code;
          const updatedType = comp.type ?? existing.type;
          const updatedCountry = comp.country ?? existing.country;
          const updatedLogoUrl = comp.logoUrl ?? existing.logoUrl;

          if (
            existing.name !== updatedName ||
            existing.code !== updatedCode ||
            existing.type !== updatedType ||
            existing.country !== updatedCountry ||
            existing.logoUrl !== updatedLogoUrl
          ) {
            await tx
              .update(schema.competitions)
              .set({
                name: updatedName,
                code: updatedCode,
                type: updatedType,
                country: updatedCountry,
                logoUrl: updatedLogoUrl,
                updatedAt: new Date(),
              })
              .where(eq(schema.competitions.id, comp.id));
          }
        }

        // Establish alias if explicit provider identity is present
        if (provider === 'api_football' && providerId) {
          await ensureProviderAlias(tx, 'competition', canonicalId, provider, providerId);
        }
      });
    }

    // 2. Upsert Seasons (Safe non-destructive merge)
    for (const season of batch.seasons) {
      const existing = await db.query.competitionSeasons.findFirst({
        where: eq(schema.competitionSeasons.id, season.id),
      });

      if (!existing) {
        await db.insert(schema.competitionSeasons).values(season).onConflictDoNothing();
      } else {
        if (existing.isCurrent !== season.isCurrent) {
          await db
            .update(schema.competitionSeasons)
            .set({ isCurrent: season.isCurrent })
            .where(eq(schema.competitionSeasons.id, season.id));
        }
      }
    }

    // 3. Upsert Teams (Safe non-destructive merge with canonical identity)
    for (const team of batch.teams) {
      const { provider, providerId, ...teamValues } = team;
      await db.transaction(async (tx) => {
        const existing = await tx.query.teams.findFirst({
          where: eq(schema.teams.id, team.id),
        });

        let canonicalId: string;

        if (!existing) {
          canonicalId = team.canonicalId || generateCanonicalId('team');
          await tx.insert(schema.teams).values({
            ...teamValues,
            canonicalId,
          });
        } else {
          if (!existing.canonicalId) {
            // Existing row with canonical_id NULL: assign atomically
            const candidateId = generateCanonicalId('team');
            const updated = await tx
              .update(schema.teams)
              .set({
                canonicalId: sql`COALESCE(${schema.teams.canonicalId}, ${candidateId})`,
                updatedAt: new Date(),
              })
              .where(eq(schema.teams.id, team.id))
              .returning({ canonicalId: schema.teams.canonicalId });
            canonicalId = updated[0]?.canonicalId || candidateId;
          } else {
            // Existing canonical_id is preserved exactly
            canonicalId = existing.canonicalId;
          }

          const updatedName = team.name || existing.name;
          const updatedShortName = team.shortName || existing.shortName;
          const updatedCode = team.code ?? existing.code;
          const updatedCountry = team.country ?? existing.country;
          const updatedLogoUrl = team.logoUrl ?? existing.logoUrl;
          const updatedVenue = team.venueName ?? existing.venueName;

          if (
            existing.name !== updatedName ||
            existing.shortName !== updatedShortName ||
            existing.code !== updatedCode ||
            existing.country !== updatedCountry ||
            existing.logoUrl !== updatedLogoUrl ||
            existing.venueName !== updatedVenue
          ) {
            await tx
              .update(schema.teams)
              .set({
                name: updatedName,
                shortName: updatedShortName,
                code: updatedCode,
                country: updatedCountry,
                logoUrl: updatedLogoUrl,
                venueName: updatedVenue,
                updatedAt: new Date(),
              })
              .where(eq(schema.teams.id, team.id));
          }
        }

        // Establish alias if explicit provider identity is present
        if (provider === 'api_football' && providerId) {
          await ensureProviderAlias(tx, 'team', canonicalId, provider, providerId);
        }
      });
    }

    // 4. Upsert Fixtures with Comprehensive Change Detection & Non-destructive Merge
    for (const incoming of batch.fixtures) {
      const { provider, providerId, ...incomingValues } = incoming;
      await db.transaction(async (tx) => {
        const existing = await tx.query.fixtures.findFirst({
          where: eq(schema.fixtures.id, incoming.id),
        });

        let canonicalId: string;

        if (!existing) {
          // New fixture insertion
          canonicalId = incoming.canonicalId || generateCanonicalId('fix');
          await tx.insert(schema.fixtures).values({
            ...incomingValues,
            canonicalId,
          });
          summary.fixturesInserted++;
          summary.hasChanges = true;
        } else {
          if (!existing.canonicalId) {
            // Existing row with canonical_id NULL: assign atomically
            const candidateId = generateCanonicalId('fix');
            const updated = await tx
              .update(schema.fixtures)
              .set({
                canonicalId: sql`COALESCE(${schema.fixtures.canonicalId}, ${candidateId})`,
                updatedAt: new Date(),
              })
              .where(eq(schema.fixtures.id, incoming.id))
              .returning({ canonicalId: schema.fixtures.canonicalId });
            canonicalId = updated[0]?.canonicalId || candidateId;
          } else {
            // Existing canonical_id is preserved exactly
            canonicalId = existing.canonicalId;
          }

          // Non-destructive preservation of already known good optional data
          const mergedHomeScoreHt = incoming.homeScoreHt ?? existing.homeScoreHt;
          const mergedAwayScoreHt = incoming.awayScoreHt ?? existing.awayScoreHt;
          const mergedHomeScoreEt = incoming.homeScoreEt ?? existing.homeScoreEt;
          const mergedAwayScoreEt = incoming.awayScoreEt ?? existing.awayScoreEt;
          const mergedHomeScorePen = incoming.homeScorePen ?? existing.homeScorePen;
          const mergedAwayScorePen = incoming.awayScorePen ?? existing.awayScorePen;
          const mergedVenue = incoming.venue ?? existing.venue;
          const mergedReferee = incoming.referee ?? existing.referee;
          const mergedRound = incoming.round ?? existing.round;
          const mergedKickoff = incoming.kickoffTime ?? existing.kickoffTime;

          // Check for meaningful changes
          const hasScoreChanged =
            existing.homeScore !== incoming.homeScore ||
            existing.awayScore !== incoming.awayScore;
          const hasStateChanged = existing.state !== incoming.state;
          const hasMinuteChanged =
            existing.elapsedMinute !== incoming.elapsedMinute ||
            existing.extraMinute !== incoming.extraMinute;
          const hasHtChanged =
            existing.homeScoreHt !== mergedHomeScoreHt ||
            existing.awayScoreHt !== mergedAwayScoreHt;
          const hasEtChanged =
            existing.homeScoreEt !== mergedHomeScoreEt ||
            existing.awayScoreEt !== mergedAwayScoreEt;
          const hasPenChanged =
            existing.homeScorePen !== mergedHomeScorePen ||
            existing.awayScorePen !== mergedAwayScorePen;
          const hasKickoffChanged =
            existing.kickoffTime.getTime() !== mergedKickoff.getTime();
          const hasMetadataChanged =
            existing.venue !== mergedVenue ||
            existing.referee !== mergedReferee ||
            existing.round !== mergedRound;

          const isChanged =
            hasScoreChanged ||
            hasStateChanged ||
            hasMinuteChanged ||
            hasHtChanged ||
            hasEtChanged ||
            hasPenChanged ||
            hasKickoffChanged ||
            hasMetadataChanged;

          if (isChanged) {
            await tx
              .update(schema.fixtures)
              .set({
                state: incoming.state,
                elapsedMinute: incoming.elapsedMinute,
                extraMinute: incoming.extraMinute,
                homeScore: incoming.homeScore,
                awayScore: incoming.awayScore,
                homeScoreHt: mergedHomeScoreHt,
                awayScoreHt: mergedAwayScoreHt,
                homeScoreEt: mergedHomeScoreEt,
                awayScoreEt: mergedAwayScoreEt,
                homeScorePen: mergedHomeScorePen,
                awayScorePen: mergedAwayScorePen,
                kickoffTime: mergedKickoff,
                round: mergedRound,
                venue: mergedVenue,
                referee: mergedReferee,
                lastPolledAt: new Date(),
                updatedAt: new Date(),
              })
              .where(eq(schema.fixtures.id, incoming.id));

            summary.fixturesUpdated++;
            summary.hasChanges = true;
          } else {
            // Update lastPolledAt only without changing updatedAt version timestamp
            await tx
              .update(schema.fixtures)
              .set({ lastPolledAt: new Date() })
              .where(eq(schema.fixtures.id, incoming.id));
          }
        }

        // Establish alias if explicit provider identity is present
        if (provider === 'api_football' && providerId) {
          await ensureProviderAlias(tx, 'fixture', canonicalId, provider, providerId);
        }
      });
    }

    // 5. Upsert Events with Duplicate Deduplication
    if (batch.events.length > 0) {
      const incomingUids = batch.events.map((e) => e.eventUid);
      const existingEvents = await db
        .select({ eventUid: schema.events.eventUid })
        .from(schema.events)
        .where(inArray(schema.events.eventUid, incomingUids));

      const existingUidSet = new Set(existingEvents.map((e) => e.eventUid));
      
      // Deduplicate both against existing database records and within incoming batch
      const seenBatchUids = new Set<string>();
      const newEvents = batch.events.filter((e) => {
        if (existingUidSet.has(e.eventUid) || seenBatchUids.has(e.eventUid)) {
          return false;
        }
        seenBatchUids.add(e.eventUid);
        return true;
      });

      summary.eventsSkippedDuplicates = batch.events.length - newEvents.length;

      if (newEvents.length > 0) {
        await db.insert(schema.events).values(newEvents).onConflictDoNothing();
        summary.eventsInserted = newEvents.length;
        summary.hasChanges = true;
      }
    }

    // 6. Vanished-Live Fixture Reconciliation
    // If this was a live-only sync, inspect local active fixtures that were NOT returned by the provider.
    // When a match vanishes after normal match duration (>= 105m):
    // 1. Attempt bounded provider detail lookup to confirm completion and capture true final state.
    // 2. If details confirm FINISHED, POSTPONED, CANCELLED, etc., transition accordingly.
    // 3. If details are unavailable and elapsed time >= MAX_LIVE_MATCH_AGE_MS (3.5h), transition to FINISHED.
    // 4. If details are unavailable and elapsed time < MAX_LIVE_MATCH_AGE_MS, touch lastPolledAt but remain LIVE
    //    to safely protect delayed, suspended, or temporarily omitted matches from premature finalization.
    if (liveOnly) {
      const incomingLiveIds = new Set(batch.fixtures.map((f) => f.id));
      const now = new Date();
      const nowMs = now.getTime();

      const activeLocalFixtures = await db.query.fixtures.findMany({
        where: inArray(schema.fixtures.state, ['LIVE', 'HALFTIME', '1H', '2H', 'ET', 'P', 'BT', 'IN_PLAY']),
      });

      let detailLookupsPerformed = 0;
      const MAX_DETAIL_LOOKUPS_PER_CYCLE = 3;

      for (const localFix of activeLocalFixtures) {
        if (incomingLiveIds.has(localFix.id)) {
          continue;
        }

        const kickoffMs = new Date(localFix.kickoffTime).getTime();
        const elapsedSinceKickoffMs = nowMs - kickoffMs;

        // Candidate for reconciliation: elapsed time >= SAFE_VANISHED_MATCH_AGE_MS (105m)
        if (elapsedSinceKickoffMs >= SyncService.SAFE_VANISHED_MATCH_AGE_MS) {
          let detailsConfirmed = false;

          if (detailLookupsPerformed < MAX_DETAIL_LOOKUPS_PER_CYCLE) {
            detailLookupsPerformed++;
            try {
              const details = await this.provider.getFixtureDetails(localFix.id);
              if (details) {
                detailsConfirmed = true;
                const mappedState = (details.state || '').toUpperCase().trim();
                let targetState = 'FINISHED';
                if (['POSTPONED', 'CANCELLED', 'SUSP', 'INT', 'ABD', 'AWD', 'WO'].includes(mappedState)) {
                  targetState = mappedState;
                }

                await db
                  .update(schema.fixtures)
                  .set({
                    state: targetState,
                    homeScore: details.score.home,
                    awayScore: details.score.away,
                    homeScoreHt: details.score.homeHt ?? localFix.homeScoreHt,
                    awayScoreHt: details.score.awayHt ?? localFix.awayScoreHt,
                    lastPolledAt: now,
                    updatedAt: now,
                  })
                  .where(eq(schema.fixtures.id, localFix.id));

                if (details.events && details.events.length > 0) {
                  const incomingUids = details.events.map((e) => e.eventUid);
                  const existingEvents = await db
                    .select({ eventUid: schema.events.eventUid })
                    .from(schema.events)
                    .where(inArray(schema.events.eventUid, incomingUids));

                  const existingUidSet = new Set(existingEvents.map((e) => e.eventUid));
                  const newEvents = details.events
                    .filter((e) => !existingUidSet.has(e.eventUid))
                    .map(normalizeEvent);
                  if (newEvents.length > 0) {
                    await db.insert(schema.events).values(newEvents).onConflictDoNothing();
                    summary.eventsInserted += newEvents.length;
                  }
                }

                summary.fixturesUpdated++;
                summary.hasChanges = true;
                continue;
              }
            } catch {
              // Bounded detail lookup failed; proceed to fallback below
            }
          }

          // Fallback when details are unavailable or not confirmed:
          // 1. If elapsed time exceeds MAX_LIVE_MATCH_AGE_MS (3.5h), the match is definitively past
          //    any possible in-play window. Transition to FINISHED to prevent permanent live lock.
          if (elapsedSinceKickoffMs >= AcquisitionPolicyEngine.MAX_LIVE_MATCH_AGE_MS) {
            await db
              .update(schema.fixtures)
              .set({
                state: 'FINISHED',
                lastPolledAt: now,
                updatedAt: now,
              })
              .where(eq(schema.fixtures.id, localFix.id));

            summary.fixturesUpdated++;
            summary.hasChanges = true;
          } else if (!detailsConfirmed) {
            // 2. If elapsed time is between 105m and 210m, and details were unconfirmed:
            //    Touch lastPolledAt but keep state = 'LIVE' to prevent prematurely finalizing
            //    delayed, unusually long, or temporarily omitted matches.
            await db
              .update(schema.fixtures)
              .set({ lastPolledAt: now })
              .where(eq(schema.fixtures.id, localFix.id));
          }
        }
      }
    }

    return summary;
  }

  /**
   * Synchronizes active live matches (consolidated single query).
   */
  async syncLive(competitionIds?: number[], cycleId?: string): Promise<SyncSummary> {
    return this.sync({ liveOnly: true, competitionIds, cycleId });
  }

  /**
   * Synchronizes daily match schedule for a specific date across target competitions.
   * Performs a single date-wide discovery query and filters in-memory using competition_provider_aliases.
   */
  async syncSchedule(date: string, competitionIds?: number[], cycleId?: string): Promise<SyncSummary> {
    // 1. Single date-wide fetch across all competitions for the target date
    const batch = await this.acquisitionService.acquireScheduled(date, competitionIds, cycleId);

    // 2. Discover active monitored competitions via competition_provider_aliases
    const activeAliases = await db
      .select({
        providerId: schema.competitionProviderAliases.providerId,
        canonicalCompetitionId: schema.competitionProviderAliases.canonicalCompetitionId,
      })
      .from(schema.competitionProviderAliases)
      .where(eq(schema.competitionProviderAliases.provider, this.provider.providerName));

    const activeProviderIds = new Set(activeAliases.map((a) => a.providerId));

    let effectiveProviderIds = activeProviderIds;
    if (activeProviderIds.size === 0 && competitionIds && competitionIds.length > 0) {
      effectiveProviderIds = new Set(competitionIds.map(String));
    } else if (activeProviderIds.size > 0 && competitionIds && competitionIds.length > 0) {
      const requested = new Set(competitionIds.map(String));
      const intersected = new Set([...activeProviderIds].filter((id) => requested.has(id)));
      if (intersected.size > 0) {
        effectiveProviderIds = intersected;
      }
    }

    // 3. Filter the batch in-memory against active competitions
    if (effectiveProviderIds.size > 0) {
      const allowedCompIds = new Set<string>();
      for (const pId of effectiveProviderIds) {
        allowedCompIds.add(pId);
        allowedCompIds.add(`comp_${pId}`);
      }

      const compProviderMap = new Map<string, string>();
      for (const c of batch.competitions) {
        if (c.providerId) compProviderMap.set(c.id, c.providerId);
      }

      const isCompAllowed = (compId: string): boolean => {
        if (allowedCompIds.has(compId)) return true;
        const pId = compProviderMap.get(compId);
        return !!pId && (effectiveProviderIds.has(pId) || allowedCompIds.has(pId));
      };

      const filteredFixtures = batch.fixtures.filter((f) => isCompAllowed(f.competitionId));
      const filteredCompetitions = batch.competitions.filter((c) => isCompAllowed(c.id));
      const filteredSeasons = batch.seasons.filter((s) => isCompAllowed(s.competitionId));

      const retainedTeamIds = new Set<string>();
      for (const f of filteredFixtures) {
        retainedTeamIds.add(f.homeTeamId);
        retainedTeamIds.add(f.awayTeamId);
      }
      const filteredTeams = batch.teams.filter((t) => retainedTeamIds.has(t.id));

      const retainedFixtureIds = new Set(filteredFixtures.map((f) => f.id));
      const filteredEvents = batch.events.filter((e) => retainedFixtureIds.has(e.fixtureId));

      const filteredBatch: NormalizedAcquisitionBatch = {
        competitions: filteredCompetitions,
        seasons: filteredSeasons,
        teams: filteredTeams,
        fixtures: filteredFixtures,
        events: filteredEvents,
      };

      return this.processBatch(filteredBatch, false);
    }

    return this.processBatch(batch, false);
  }

  /**
   * Finalizes recently finished matches to capture final score and events.
   * Delegates to acquireFinalization.
   */
  async finalizeRecentlyFinished(competitionIds?: (string | number)[], cycleId?: string): Promise<SyncSummary> {
    return this.acquireFinalization(competitionIds, cycleId);
  }

  /**
   * Acquires finalization details in batches of up to 20 fixtures.
   * Employs partial failure isolation so anomalies or missing fixtures in the batch
   * do not abort the reconciliation of remaining valid fixtures.
   */
  async acquireFinalization(competitionIds?: (string | number)[], cycleId?: string): Promise<SyncSummary> {
    const summary: SyncSummary = {
      provider: this.provider.providerName,
      fixturesDiscovered: 0,
      fixturesInserted: 0,
      fixturesUpdated: 0,
      eventsInserted: 0,
      eventsSkippedDuplicates: 0,
      hasChanges: false,
    };

    const now = new Date();
    const whereConditions = [
      inArray(schema.fixtures.state, ['FINISHED', 'POSTMATCH']),
      gte(schema.fixtures.updatedAt, new Date(now.getTime() - 30 * 60 * 1000)),
    ];

    if (competitionIds && competitionIds.length > 0) {
      whereConditions.push(
        inArray(schema.fixtures.competitionId, competitionIds.map(String))
      );
    }

    // Bounded: fetch up to 20 recently finished fixtures per cycle, prioritizing unpolled fixtures
    const candidates = await db.query.fixtures.findMany({
      where: and(...whereConditions),
      orderBy: (fixtures, { asc, desc }) => [asc(fixtures.lastPolledAt), desc(fixtures.updatedAt)],
      limit: 20,
    });

    if (candidates.length === 0) {
      return summary;
    }

    // Process candidate fixtures in batches of up to 20 IDs
    const BATCH_SIZE = 20;
    for (let i = 0; i < candidates.length; i += BATCH_SIZE) {
      const batchCandidates = candidates.slice(i, i + BATCH_SIZE);
      const batchIds = batchCandidates.map((c) => c.id);

      let detailsList: ProviderFixtureDetail[] = [];
      try {
        if (typeof this.provider.getFixtureDetailsBatch === 'function') {
          detailsList = await this.provider.getFixtureDetailsBatch(batchIds, cycleId);
        } else {
          // Fallback if provider doesn't implement batching
          const fetched = await Promise.all(
            batchIds.map((id) => this.provider.getFixtureDetails(id, cycleId))
          );
          detailsList = fetched.filter((d): d is ProviderFixtureDetail => d !== null);
        }
      } catch (err) {
        Logger.warn('SyncService', 'BatchAcquisitionError', `Batch fixture details acquisition failed for batch of ${batchIds.length} fixtures: ${err}`, cycleId);
        // Touch lastPolledAt on all candidates so they don't loop endlessly on persistent errors
        for (const candidate of batchCandidates) {
          await db
            .update(schema.fixtures)
            .set({ lastPolledAt: now })
            .where(eq(schema.fixtures.id, candidate.id));
        }
        continue;
      }

      const detailsMap = new Map<string, ProviderFixtureDetail>();
      for (const d of detailsList) {
        detailsMap.set(d.id, d);
      }

      // Reconcile each candidate with partial failure isolation
      for (const fix of batchCandidates) {
        try {
          const details = detailsMap.get(fix.id);
          if (details) {
            await db
              .update(schema.fixtures)
              .set({
                homeScore: details.score.home,
                awayScore: details.score.away,
                homeScoreHt: details.score.homeHt ?? fix.homeScoreHt,
                awayScoreHt: details.score.awayHt ?? fix.awayScoreHt,
                lastPolledAt: now,
                updatedAt: now,
              })
              .where(eq(schema.fixtures.id, fix.id));

            summary.fixturesUpdated++;
            summary.hasChanges = true;

            if (details.events && details.events.length > 0) {
              const incomingUids = details.events.map((e) => e.eventUid);
              const existingEvents = await db
                .select({ eventUid: schema.events.eventUid })
                .from(schema.events)
                .where(inArray(schema.events.eventUid, incomingUids));

              const existingUidSet = new Set(existingEvents.map((e) => e.eventUid));
              const newEvents = details.events
                .filter((e) => !existingUidSet.has(e.eventUid))
                .map(normalizeEvent);
              if (newEvents.length > 0) {
                await db.insert(schema.events).values(newEvents).onConflictDoNothing();
                summary.eventsInserted += newEvents.length;
                summary.hasChanges = true;
              }
            }
          } else {
            // Missing from batch response: touch lastPolledAt and log warning so it doesn't loop forever
            Logger.warn('SyncService', 'MissingBatchFixture', `Fixture ${fix.id} candidate for finalization was not returned in provider batch details response`, cycleId);
            await db
              .update(schema.fixtures)
              .set({ lastPolledAt: now })
              .where(eq(schema.fixtures.id, fix.id));
          }
        } catch (itemErr) {
          Logger.warn('SyncService', 'ReconciliationError', `Failed reconciling fixture ${fix.id} during batch finalization: ${itemErr}`, cycleId);
          await db
            .update(schema.fixtures)
            .set({ lastPolledAt: now })
            .where(eq(schema.fixtures.id, fix.id));
        }
      }
    }

    return summary;
  }

  /**
   * Recovers stale prematch fixtures to check for postponement/cancellation.
   */
  async recoverStale(options?: { date?: string; competitionIds?: number[]; cycleId?: string } | number[], cycleId?: string): Promise<SyncSummary> {
    if (Array.isArray(options)) {
      if (options.length === 0) {
        return {
          provider: this.provider.providerName,
          fixturesDiscovered: 0,
          fixturesInserted: 0,
          fixturesUpdated: 0,
          eventsInserted: 0,
          eventsSkippedDuplicates: 0,
          hasChanges: false,
        };
      }
      const batch = await this.acquisitionService.acquireStaleRecovery(options, cycleId);
      return this.processBatch(batch, false);
    }

    if (options?.competitionIds && options.competitionIds.length === 0) {
      return {
        provider: this.provider.providerName,
        fixturesDiscovered: 0,
        fixturesInserted: 0,
        fixturesUpdated: 0,
        eventsInserted: 0,
        eventsSkippedDuplicates: 0,
        hasChanges: false,
      };
    }

    const batch = await this.acquisitionService.acquireStaleRecovery(
      options?.competitionIds,
      options?.cycleId ?? cycleId
    );
    return this.processBatch(batch, false);
  }

  /**
   * Executes synchronization according to an AcquisitionPolicyEngine SyncPlan.
   * Returns SyncSummary if an external action was taken, or null if IDLE.
   */
  async syncWithPlan(
    plan: import('./acquisition-policy.js').SyncPlan,
    options?: { date?: string; competitionIds?: number[]; cycleId?: string }
  ): Promise<SyncSummary | null> {
    switch (plan.action) {
      case 'POLL_LIVE':
        return this.syncLive(options?.competitionIds, options?.cycleId);
      case 'DISCOVER_SCHEDULE':
        return this.syncSchedule(
          options?.date ?? new Date().toISOString().slice(0, 10),
          options?.competitionIds,
          options?.cycleId
        );
      case 'FINALIZE':
        return this.finalizeRecentlyFinished(options?.competitionIds, options?.cycleId);
      case 'RECOVER_STALE':
        return this.recoverStale(options, options?.cycleId);
      case 'IDLE':
      default:
        return null;
    }
  }
}

