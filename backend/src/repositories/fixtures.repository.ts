import { eq, desc, or, and, gte, lte, inArray, lt, like, asc, sql } from 'drizzle-orm';
import { db, schema } from '../db/index.js';
import { AcquisitionPolicyEngine, FixturesSummary } from '../acquisition/acquisition-policy.js';

export class FixturesRepository {
  async getAll(limit = 50) {
    return await db.query.fixtures.findMany({
      columns: {
        canonicalId: false,
      },
      with: {
        competition: {
          columns: {
            canonicalId: false,
          },
        },
        homeTeam: {
          columns: {
            canonicalId: false,
          },
        },
        awayTeam: {
          columns: {
            canonicalId: false,
          },
        },
        events: true,
      },
      orderBy: [desc(schema.fixtures.kickoffTime)],
      limit,
    });
  }

  async getById(id: string) {
    return await db.query.fixtures.findFirst({
      where: eq(schema.fixtures.id, id),
      columns: {
        canonicalId: false,
      },
      with: {
        competition: {
          columns: {
            canonicalId: false,
          },
        },
        season: true,
        homeTeam: {
          columns: {
            canonicalId: false,
          },
        },
        awayTeam: {
          columns: {
            canonicalId: false,
          },
        },
        events: true,
        lineups: {
          with: {
            players: true,
          },
        },
        statistics: true,
      },
    });
  }

  async getTrayMatches(limit = 10, now: Date = new Date()) {
    const nowMs = now.getTime();

    const LIVE_STATES = ['LIVE', 'HALFTIME', '1H', '2H', 'ET', 'P', 'BT', 'IN_PLAY'];
    const PREMATCH_STATES = ['PREMATCH', 'NS', 'TBD'];
    const FINISHED_STATES = ['FINISHED', 'FT', 'AET', 'PEN', 'POSTMATCH'];

    const livePastCutoff = new Date(nowMs - AcquisitionPolicyEngine.MAX_LIVE_MATCH_AGE_MS);
    const liveFutureCutoff = new Date(nowMs + AcquisitionPolicyEngine.APPROACHING_WINDOW_MS);
    const prematchPastCutoff = new Date(nowMs - AcquisitionPolicyEngine.APPROACHING_WINDOW_MS);
    const prematchFutureCutoff = new Date(nowMs + 24 * 60 * 60 * 1000);
    const finishedPastCutoff = new Date(nowMs - 4 * 60 * 60 * 1000);
    const finishedFutureCutoff = now;

    // Return live matches first, then upcoming matches within 24 hours, then recent finished matches.
    return await db.query.fixtures.findMany({
      where: or(
        and(
          inArray(schema.fixtures.state, LIVE_STATES),
          gte(schema.fixtures.kickoffTime, livePastCutoff),
          lte(schema.fixtures.kickoffTime, liveFutureCutoff)
        ),
        and(
          inArray(schema.fixtures.state, PREMATCH_STATES),
          gte(schema.fixtures.kickoffTime, prematchPastCutoff),
          lte(schema.fixtures.kickoffTime, prematchFutureCutoff)
        ),
        and(
          inArray(schema.fixtures.state, FINISHED_STATES),
          gte(schema.fixtures.kickoffTime, finishedPastCutoff),
          lte(schema.fixtures.kickoffTime, finishedFutureCutoff)
        )
      ),
      with: {
        competition: true,
        homeTeam: true,
        awayTeam: true,
        events: {
          orderBy: [desc(schema.events.minute), desc(schema.events.extraMinute)],
          limit: 5,
        },
      },
      orderBy: [
        sql`CASE
          WHEN ${schema.fixtures.state} IN ('LIVE', 'HALFTIME', '1H', '2H', 'ET', 'P', 'BT', 'IN_PLAY') THEN 0
          WHEN ${schema.fixtures.state} IN ('PREMATCH', 'NS', 'TBD') THEN 1
          WHEN ${schema.fixtures.state} IN ('FINISHED', 'FT', 'AET', 'PEN', 'POSTMATCH') THEN 2
          ELSE 3
        END ASC`,
        sql`CASE
          WHEN ${schema.fixtures.state} IN ('LIVE', 'HALFTIME', '1H', '2H', 'ET', 'P', 'BT', 'IN_PLAY', 'PREMATCH', 'NS', 'TBD')
            THEN ${schema.fixtures.kickoffTime}
        END ASC NULLS LAST`,
        sql`CASE
          WHEN ${schema.fixtures.state} IN ('FINISHED', 'FT', 'AET', 'PEN', 'POSTMATCH')
            THEN ${schema.fixtures.kickoffTime}
        END DESC NULLS LAST`,
        asc(schema.fixtures.id),
      ],
      limit,
    });
  }

