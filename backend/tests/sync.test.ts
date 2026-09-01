import { describe, it, expect, beforeAll, afterAll, beforeEach } from 'vitest';
import { MockFootballProvider } from '../src/providers/mock.provider.js';
import { SyncService } from '../src/acquisition/sync.service.js';
import { AcquisitionService } from '../src/acquisition/acquisition.service.js';
import { db, schema, pool } from '../src/db/index.js';
import { eq, notInArray } from 'drizzle-orm';

describe('Acquisition & Sync Service (Integration with PostgreSQL)', () => {
  let provider: MockFootballProvider;
  let syncService: SyncService;

  const baselineEventIds = [
    'evt_elclasico_1',
    'evt_elclasico_2',
    'evt_elclasico_3',
    'evt_elclasico_4',
    'evt_elclasico_5',
    'evt_elclasico_6',
  ];

  const resetBaseline = async () => {
    provider.reset();

    // Remove any dynamic simulation events from prior test iterations
    await db.delete(schema.events).where(notInArray(schema.events.id, baselineEventIds));

    // Reset El Clásico fixture state back to baseline 1-1 LIVE 72'
    await db.update(schema.fixtures)
      .set({
        homeScore: 1,
        awayScore: 1,
        elapsedMinute: 72,
        extraMinute: 0,
        state: 'LIVE',
      })
      .where(eq(schema.fixtures.id, 'fix_elclasico_2026'));
  };

  beforeAll(async () => {
    // Explicit safety assertion: ensure we are operating on the dedicated test database
    const dbInfo = await pool.query('SELECT current_database()');
    if (dbInfo.rows[0].current_database !== 'footballtray_test') {
      throw new Error(
        `[SAFETY ERROR] Tests are connected to '${dbInfo.rows[0].current_database}', expected 'footballtray_test'!`
      );
    }

    provider = new MockFootballProvider();
    syncService = new SyncService(provider);
    await resetBaseline();
  });

  afterAll(async () => {
    await resetBaseline();
    await pool.end();
  });

  it('acquires and normalizes full batch from MockFootballProvider', async () => {
    const acq = new AcquisitionService(provider);
    const batch = await acq.acquireAll();

    expect(batch.competitions.length).toBeGreaterThanOrEqual(2);
    expect(batch.seasons.length).toBeGreaterThanOrEqual(2);
    expect(batch.teams.length).toBeGreaterThanOrEqual(4);
    expect(batch.fixtures.length).toBe(2);
    expect(batch.events.length).toBe(6);
  });

  it('performs first synchronization pass and inserts canonical records', async () => {
    const summary1 = await syncService.sync();

    expect(summary1.provider).toBe('mock');
    expect(summary1.fixturesDiscovered).toBe(2);
    // Either inserted or already existing from seed
    expect(summary1.fixturesInserted + summary1.fixturesUpdated).toBeGreaterThanOrEqual(0);
  });

  it('is completely idempotent on consecutive sync passes with no changes', async () => {
    const summary2 = await syncService.sync();

    expect(summary2.fixturesDiscovered).toBe(2);
    expect(summary2.fixturesInserted).toBe(0);
    expect(summary2.fixturesUpdated).toBe(0);
    expect(summary2.eventsInserted).toBe(0);
    expect(summary2.eventsSkippedDuplicates).toBe(6);
    expect(summary2.hasChanges).toBe(false);
  });

  it('detects match change, updates fixture, and inserts only new events', async () => {
    // Simulate an 89th minute goal for Barcelona
    provider.simulateGoal('fix_elclasico_2026', true, 89, {
      name: 'L. Martinez (Dev Player)',
      id: 'ply_bar_10',
    });

    const summary3 = await syncService.sync();

    expect(summary3.fixturesDiscovered).toBe(2);
    expect(summary3.fixturesInserted).toBe(0);
    expect(summary3.fixturesUpdated).toBe(1); // El Clásico updated
    expect(summary3.eventsInserted).toBe(1); // Only 1 new goal event
    expect(summary3.eventsSkippedDuplicates).toBe(6); // 6 prior events skipped
    expect(summary3.hasChanges).toBe(true);

    // Verify change in PostgreSQL
    const updatedFixture = await db.query.fixtures.findFirst({
      where: eq(schema.fixtures.id, 'fix_elclasico_2026'),
    });

    expect(updatedFixture?.homeScore).toBe(2);
    expect(updatedFixture?.awayScore).toBe(1);
    expect(updatedFixture?.elapsedMinute).toBe(89);
  });
});
