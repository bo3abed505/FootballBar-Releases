import { eq, inArray } from 'drizzle-orm';
import { db, schema } from '../db/index.js';

export class TeamsRepository {
  async getAll() {
    return await db.select().from(schema.teams);
  }

  async getById(id: string) {
    const results = await db.select().from(schema.teams).where(eq(schema.teams.id, id)).limit(1);
    return results[0] ?? null;
  }

  async getByIds(ids: string[]) {
    if (ids.length === 0) return [];
    return await db.select().from(schema.teams).where(inArray(schema.teams.id, ids));
  }

  async insert(team: typeof schema.teams.$inferInsert) {
    const results = await db.insert(schema.teams).values(team).onConflictDoNothing().returning();
    return results[0] ?? null;
  }
}
