import { eq, desc, or, and, gte, lte } from 'drizzle-orm';
import { db, schema } from '../db/index.js';

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
      orderBy: [desc(schema.fixtures.kickoffTime)],
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
}