  async insert(fixture: typeof schema.fixtures.$inferInsert) {
    const results = await db.insert(schema.fixtures).values(fixture).onConflictDoNothing().returning();
    return results[0] ?? null;
  }

  async updateState(id: string, state: string, elapsedMinute: number, homeScore: number, awayScore: number) {
    const results = await db.update(schema.fixtures)
      .set({
        state,
        elapsedMinute,
        homeScore,
        awayScore,
        updatedAt: new Date(),
      })
      .where(eq(schema.fixtures.id, id))
      .returning();
    return results[0] ?? null;
  }

  /**
   * Reconciles expired LIVE fixtures locally without external provider calls.
   * Any fixture remaining in an active live state beyond MAX_LIVE_MATCH_AGE_MS (3.5h)
   * has conclusively finished play and is transitioned to FINISHED.
   */
  async reconcileExpiredLiveFixtures(now: Date = new Date()): Promise<number> {
    const cutoff = new Date(now.getTime() - AcquisitionPolicyEngine.MAX_LIVE_MATCH_AGE_MS);
    const result = await db
      .update(schema.fixtures)
      .set({
        state: 'FINISHED',
        updatedAt: now,
      })
      .where(
        and(
          inArray(schema.fixtures.state, ['LIVE', 'HALFTIME', '1H', '2H', 'ET', 'P', 'BT', 'IN_PLAY']),
          lt(schema.fixtures.kickoffTime, cutoff)
        )
      );

    // Also sweep ancient PREMATCH fixtures past match day (> 24h past kickoff) locally to POSTPONED
    const ancientPrematchCutoff = new Date(now.getTime() - 24 * 60 * 60 * 1000);
    await db
      .update(schema.fixtures)
      .set({
        state: 'POSTPONED',
        updatedAt: now,
      })
      .where(
        and(
          inArray(schema.fixtures.state, ['PREMATCH', 'NS', 'TBD']),
          lt(schema.fixtures.kickoffTime, ancientPrematchCutoff)
        )
      );

    return result.rowCount ?? 0;
  }

  // Tracks dates for which schedule discovery has been executed
  private discoveredDates = new Set<string>();
  private invalidatedDates = new Set<string>();

  /**
   * Marks schedule discovery as completed for a given UTC date string (YYYY-MM-DD).
   * Persists a durable discovery record in api_request_ledger (0 cost units, provider='system').
   */
  async markScheduleDiscovered(dateStr: string, now: Date = new Date(), cycleId?: string): Promise<void> {
    this.invalidatedDates.delete(dateStr);
    this.discoveredDates.add(dateStr);

    try {
      await db.insert(schema.apiRequestLedger).values({
        id: `disc_${dateStr}_${Date.now()}_${Math.random().toString(36).substring(2, 7)}`,
        cycleId: cycleId ?? null,
        provider: 'system',
        endpoint: `/schedule?date=${dateStr}`,
        operation: 'DISCOVER_SCHEDULE',
        priority: 'NORMAL',
        success: true,
        httpStatus: 200,
        responseTimeMs: 0,
        costUnits: 0,
        requestedAt: now,
      }).onConflictDoNothing();
    } catch {
      // Fallback gracefully if ledger write fails
    }
  }

