import { eq, desc, or, and, gte, lte, inArray, lt } from 'drizzle-orm';
import { db, schema } from '../db/index.js';
import { AcquisitionPolicyEngine, FixturesSummary } from '../acquisition/acquisition-policy.js';

export class FixturesRepository {
  async getAll(limit = 50) {
    return await db.query.fixtures.findMany({
      with: {
        competition: true,
        homeTeam: true,
        awayTeam: true,
        events: true,
      },
      orderBy: [desc(schema.fixtures.kickoffTime)],
      limit,
    });
  }

  async getById(id: string) {
    return await db.query.fixtures.findFirst({
      where: eq(schema.fixtures.id, id),
      with: {
        competition: true,
        season: true,
        homeTeam: true,
        awayTeam: true,
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

  async getTrayMatches(limit = 10) {
    // Return live matches first, then upcoming matches today, then recent finished
    return await db.query.fixtures.findMany({
      with: {
        competition: true,
        homeTeam: true,
        awayTeam: true,
        events: {
          orderBy: [desc(schema.events.minute), desc(schema.events.extraMinute)],
          limit: 5,
        },
      },
      orderBy: [desc(schema.fixtures.kickoffTime), desc(schema.fixtures.id)],
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
    return result.rowCount ?? 0;
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
    let scheduleKnownForToday = false;

    for (const f of candidateFixtures) {
      const kickoffStr = f.kickoffTime instanceof Date ? f.kickoffTime.toISOString() : String(f.kickoffTime);
      if (kickoffStr.slice(0, 10) === todayUtcPrefix) {
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
      }
    }

    return {
      liveCount,
      approachingCount,
      recentlyFinishedCount,
      stalePrematchCount,
      scheduleKnownForToday,
    };
  }
}

