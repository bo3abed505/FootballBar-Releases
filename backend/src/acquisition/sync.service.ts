import { inArray, eq, and, gte } from 'drizzle-orm';
import { db, schema } from '../db/index.js';
import { FootballProvider, FixtureFilter } from '../providers/football-provider.js';
import { AcquisitionService } from './acquisition.service.js';
import { normalizeEvent } from '../normalization/event.js';
import { AcquisitionPolicyEngine } from './acquisition-policy.js';

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
    const batch = await this.acquisitionService.acquireAll(filter);

    const summary: SyncSummary = {
      provider: this.provider.providerName,
      fixturesDiscovered: batch.fixtures.length,
      fixturesInserted: 0,
      fixturesUpdated: 0,
      eventsInserted: 0,
      eventsSkippedDuplicates: 0,
      hasChanges: false,
    };

    // 1. Upsert Competitions (Safe non-destructive merge)
    for (const comp of batch.competitions) {
      const existing = await db.query.competitions.findFirst({
        where: eq(schema.competitions.id, comp.id),
      });

      if (!existing) {
        await db.insert(schema.competitions).values(comp);
      } else {
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
          await db
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

    // 3. Upsert Teams (Safe non-destructive merge)
    for (const team of batch.teams) {
      const existing = await db.query.teams.findFirst({
        where: eq(schema.teams.id, team.id),
      });

      if (!existing) {
        await db.insert(schema.teams).values(team);
      } else {
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
          await db
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
    }

    // 4. Upsert Fixtures with Comprehensive Change Detection & Non-destructive Merge
    for (const incoming of batch.fixtures) {
      const existing = await db.query.fixtures.findFirst({
        where: eq(schema.fixtures.id, incoming.id),
      });

      if (!existing) {
        // New fixture insertion
        await db.insert(schema.fixtures).values(incoming);
        summary.fixturesInserted++;
        summary.hasChanges = true;
      } else {
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
          await db
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
          await db
            .update(schema.fixtures)
            .set({ lastPolledAt: new Date() })
            .where(eq(schema.fixtures.id, incoming.id));
        }
      }
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
    if (filter?.liveOnly) {
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
  async syncLive(competitionIds?: number[]): Promise<SyncSummary> {
    return this.sync({ liveOnly: true, competitionIds });
  }

  /**
   * Synchronizes daily match schedule for a specific date across target competitions.
   */
  async syncSchedule(date: string, competitionIds?: number[]): Promise<SyncSummary> {
    return this.sync({ date, competitionIds });
  }

  /**
   * Finalizes recently finished matches to capture final score and events.
   * Directly fetches fixture details for recently finished matches (bounded).
   */
  async finalizeRecentlyFinished(competitionIds?: (string | number)[]): Promise<SyncSummary> {
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

    // Bounded: fetch at most 5 recently finished fixtures per cycle, prioritizing unpolled fixtures
    const candidates = await db.query.fixtures.findMany({
      where: and(...whereConditions),
      orderBy: (fixtures, { asc, desc }) => [asc(fixtures.lastPolledAt), desc(fixtures.updatedAt)],
      limit: 5,
    });

    for (const fix of candidates) {
      try {
        const details = await this.provider.getFixtureDetails(fix.id);
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
          // If provider cannot find fixture or returns null, touch lastPolledAt so it ages out to FINAL
          await db
            .update(schema.fixtures)
            .set({ lastPolledAt: now })
            .where(eq(schema.fixtures.id, fix.id));
        }
      } catch {
        // Safe error handling: touch lastPolledAt so failed finalization doesn't spin forever
        await db
          .update(schema.fixtures)
          .set({ lastPolledAt: now })
          .where(eq(schema.fixtures.id, fix.id));
      }
    }

    return summary;
  }

  /**
   * Recovers stale prematch fixtures to check for postponement/cancellation.
   */
  async recoverStale(options?: { date?: string; competitionIds?: number[] } | number[]): Promise<SyncSummary> {
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
      const todayStr = new Date().toISOString().slice(0, 10);
      return this.sync({ date: todayStr, competitionIds: options });
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

    const todayStr = options?.date ?? new Date().toISOString().slice(0, 10);
    return this.sync({ date: todayStr, competitionIds: options?.competitionIds });
  }

  /**
   * Executes synchronization according to an AcquisitionPolicyEngine SyncPlan.
   * Returns SyncSummary if an external action was taken, or null if IDLE.
   */
  async syncWithPlan(
    plan: import('./acquisition-policy.js').SyncPlan,
    options?: { date?: string; competitionIds?: number[] }
  ): Promise<SyncSummary | null> {
    switch (plan.action) {
      case 'POLL_LIVE':
        return this.syncLive(options?.competitionIds);
      case 'DISCOVER_SCHEDULE':
        return this.syncSchedule(
          options?.date ?? new Date().toISOString().slice(0, 10),
          options?.competitionIds
        );
      case 'FINALIZE':
        return this.finalizeRecentlyFinished(options?.competitionIds);
      case 'RECOVER_STALE':
        return this.recoverStale(options);
      case 'IDLE':
      default:
        return null;
    }
  }
}