  /**
   * Explicitly invalidates schedule discovery state for a date (or all dates).
   * Persists a durable invalidation marker (0 cost units, provider='system', operation='INVALIDATE_SCHEDULE').
   * Forces next scheduler cycle to perform a discovery cycle even across restarts,
   * while completely preserving immutable historical provider audit logs.
   */
  async invalidateSchedule(dateStr?: string, now: Date = new Date(), cycleId?: string): Promise<void> {
    const targetDate = dateStr ?? now.toISOString().slice(0, 10);
    this.discoveredDates.delete(targetDate);
    this.invalidatedDates.add(targetDate);

    try {
      await db.insert(schema.apiRequestLedger).values({
        id: `inval_${targetDate}_${Date.now()}_${Math.random().toString(36).substring(2, 7)}`,
        cycleId: cycleId ?? null,
        provider: 'system',
        endpoint: `/schedule?date=${targetDate}`,
        operation: 'INVALIDATE_SCHEDULE',
        priority: 'NORMAL',
        success: true,
        httpStatus: 200,
        responseTimeMs: 0,
        costUnits: 0,
        requestedAt: now,
        errorMessage: 'SCHEDULE_INVALIDATED',
      }).onConflictDoNothing();
    } catch {
      // Fallback gracefully if database insert fails
    }
  }

  /**
   * Returns true if schedule discovery has been executed for the given date and not invalidated.
   */
  isScheduleDiscovered(dateStr: string): boolean {
    if (this.invalidatedDates.has(dateStr)) return false;
    return this.discoveredDates.has(dateStr);
  }

  /**
   * Resolves the authoritative discovery state for a date by checking in-memory cache
   * and persistent ledger markers (cleanly ordering DISCOVER_SCHEDULE vs INVALIDATE_SCHEDULE).
   */
  async resolveScheduleState(dateStr: string, now: Date = new Date()): Promise<'DISCOVERED' | 'INVALIDATED' | 'UNKNOWN'> {
    if (this.invalidatedDates.has(dateStr)) return 'INVALIDATED';
    if (this.discoveredDates.has(dateStr)) return 'DISCOVERED';

    try {
      const startOfDay = new Date(dateStr + 'T00:00:00.000Z');
      const nextDay = new Date(startOfDay.getTime() + 24 * 60 * 60 * 1000);

      // Check for durable invalidation marker for dateStr
      const latestInvalidation = await db.query.apiRequestLedger.findFirst({
        where: and(
          eq(schema.apiRequestLedger.provider, 'system'),
          eq(schema.apiRequestLedger.operation, 'INVALIDATE_SCHEDULE'),
          gte(schema.apiRequestLedger.requestedAt, startOfDay),
          lt(schema.apiRequestLedger.requestedAt, nextDay)
        ),
        orderBy: [desc(schema.apiRequestLedger.requestedAt)],
      });

      // Check for latest successful discovery evidence for dateStr
      const latestDiscovery = await db.query.apiRequestLedger.findFirst({
        where: and(
          eq(schema.apiRequestLedger.success, true),
          gte(schema.apiRequestLedger.requestedAt, startOfDay),
          lt(schema.apiRequestLedger.requestedAt, nextDay),
          or(
            and(
              eq(schema.apiRequestLedger.provider, 'system'),
              eq(schema.apiRequestLedger.operation, 'DISCOVER_SCHEDULE')
            ),
            and(
              eq(schema.apiRequestLedger.provider, 'api_football'),
              eq(schema.apiRequestLedger.operation, 'get_fixtures'),
              like(schema.apiRequestLedger.endpoint, `%date=${dateStr}%`)
            )
          )
        ),
        orderBy: [desc(schema.apiRequestLedger.requestedAt)],
      });

      const invalidationTime = latestInvalidation
        ? (latestInvalidation.requestedAt instanceof Date
            ? latestInvalidation.requestedAt.getTime()
            : new Date(latestInvalidation.requestedAt).getTime())
        : null;

      const discoveryTime = latestDiscovery
        ? (latestDiscovery.requestedAt instanceof Date
            ? latestDiscovery.requestedAt.getTime()
            : new Date(latestDiscovery.requestedAt).getTime())
        : null;

      if (invalidationTime !== null && (discoveryTime === null || invalidationTime >= discoveryTime)) {
        this.invalidatedDates.add(dateStr);
        return 'INVALIDATED';
      }

      if (discoveryTime !== null && (invalidationTime === null || discoveryTime > invalidationTime)) {
        this.discoveredDates.add(dateStr);
        if (latestDiscovery?.operation !== 'DISCOVER_SCHEDULE') {
          await this.markScheduleDiscovered(dateStr, now);
        }
        return 'DISCOVERED';
      }
    } catch {
      // Fallback gracefully if database query fails
    }

    return 'UNKNOWN';
  }

