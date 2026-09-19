import { eq } from 'drizzle-orm';
import { db, schema } from '../db/index.js';
import { generateCanonicalId } from '../db/canonical-id.js';

export class CompetitionsRepository {
  async getAll() {
    return await db.select().from(schema.competitions);
  }

  async getById(id: string) {
    const results = await db.select().from(schema.competitions).where(eq(schema.competitions.id, id)).limit(1);
    return results[0] ?? null;
  }

  async insert(competition: typeof schema.competitions.$inferInsert) {
    const canonicalId = competition.canonicalId || generateCanonicalId('comp');
    const results = await db.insert(schema.competitions).values({ ...competition, canonicalId }).onConflictDoNothing().returning();
    return results[0] ?? null;
  }
}
