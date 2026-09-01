import { inArray, eq } from 'drizzle-orm';
import { db, schema } from '../db/index.js';
import { FootballProvider, FixtureFilter } from '../providers/football-provider.js';
import { AcquisitionService } from './acquisition.service.js';

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
   */
  async finalizeRecentlyFinished(competitionIds?: number[]): Promise<SyncSummary> {
    return this.sync({ liveOnly: true, competitionIds });
  }

  /**
   * Recovers stale prematch fixtures to check for postponement/cancellation.
   */
  async recoverStale(competitionIds?: number[]): Promise<SyncSummary> {
    return this.sync({ competitionIds });
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
        return this.recoverStale(options?.competitionIds);
      case 'IDLE':
      default:
        return null;
    }
  }
}

