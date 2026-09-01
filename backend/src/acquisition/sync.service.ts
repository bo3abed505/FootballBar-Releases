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

    // 1. Upsert Competitions
    if (batch.competitions.length > 0) {
      await db.insert(schema.competitions).values(batch.competitions).onConflictDoNothing();
    }

    // 2. Upsert Seasons
    if (batch.seasons.length > 0) {
      await db.insert(schema.competitionSeasons).values(batch.seasons).onConflictDoNothing();
    }

    // 3. Upsert Teams
    if (batch.teams.length > 0) {
      await db.insert(schema.teams).values(batch.teams).onConflictDoNothing();
    }

    // 4. Upsert Fixtures with Change Detection
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
        // Check for meaningful score/state/minute changes
        const hasScoreChanged = existing.homeScore !== incoming.homeScore || existing.awayScore !== incoming.awayScore;
        const hasStateChanged = existing.state !== incoming.state;
        const hasMinuteChanged = existing.elapsedMinute !== incoming.elapsedMinute || existing.extraMinute !== incoming.extraMinute;
        const hasHtScoreChanged = existing.homeScoreHt !== incoming.homeScoreHt || existing.awayScoreHt !== incoming.awayScoreHt;

        if (hasScoreChanged || hasStateChanged || hasMinuteChanged || hasHtScoreChanged) {
          await db.update(schema.fixtures)
            .set({
              state: incoming.state,
              elapsedMinute: incoming.elapsedMinute,
              extraMinute: incoming.extraMinute,
              homeScore: incoming.homeScore,
              awayScore: incoming.awayScore,
              homeScoreHt: incoming.homeScoreHt,
              awayScoreHt: incoming.awayScoreHt,
              lastPolledAt: new Date(),
              updatedAt: new Date(),
            })
            .where(eq(schema.fixtures.id, incoming.id));

          summary.fixturesUpdated++;
          summary.hasChanges = true;
        } else {
          // Update lastPolledAt only without changing updatedAt version
          await db.update(schema.fixtures)
            .set({ lastPolledAt: new Date() })
            .where(eq(schema.fixtures.id, incoming.id));
        }
      }
    }

    // 5. Upsert Events with Duplicate Deduplication
    if (batch.events.length > 0) {
      const incomingUids = batch.events.map((e) => e.eventUid);
      const existingEvents = await db.select({ eventUid: schema.events.eventUid })
        .from(schema.events)
        .where(inArray(schema.events.eventUid, incomingUids));

      const existingUidSet = new Set(existingEvents.map((e) => e.eventUid));
      const newEvents = batch.events.filter((e) => !existingUidSet.has(e.eventUid));

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