  /**
   * Retrieves active summary counts (live, approaching, recently finished, stale prematch)
   * by evaluating candidate database fixtures through AcquisitionPolicyEngine.
   */
  async getActiveSummary(now: Date = new Date()): Promise<FixturesSummary> {
    // Automatically sweep and clean up any ancient LIVE fixtures without external calls
    await this.reconcileExpiredLiveFixtures(now);

    const candidateFixtures = await db.query.fixtures.findMany({
      where: or(
        and(
          inArray(schema.fixtures.state, ['LIVE', 'HALFTIME', '1H', '2H', 'ET', 'P', 'BT', 'IN_PLAY']),
          gte(schema.fixtures.kickoffTime, new Date(now.getTime() - AcquisitionPolicyEngine.MAX_LIVE_MATCH_AGE_MS)),
          lte(schema.fixtures.kickoffTime, new Date(now.getTime() + AcquisitionPolicyEngine.APPROACHING_WINDOW_MS))
        ),
        and(
          inArray(schema.fixtures.state, ['PREMATCH', 'NS', 'TBD']),
          gte(schema.fixtures.kickoffTime, new Date(now.getTime() - 24 * 60 * 60 * 1000)),
          lte(schema.fixtures.kickoffTime, new Date(now.getTime() + 24 * 60 * 60 * 1000))
        ),
        and(
          inArray(schema.fixtures.state, ['FINISHED', 'FT', 'AET', 'PEN', 'POSTMATCH']),
          gte(schema.fixtures.kickoffTime, new Date(now.getTime() - 4 * 60 * 60 * 1000))
        )
      ),
      limit: 200,
    });

    const todayUtcPrefix = now.toISOString().slice(0, 10);
    let liveCount = 0;
    let approachingCount = 0;
    let recentlyFinishedCount = 0;
    let stalePrematchCount = 0;
    const staleCompetitionIds: number[] = [];

    // Authoritative schedule resolution (persists across restarts)
    const scheduleState = await this.resolveScheduleState(todayUtcPrefix, now);
    let scheduleKnownForToday = scheduleState === 'DISCOVERED';

    for (const f of candidateFixtures) {
      const kickoffStr = f.kickoffTime instanceof Date ? f.kickoffTime.toISOString() : String(f.kickoffTime);
      // Only recognize schedule from existing DB fixtures if today's date was not explicitly invalidated
      if (kickoffStr.slice(0, 10) === todayUtcPrefix && scheduleState !== 'INVALIDATED') {
        scheduleKnownForToday = true;
      }

      const classification = AcquisitionPolicyEngine.classifyFixtureState({
        state: f.state,
        kickoffTime: f.kickoffTime,
        lastPolledAt: f.lastPolledAt,
        updatedAt: f.updatedAt,
      }, now);

      if (classification === 'LIVE_WINDOW') {
        liveCount++;
      } else if (classification === 'APPROACHING') {
        approachingCount++;
      } else if (classification === 'RECENTLY_FINISHED') {
        recentlyFinishedCount++;
      } else if (classification === 'STALE_PREMATCH') {
        stalePrematchCount++;
        if (f.competitionId) {
          const compNum = parseInt(f.competitionId.replace(/^comp_/, ''), 10);
          if (!isNaN(compNum)) {
            staleCompetitionIds.push(compNum);
          }
        }
      }
    }

    // If fixtures existed today but no ledger record existed yet, persist discovery marker
    if (scheduleKnownForToday && scheduleState === 'UNKNOWN') {
      await this.markScheduleDiscovered(todayUtcPrefix, now);
    }

    return {
      liveCount,
      approachingCount,
      recentlyFinishedCount,
      stalePrematchCount,
      staleCompetitionIds: Array.from(new Set(staleCompetitionIds)),
      scheduleKnownForToday,
    };
  }
}

