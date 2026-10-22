import { inArray, eq, and, or, gte, sql, isNull } from 'drizzle-orm';
import { db, schema } from '../db/index.js';
import { IDataProvider, FixtureFilter, NormalizedMatchDetails } from '../providers/types.js';
import { AcquisitionService, NormalizedAcquisitionBatch } from './acquisition.service.js';
import { normalizeEvent } from '../normalization/event.js';
import { AcquisitionPolicyEngine } from './acquisition-policy.js';
import { generateCanonicalId } from '../db/canonical-id.js';
import { ensureProviderAlias } from '../db/provider-alias.js';
import { Logger } from '../diagnostics/logger.js';

import { MatchRepository, BatchPersistResult } from '../repositories/match.repository.js';
import { ProviderNetworkError, ProviderChallengeError } from '../providers/errors.js';

export interface SyncSummary {
  provider: string;
  fixturesDiscovered: number;
  fixturesInserted: number;
  fixturesUpdated: number;
  eventsInserted: number;
  eventsSkippedDuplicates: number;
  hasChanges: boolean;
  cachedFallback?: boolean;
  discardedCount?: number;
}

export class SyncService {
  // Normal regulation match duration threshold after which an absent live match is considered concluded.
  // 90 minutes regulation + 15 minutes halftime = 105 minutes.
  // A match absent from live=all after 105 minutes has almost certainly finished regulation time.
  static readonly SAFE_VANISHED_MATCH_AGE_MS = 105 * 60 * 1000;

  public readonly provider: IDataProvider;
  public readonly acquisitionService: AcquisitionService;
  public readonly matchRepository: MatchRepository;
  public lastSyncSummary: SyncSummary | null = null;
  private readonly matchDetailsCache = new Map<string, NormalizedMatchDetails>();

  constructor(provider: IDataProvider, matchRepository?: MatchRepository) {
    this.provider = provider;
    this.acquisitionService = new AcquisitionService(provider);
    this.matchRepository = matchRepository ?? new MatchRepository();
  }

  public getCachedMatchDetails(id: string): NormalizedMatchDetails | null {
    return this.matchDetailsCache.get(id) ?? null;
  }

  private getProviderName(): string {
    const raw = (this.provider as any)?.providerName || this.provider?.name || 'unknown';
    return raw === 'api-football' ? 'api_football' : raw;
  }

  /**
   * Determines if an error is a transient network or circuit breaker event that should trigger offline cache fallback.
   */
  private isRecoverableNetworkOrCircuitError(error: any): boolean {
    if (!error) return false;
    const msg = error.message || String(error);
    return (
      msg.includes('CircuitBreakerOpen') ||
      msg.includes('CircuitBreaker') ||
      msg.includes('timeout') ||
      msg.includes('AbortError') ||
      msg.includes('fetch failed') ||
      msg.includes('ECONNREFUSED') ||
      msg.includes('ETIMEDOUT') ||
      msg.includes('ENOTFOUND') ||
      error.name === 'AbortError' ||
      error instanceof ProviderNetworkError ||
      error instanceof ProviderChallengeError
    );
  }

  /**
   * Executes a full deterministic synchronization cycle against SQLite.
   * If the CircuitBreaker is OPEN or a network timeout occurs, gracefully falls back to serving cached SQLite state.
   */
  async sync(filter?: FixtureFilter): Promise<SyncSummary> {
    try {
      if (filter?.date && !filter?.liveOnly) {
        return await this.syncSchedule(filter.date, filter.competitionIds, filter.cycleId);
      }
      const batch = await this.acquisitionService.acquireAll(filter);
      const summary = await this.processBatch(batch, filter?.liveOnly);
      this.lastSyncSummary = summary;
      return summary;
    } catch (error: any) {
      if (this.isRecoverableNetworkOrCircuitError(error)) {
        Logger.warn(
          'SyncService',
          'OfflineFallback',
          `Live synchronization encountered upstream interruption (${error.message}). Serving cached state from SQLite without crashing.`,
          filter?.cycleId
        );
        const fallbackSummary: SyncSummary = {
          provider: this.getProviderName(),
          fixturesDiscovered: 0,
          fixturesInserted: 0,
          fixturesUpdated: 0,
          eventsInserted: 0,
          eventsSkippedDuplicates: 0,
          hasChanges: false,
          cachedFallback: true,
        };
        this.lastSyncSummary = fallbackSummary;
        return fallbackSummary;
      }
      throw error;
    }
  }

