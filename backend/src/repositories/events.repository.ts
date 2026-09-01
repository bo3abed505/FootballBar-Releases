import { eq, desc } from 'drizzle-orm';
import { db, schema } from '../db/index.js';

export class EventsRepository {
  async getByFixtureId(fixtureId: string) {
    return await db.query.events.findMany({
      where: eq(schema.events.fixtureId, fixtureId),
      orderBy: [schema.events.minute, schema.events.extraMinute],
    });
  }

  async insert(event: typeof schema.events.$inferInsert) {
    const results = await db.insert(schema.events).values(event).onConflictDoNothing().returning();
    return results[0] ?? null;
  }

  async insertBatch(eventList: (typeof schema.events.$inferInsert)[]) {
    if (eventList.length === 0) return [];
    return await db.insert(schema.events).values(eventList).onConflictDoNothing().returning();
  }
}
