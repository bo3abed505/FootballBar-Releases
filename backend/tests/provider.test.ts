import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import { MockFootballProvider } from '../src/providers/mock.provider.js';

describe('MockFootballProvider', () => {
  const tempStateFile = path.resolve(process.cwd(), '.mock-state-test.json');
  let provider: MockFootballProvider;

  beforeEach(() => {
    if (fs.existsSync(tempStateFile)) {
      fs.unlinkSync(tempStateFile);
    }
    provider = new MockFootballProvider(tempStateFile);
  });

  afterEach(() => {
    if (fs.existsSync(tempStateFile)) {
      fs.unlinkSync(tempStateFile);
    }
  });

  it('provides deterministic competitions and seasons', async () => {
    const competitions = await provider.getCompetitions();
    expect(competitions.length).toBeGreaterThanOrEqual(2);
    expect(competitions.some((c) => c.id === 'comp_laliga')).toBe(true);
    expect(competitions.some((c) => c.id === 'comp_international')).toBe(true);
  });

  it('provides deterministic development fixtures including El Clásico and Spain vs Egypt', async () => {
    const fixtures = await provider.getFixtures();
    expect(fixtures.length).toBe(2);

    const elClasico = fixtures.find((f) => f.id === 'fix_elclasico_2026');
    expect(elClasico).toBeDefined();
    expect(elClasico?.state).toBe('LIVE');
    expect(elClasico?.elapsedMinute).toBe(72);
    expect(elClasico?.score.home).toBe(1);
    expect(elClasico?.score.away).toBe(1);
    expect(elClasico?.homeTeam.shortName).toBe('BAR');
    expect(elClasico?.awayTeam.shortName).toBe('RMA');

    const spainEgypt = fixtures.find((f) => f.id === 'fix_spain_egypt_2026');
    expect(spainEgypt).toBeDefined();
    expect(spainEgypt?.state).toBe('PREMATCH');
    expect(spainEgypt?.score.home).toBe(0);
    expect(spainEgypt?.score.away).toBe(0);
  });

  it('returns fixture details with chronological match events', async () => {
    const details = await provider.getFixtureDetails('fix_elclasico_2026');
    expect(details).not.toBeNull();
    expect(details?.events.length).toBe(6);
    expect(details?.events[0].type).toBe('PeriodStart');
    expect(details?.events[1].type).toBe('Goal');
    expect(details?.events[1].playerName).toContain('Martinez');
  });

  it('simulates a new goal dynamically and persists state to file in development', async () => {
    const applied = provider.simulateGoal('fix_elclasico_2026', true, 88, { name: 'P. Gonzalez', id: 'ply_bar_8' });
    expect(applied).toBe(true);

    const details = await provider.getFixtureDetails('fix_elclasico_2026');
    expect(details?.score.home).toBe(2);
    expect(details?.score.away).toBe(1);
    expect(details?.elapsedMinute).toBe(88);
    expect(details?.events.length).toBe(7);

    // Verify file was written
    expect(fs.existsSync(tempStateFile)).toBe(true);

    // Verify a fresh MockFootballProvider instance in a new "process" loads the persisted state
    const newInstance = new MockFootballProvider(tempStateFile);
    const loadedDetails = await newInstance.getFixtureDetails('fix_elclasico_2026');
    expect(loadedDetails?.score.home).toBe(2);
    expect(loadedDetails?.score.away).toBe(1);
    expect(loadedDetails?.elapsedMinute).toBe(88);
    expect(loadedDetails?.events.length).toBe(7);
  });

  it('is strictly idempotent when simulateGoal is called repeatedly with same parameters', async () => {
    const firstCall = provider.simulateGoal('fix_elclasico_2026', true, 89, { name: 'L. Martinez', id: 'ply_bar_10' });
    expect(firstCall).toBe(true);

    // Second call with same event UID must return false and not re-increment score
    const secondCall = provider.simulateGoal('fix_elclasico_2026', true, 89, { name: 'L. Martinez', id: 'ply_bar_10' });
    expect(secondCall).toBe(false);

    const details = await provider.getFixtureDetails('fix_elclasico_2026');
    expect(details?.score.home).toBe(2); // Still 2, not 3
    expect(details?.events.length).toBe(7); // Still 7, not 8
  });

  it('removes persistent state file when reset() is called', () => {
    provider.simulateGoal('fix_elclasico_2026', true, 89, { name: 'L. Martinez', id: 'ply_bar_10' });
    expect(fs.existsSync(tempStateFile)).toBe(true);

    provider.clearPersistentState();
    expect(fs.existsSync(tempStateFile)).toBe(false);
  });

  it('never reads or writes persistent mock state when NODE_ENV=production', () => {
    const originalEnv = process.env.NODE_ENV;
    try {
      process.env.NODE_ENV = 'production';

      const prodProvider = new MockFootballProvider(tempStateFile);
      prodProvider.simulateGoal('fix_elclasico_2026', true, 89, { name: 'L. Martinez', id: 'ply_bar_10' });

      // In production mode, file must NEVER be created
      expect(fs.existsSync(tempStateFile)).toBe(false);
    } finally {
      process.env.NODE_ENV = originalEnv;
    }
  });
});