  /**
   * Ingests an arbitrary array of NormalizedMatch objects directly into the database.
   * Executes Partial Failure Isolation, canonical transformation, and single-transaction persistence.
   */
  async ingestNormalizedMatches(
    rawMatches: unknown[],
    liveOnly = true
  ): Promise<BatchPersistResult> {
    return await this.matchRepository.ingestNormalizedMatches(rawMatches, {
      providerName: this.getProviderName(),
      liveOnly,
    });
  }

  /**
   * Processes and persists a normalized acquisition batch into SQLite.
   * Atomic Transaction Boundary: All operations commit in a single SQLite transaction.
   * Safe Upserts: Existing fixtures are updated in-place to protect foreign keys.
   */
  async processBatch(batch: NormalizedAcquisitionBatch, liveOnly = false): Promise<SyncSummary> {
    const result = await this.matchRepository.persistBatch(batch, {
      liveOnly,
      providerName: this.getProviderName(),
    });

    const summary: SyncSummary = {
      provider: result.provider,
      fixturesDiscovered: result.fixturesDiscovered,
      fixturesInserted: result.fixturesInserted,
      fixturesUpdated: result.fixturesUpdated,
      eventsInserted: result.eventsInserted,
      eventsSkippedDuplicates: result.eventsSkippedDuplicates,
      hasChanges: result.hasChanges,
      discardedCount: result.discardedCount,
    };

    if (liveOnly) {
      const incomingLiveIds = new Set(batch.fixtures.map((f) => f.id));
      const now = new Date();
      const nowMs = now.getTime();

      const activeLocalFixtures = await db.query.fixtures.findMany({
        where: inArray(schema.fixtures.state, [
          'LIVE', 'HALFTIME', '1H', '2H', 'FIRST_HALF', 'SECOND_HALF',
          'EXTRA_TIME', 'EXTRA_TIME_HALFTIME', 'PENALTIES', 'ET', 'P', 'BT', 'IN_PLAY'
        ]),
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
              let details: any = null;
              if (typeof (this.provider as any).getFixtureDetails === 'function') {
                details = await (this.provider as any).getFixtureDetails(localFix.id);
              } else if (typeof (this.provider as any).getMatchDetails === 'function') {
                details = await (this.provider as any).getMatchDetails(localFix.id);
              }

              if (details) {
                detailsConfirmed = true;
                const mappedState = (details.state || details.status?.reason || '').toUpperCase().trim();
                let targetState = 'FINISHED';
                if (['POSTPONED', 'CANCELLED', 'SUSP', 'INT', 'ABD', 'AWD', 'WO'].includes(mappedState)) {
                  targetState = mappedState;
                }

                const homeScore = details.score?.home ?? details.homeTeam?.score ?? localFix.homeScore ?? 0;
                const awayScore = details.score?.away ?? details.awayTeam?.score ?? localFix.awayScore ?? 0;
                const isFinished = ['FINISHED', 'FT', 'AET', 'PEN', 'POSTMATCH'].includes(targetState);

                await db
                  .update(schema.fixtures)
                  .set({
                    state: targetState,
                    homeScore,
                    awayScore,
                    homeScoreHt: details.score?.homeHt ?? localFix.homeScoreHt,
                    awayScoreHt: details.score?.awayHt ?? localFix.awayScoreHt,
                    finalizedAt: isFinished ? now : null,
                    lastPolledAt: now,
                    updatedAt: now,
                  })
                  .where(eq(schema.fixtures.id, localFix.id));

                summary.fixturesUpdated++;
                summary.hasChanges = true;
                continue;
              }
            } catch {
              // Bounded detail lookup failed; proceed to fallback below
            }
          }

          // Fallback when details are unavailable or not confirmed:
          if (elapsedSinceKickoffMs >= AcquisitionPolicyEngine.MAX_LIVE_MATCH_AGE_MS) {
            await db
              .update(schema.fixtures)
              .set({
                state: 'FINISHED',
                finalizedAt: now,
                lastPolledAt: now,
                updatedAt: now,
              })
              .where(eq(schema.fixtures.id, localFix.id));

            summary.fixturesUpdated++;
            summary.hasChanges = true;
          } else if (!detailsConfirmed) {
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
    try {
      // 1. Single date-wide fetch across all competitions for the target date
      const batch = await this.acquisitionService.acquireScheduled(date, competitionIds, cycleId);

      if (this.getProviderName() === 'fotmob' || this.provider.name === 'fotmob') {
        return await this.processBatch(batch, false);
      }

      // 2. Discover active monitored competitions via competition_provider_aliases
      const activeAliases = await db
        .select({
          providerId: schema.competitionProviderAliases.providerId,
          canonicalCompetitionId: schema.competitionProviderAliases.canonicalCompetitionId,
        })
        .from(schema.competitionProviderAliases)
        .where(eq(schema.competitionProviderAliases.provider, this.getProviderName()));

      const activeProviderIds = new Set(activeAliases.map((a) => a.providerId));

      let effectiveProviderIds = activeProviderIds;
      if (this.getProviderName() === 'api_football') {
        if (activeProviderIds.size === 0 && competitionIds && competitionIds.length > 0) {
          effectiveProviderIds = new Set(competitionIds.map(String));
        } else if (activeProviderIds.size > 0 && competitionIds && competitionIds.length > 0) {
          const requested = new Set(competitionIds.map(String));
          const intersected = new Set([...activeProviderIds].filter((id) => requested.has(id)));
          if (intersected.size > 0) {
            effectiveProviderIds = intersected;
          }
        }
      } else {
        if (activeProviderIds.size > 0 && competitionIds && competitionIds.length > 0) {
          const requested = new Set(competitionIds.map(String));
          const intersected = new Set([...activeProviderIds].filter((id) => requested.has(id)));
          if (intersected.size > 0) {
            effectiveProviderIds = intersected;
          }
        } else if (activeProviderIds.size === 0 && competitionIds && competitionIds.length > 0) {
          const batchCompIds = new Set(batch.competitions.map((c) => c.providerId || c.id));
          const matchingRequested = competitionIds.map(String).filter((id) => batchCompIds.has(id));
          if (matchingRequested.length > 0) {
            effectiveProviderIds = new Set(matchingRequested);
          }
        }
      }

      // 3. Filter the batch in-memory against active competitions
      if (effectiveProviderIds.size > 0) {
        const allowedCompIds = new Set<string>();
        for (const pId of effectiveProviderIds) {
          allowedCompIds.add(pId);
          allowedCompIds.add(`comp_${pId}`);
          allowedCompIds.add(`ft_comp_${pId}`);
        }
        for (const a of activeAliases) {
          if (a.canonicalCompetitionId) {
            allowedCompIds.add(a.canonicalCompetitionId);
          }
        }

        const compProviderMap = new Map<string, string>();
        for (const c of batch.competitions) {
          if (c.providerId) compProviderMap.set(c.id, c.providerId);
          if (c.canonicalId && c.providerId) compProviderMap.set(c.canonicalId, c.providerId);
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

        return await this.processBatch(filteredBatch, false);
      }

      return await this.processBatch(batch, false);
    } catch (error: any) {
      if (this.isRecoverableNetworkOrCircuitError(error)) {
        Logger.warn(
          'SyncService',
          'OfflineFallback',
          `Schedule discovery encountered upstream interruption (${error.message}). Serving cached state from SQLite without crashing.`,
          cycleId
        );
        return {
          provider: this.getProviderName(),
          fixturesDiscovered: 0,
          fixturesInserted: 0,
          fixturesUpdated: 0,
          eventsInserted: 0,
          eventsSkippedDuplicates: 0,
          hasChanges: false,
          cachedFallback: true,
        };
      }
      throw error;
    }
  }

  /**
   * Paced multi-day schedule synchronization across an array of date strings.
   * Fetches days sequentially with safe spacing (default 500ms) to prevent burst-spamming providers.
   */
  async syncScheduleDays(dates: string[], competitionIds?: number[], cycleId?: string, paceMs = 500): Promise<SyncSummary> {
    const combinedSummary: SyncSummary = {
      provider: this.getProviderName(),
      fixturesDiscovered: 0,
      fixturesInserted: 0,
      fixturesUpdated: 0,
      eventsInserted: 0,
      eventsSkippedDuplicates: 0,
      hasChanges: false,
    };

    for (let i = 0; i < dates.length; i++) {
      const date = dates[i];
      try {
        const daySummary = await this.syncSchedule(date, competitionIds, cycleId);
        if (daySummary) {
          combinedSummary.provider = daySummary.provider || combinedSummary.provider;
          combinedSummary.fixturesDiscovered += daySummary.fixturesDiscovered ?? 0;
          combinedSummary.fixturesInserted += daySummary.fixturesInserted ?? 0;
          combinedSummary.fixturesUpdated += daySummary.fixturesUpdated ?? 0;
          combinedSummary.eventsInserted += daySummary.eventsInserted ?? 0;
          combinedSummary.eventsSkippedDuplicates += daySummary.eventsSkippedDuplicates ?? 0;
          if (daySummary.hasChanges) {
            combinedSummary.hasChanges = true;
          }
        }
      } catch {
        // Continue across days fault-tolerantly
      }

      if (i < dates.length - 1 && paceMs > 0) {
        await new Promise((resolve) => setTimeout(resolve, paceMs));
      }
    }

    return combinedSummary;
  }

  /**
   * Performs a single date-range discovery query across the entire window (e.g. Yesterday to +5 days)
   * and filters in-memory using competition_provider_aliases. Exactly 1 quota request.
   */
  async syncScheduleRange(fromDate: string, toDate: string, competitionIds?: number[], cycleId?: string): Promise<SyncSummary> {
    const batch = await this.acquisitionService.acquireScheduledRange(fromDate, toDate, competitionIds, cycleId);

    if (this.getProviderName() === 'fotmob' || this.provider.name === 'fotmob') {
      return this.processBatch(batch, false);
    }

    const activeAliases = await db
      .select({
        providerId: schema.competitionProviderAliases.providerId,
        canonicalCompetitionId: schema.competitionProviderAliases.canonicalCompetitionId,
      })
      .from(schema.competitionProviderAliases)
      .where(eq(schema.competitionProviderAliases.provider, this.getProviderName()));

    const activeProviderIds = new Set(activeAliases.map((a) => a.providerId));

    let effectiveProviderIds = activeProviderIds;
    if (this.getProviderName() === 'api_football') {
      if (activeProviderIds.size === 0 && competitionIds && competitionIds.length > 0) {
        effectiveProviderIds = new Set(competitionIds.map(String));
      } else if (activeProviderIds.size > 0 && competitionIds && competitionIds.length > 0) {
        const requested = new Set(competitionIds.map(String));
        const intersected = new Set([...activeProviderIds].filter((id) => requested.has(id)));
        if (intersected.size > 0) {
          effectiveProviderIds = intersected;
        }
      }
    } else {
      if (activeProviderIds.size > 0 && competitionIds && competitionIds.length > 0) {
        const requested = new Set(competitionIds.map(String));
        const intersected = new Set([...activeProviderIds].filter((id) => requested.has(id)));
        if (intersected.size > 0) {
          effectiveProviderIds = intersected;
        }
      } else if (activeProviderIds.size === 0 && competitionIds && competitionIds.length > 0) {
        const batchCompIds = new Set(batch.competitions.map((c) => c.providerId || c.id));
        const matchingRequested = competitionIds.map(String).filter((id) => batchCompIds.has(id));
        if (matchingRequested.length > 0) {
          effectiveProviderIds = new Set(matchingRequested);
        }
      }
    }

    if (effectiveProviderIds.size > 0) {
      const allowedCompIds = new Set<string>();
      for (const pId of effectiveProviderIds) {
        allowedCompIds.add(pId);
        allowedCompIds.add(`comp_${pId}`);
        allowedCompIds.add(`ft_comp_${pId}`);
      }
      for (const a of activeAliases) {
        if (a.canonicalCompetitionId) {
          allowedCompIds.add(a.canonicalCompetitionId);
        }
      }

      const compProviderMap = new Map<string, string>();
      for (const c of batch.competitions) {
        if (c.providerId) compProviderMap.set(c.id, c.providerId);
        if (c.canonicalId && c.providerId) compProviderMap.set(c.canonicalId, c.providerId);
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
      provider: this.getProviderName(),
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
      isNull(schema.fixtures.finalizedAt),
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

      let detailsList: NormalizedMatchDetails[] = [];
      try {
        const fetched = await Promise.all(
          batchIds.map((id) => this.provider.getMatchDetails(id).catch(() => null))
        );
        detailsList = fetched.filter((d): d is NormalizedMatchDetails => d !== null);
      } catch (err) {
        Logger.warn('SyncService', 'BatchAcquisitionError', `Batch fixture details acquisition failed for batch of ${batchIds.length} fixtures: ${err}`, cycleId);
        // Touch lastPolledAt and finalizedAt on all candidates so they don't loop endlessly on persistent errors
        for (const candidate of batchCandidates) {
          await db
            .update(schema.fixtures)
            .set({ lastPolledAt: now, finalizedAt: now })
            .where(eq(schema.fixtures.id, candidate.id));
        }
        continue;
      }

      const detailsMap = new Map<string, NormalizedMatchDetails>();
      for (const d of detailsList) {
        detailsMap.set(d.providerMatchId, d);
      }

      // Reconcile each candidate with partial failure isolation
      for (const fix of batchCandidates) {
        try {
          const details = detailsMap.get(fix.id);
          if (details) {
            await db
              .update(schema.fixtures)
              .set({
                state: (details?.time?.status || details?.state || fix.state || "UNKNOWN"),
                elapsedMinute: (details?.time?.minute ?? details?.elapsedMinute ?? fix.elapsedMinute ?? 0),
                extraMinute: (details?.time?.addedTime ?? details?.extraMinute ?? fix.extraMinute ?? 0),
                homeScore: (details?.homeTeam?.score ?? details?.homeScore ?? fix.homeScore ?? 0),
                awayScore: (details?.awayTeam?.score ?? details?.awayScore ?? fix.awayScore ?? 0),
                homeScoreHt: fix.homeScoreHt,
                awayScoreHt: fix.awayScoreHt,
                liveSourceProvider: (this.provider as any).activeProviderName || this.provider.name,
                liveObservedAt: now,
                lastPolledAt: now,
                finalizedAt: now,
                updatedAt: now,
              })
              .where(eq(schema.fixtures.id, fix.id));

            summary.fixturesUpdated++;
            summary.hasChanges = true;

            if (false) { // TODO: Adapt events from NormalizedMatchDetails
            
              const incomingUids = (details?.events || []).map((e: any) => e.eventUid);
              const existingEvents = await db
                .select({ eventUid: schema.events.eventUid })
                .from(schema.events)
                .where(inArray(schema.events.eventUid, incomingUids));

              const existingUidSet = new Set(existingEvents.map((e) => e.eventUid));
              const newEvents = (details?.events || []).filter((e: any) => !existingUidSet.has(e.eventUid))
                .map(normalizeEvent);
              if (newEvents.length > 0) {
                await db.insert(schema.events).values(newEvents).onConflictDoNothing();
                summary.eventsInserted += newEvents.length;
                summary.hasChanges = true;
              }
            }

            if (false) { // TODO: Adapt lineups
              await this.upsertLineups(fix.id, details?.lineups || []);
            }
            if (false) { // TODO: Adapt stats
              await this.upsertFixtureStatistics(fix.id, details?.stats || []);
            }
          } else {
            // Missing from batch response: mark finalizedAt so it doesn't loop forever
            if (this.provider.name === 'fotmob') {
              // FotMob returns complete finished match state via schedule/live matches; mark finalized
              await db
                .update(schema.fixtures)
                .set({ lastPolledAt: now, finalizedAt: now })
                .where(eq(schema.fixtures.id, fix.id));
            } else {
              Logger.warn('SyncService', 'MissingBatchFixture', `Fixture ${fix.id} candidate for finalization was not returned in provider batch details response`, cycleId);
              await db
                .update(schema.fixtures)
                .set({ lastPolledAt: now, finalizedAt: now })
                .where(eq(schema.fixtures.id, fix.id));
            }
          }
        } catch (itemErr) {
          Logger.warn('SyncService', 'ReconciliationError', `Failed reconciling fixture ${fix.id} during batch finalization: ${itemErr}`, cycleId);
          await db
            .update(schema.fixtures)
            .set({ lastPolledAt: now, finalizedAt: now })
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
          provider: this.provider.name,
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
        provider: this.provider.name,
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

  /**
   * Persists match lineups and lineup players into PostgreSQL.
   */
  async upsertLineups(fixtureId: string, lineupsList: any[]): Promise<void> {
    if (!lineupsList || lineupsList.length === 0) return;

    for (const l of lineupsList) {
      if (!l.teamId) continue;
      const lineupId = `lineup_${fixtureId}_${l.teamId}`;

      await db.transaction(async (tx) => {
        // Ensure referenced team exists to satisfy foreign key constraint
        const teamExists = await tx.query.teams.findFirst({
          where: eq(schema.teams.id, l.teamId),
        });
        if (!teamExists) {
          await tx
            .insert(schema.teams)
            .values({
              id: l.teamId,
              canonicalId: generateCanonicalId('team'),
              name: `Team ${l.teamId}`,
              shortName: l.teamId.slice(0, 4).toUpperCase(),
            })
            .onConflictDoNothing();
        }

        // Upsert lineup header
        await tx
          .insert(schema.lineups)
          .values({
            id: lineupId,
            fixtureId,
            teamId: l.teamId,
            formation: l.formation ?? null,
            coachName: l.coachName ?? null,
            coachId: l.coachId ?? null,
          })
          .onConflictDoUpdate({
            target: [schema.lineups.fixtureId, schema.lineups.teamId],
            set: {
              formation: l.formation ?? null,
              coachName: l.coachName ?? null,
              coachId: l.coachId ?? null,
            },
          });

        // Refresh player rosters cleanly
        await tx.delete(schema.lineupPlayers).where(eq(schema.lineupPlayers.lineupId, lineupId));

        const allPlayers = [
          ...(l.startXi || []).map((p: any) => ({ ...p, isStarter: true })),
          ...(l.substitutes || []).map((p: any) => ({ ...p, isStarter: false })),
        ];

        if (allPlayers.length > 0) {
          const playerRows = allPlayers.map((p, idx) => ({
            id: `lp_${lineupId}_${p.id}_${idx}`,
            lineupId,
            playerId: p.id,
            name: p.name,
            number: p.number ?? null,
            position: p.position ?? null,
            grid: p.grid ?? null,
            isStarter: p.isStarter,
          }));

          await tx.insert(schema.lineupPlayers).values(playerRows);
        }
      });
    }
  }

  /**
   * Persists match statistics into PostgreSQL.
   */
  async upsertFixtureStatistics(fixtureId: string, statsList: any[]): Promise<void> {
    if (!statsList || statsList.length === 0) return;

    const now = new Date();
    for (const s of statsList) {
      if (!s.teamId) continue;
      const statId = `stat_${fixtureId}_${s.teamId}`;

      await db.transaction(async (tx) => {
        const teamExists = await tx.query.teams.findFirst({
          where: eq(schema.teams.id, s.teamId),
        });
        if (!teamExists) {
          await tx
            .insert(schema.teams)
            .values({
              id: s.teamId,
              canonicalId: generateCanonicalId('team'),
              name: `Team ${s.teamId}`,
              shortName: s.teamId.slice(0, 4).toUpperCase(),
            })
            .onConflictDoNothing();
        }

        await tx
          .insert(schema.fixtureStatistics)
          .values({
            id: statId,
            fixtureId,
            teamId: s.teamId,
            shotsOnGoal: s.shotsOnGoal ?? null,
            shotsOffGoal: s.shotsOffGoal ?? null,
            totalShots: s.totalShots ?? null,
            possessionPercent: s.possessionPercent ?? null,
            fouls: s.fouls ?? null,
            cornerKicks: s.cornerKicks ?? null,
            offsides: s.offsides ?? null,
            yellowCards: s.yellowCards ?? null,
            redCards: s.redCards ?? null,
            goalkeeperSaves: s.goalkeeperSaves ?? null,
            totalPasses: s.totalPasses ?? null,
            passesAccurate: s.passesAccurate ?? null,
            expectedGoals: s.expectedGoals ?? null,
            updatedAt: now,
          })
          .onConflictDoUpdate({
            target: [schema.fixtureStatistics.fixtureId, schema.fixtureStatistics.teamId],
            set: {
              shotsOnGoal: s.shotsOnGoal ?? null,
              shotsOffGoal: s.shotsOffGoal ?? null,
              totalShots: s.totalShots ?? null,
              possessionPercent: s.possessionPercent ?? null,
              fouls: s.fouls ?? null,
              cornerKicks: s.cornerKicks ?? null,
              offsides: s.offsides ?? null,
              yellowCards: s.yellowCards ?? null,
              redCards: s.redCards ?? null,
              goalkeeperSaves: s.goalkeeperSaves ?? null,
              totalPasses: s.totalPasses ?? null,
              passesAccurate: s.passesAccurate ?? null,
              expectedGoals: s.expectedGoals ?? null,
              updatedAt: now,
            },
          });
      });
    }
  }

  /**
   * Fetches bundled deep match details for a single fixture from the provider
   * and persists scores, events, lineups, and statistics into SQLite.
   */
  async syncFixtureDetails(fixtureId: string, cycleId?: string): Promise<boolean> {
    const trimmedId = fixtureId.trim();
    const dbFixture = await db.query.fixtures.findFirst({
      where: or(
        eq(schema.fixtures.id, trimmedId),
        eq(schema.fixtures.canonicalId, trimmedId)
      ),
    });
    if (!dbFixture) return false;

    let providerMatchId = dbFixture.id.replace(/^fix_/, '').replace(/^ft_fix_/, '');
    const alias = await db.query.fixtureProviderAliases.findFirst({
      where: or(
        eq(schema.fixtureProviderAliases.canonicalFixtureId, dbFixture.canonicalId),
        eq(schema.fixtureProviderAliases.canonicalFixtureId, dbFixture.id)
      ),
    });
    if (alias) {
      providerMatchId = alias.providerId;
    }

    const details = await this.provider.getMatchDetails(providerMatchId).catch((err) => {
      Logger.warn('SyncService', 'SyncFixtureDetails', `Failed fetching details for ${providerMatchId}: ${err?.message}`, cycleId);
      return null;
    });
    if (!details) return false;

    (details as any).cachedAt = Date.now();
    this.matchDetailsCache.set(providerMatchId, details);
    this.matchDetailsCache.set(dbFixture.id, details);
    if (dbFixture.canonicalId) {
      this.matchDetailsCache.set(dbFixture.canonicalId, details);
    }

    const now = new Date();
    await db.transaction(async (tx) => {
      await tx
        .update(schema.fixtures)
        .set({
          state: (details?.time?.status || dbFixture.state || "UNKNOWN"),
          elapsedMinute: (details?.time?.minute ?? dbFixture.elapsedMinute ?? 0),
          extraMinute: (details?.time?.addedTime ?? dbFixture.extraMinute ?? 0),
          homeScore: (details?.homeTeam?.score ?? dbFixture.homeScore ?? 0),
          awayScore: (details?.awayTeam?.score ?? dbFixture.awayScore ?? 0),
          venue: details.venue ?? dbFixture.venue,
          referee: details.referee ?? dbFixture.referee,
          lastPolledAt: now,
          updatedAt: now,
        })
        .where(eq(schema.fixtures.id, dbFixture.id));

      if (details && details.events && details.events.length > 0) {
        const mappedEvents = details.events.map((e: any) => ({
          ...e,
          id: e.id || `ev_${dbFixture.id}_${e.eventUid || Math.random().toString(36).slice(2)}`,
          fixtureId: dbFixture.id,
          teamId: (e.teamId === details.homeTeam.providerId ? dbFixture.homeTeamId : (e.teamId === details.awayTeam.providerId ? dbFixture.awayTeamId : (e.teamId || dbFixture.homeTeamId))),
        }));
        const incomingUids = mappedEvents.map((e: any) => e.eventUid);
        const existingEvents = await tx
          .select({ eventUid: schema.events.eventUid })
          .from(schema.events)
          .where(inArray(schema.events.eventUid, incomingUids));

        const existingUidSet = new Set(existingEvents.map((e) => e.eventUid));
        const newEvents = mappedEvents
          .filter((e: any) => !existingUidSet.has(e.eventUid))
          .map(normalizeEvent);

        if (newEvents.length > 0) {
          await tx.insert(schema.events).values(newEvents).onConflictDoNothing();
        }
      }
    });

    if (details.lineups && Array.isArray(details.lineups) && details.lineups.length > 0) {
      const mappedLineups = details.lineups.map((l: any) => ({
        ...l,
        teamId: (l.teamId === details.homeTeam.providerId ? dbFixture.homeTeamId : (l.teamId === details.awayTeam.providerId ? dbFixture.awayTeamId : l.teamId))
      }));
      await this.upsertLineups(dbFixture.id, mappedLineups);
    }

    if (details.stats && Array.isArray(details.stats) && details.stats.length > 0) {
      const mappedStats = details.stats.map((s: any) => ({
        ...s,
        teamId: (s.teamId === details.homeTeam.providerId ? dbFixture.homeTeamId : (s.teamId === details.awayTeam.providerId ? dbFixture.awayTeamId : s.teamId))
      }));
      await this.upsertFixtureStatistics(dbFixture.id, mappedStats);
    }

    await db
      .update(schema.fixtures)
      .set({ updatedAt: now })
      .where(eq(schema.fixtures.id, dbFixture.id));

    return true;
  }
}

