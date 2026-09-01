import { describe, it, expect, beforeEach } from 'vitest';
import { MockFootballProvider } from '../src/providers/mock.provider.js';

describe('MockFootballProvider', () => {
  let provider: MockFootballProvider;

  beforeEach(() => {
    provider = new MockFootballProvider();
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

  it('simulates a new goal dynamically', async () => {
    provider.simulateGoal('fix_elclasico_2026', true, 88, { name: 'P. Gonzalez', id: 'ply_bar_8' });
    const details = await provider.getFixtureDetails('fix_elclasico_2026');

    expect(details?.score.home).toBe(2);
    expect(details?.score.away).toBe(1);
    expect(details?.elapsedMinute).toBe(88);
    expect(details?.events.length).toBe(7);
    expect(details?.events[6].type).toBe('Goal');
    expect(details?.events[6].minute).toBe(88);
    expect(details?.events[6].playerName).toBe('P. Gonzalez');
  });
});
