import { describe, it, expect } from 'vitest';
import {
  normalizeCompetition,
  normalizeSeason,
  normalizeTeam,
  normalizeFixture,
} from '../src/normalization/fixture.js';
import { normalizeEvent } from '../src/normalization/event.js';
import { normalizeStandings } from '../src/normalization/standings.js';

describe('Normalization Layer', () => {
  it('normalizes competition DTO into canonical entity', () => {
    const canonical = normalizeCompetition({
      id: 'comp_laliga',
      name: 'La Liga',
      code: 'ESP_L1',
      type: 'league',
      country: 'Spain',
      logoUrl: 'https://media.api-sports.io/football/leagues/140.png',
    });

    expect(canonical.id).toBe('comp_laliga');
    expect(canonical.name).toBe('La Liga');
    expect(canonical.code).toBe('ESP_L1');
    expect(canonical.type).toBe('league');
  });

  it('normalizes season DTO into canonical entity with Date objects', () => {
    const canonical = normalizeSeason({
      id: 'season_laliga_2025',
      competitionId: 'comp_laliga',
      year: 2025,
      displayYear: '2025/2026',
      startDate: '2025-08-15T00:00:00.000Z',
      endDate: '2026-05-24T00:00:00.000Z',
      isCurrent: true,
    });

    expect(canonical.id).toBe('season_laliga_2025');
    expect(canonical.startDate).toBeInstanceOf(Date);
    expect(canonical.endDate).toBeInstanceOf(Date);
    expect(canonical.isCurrent).toBe(true);
  });

  it('normalizes team DTO into canonical entity', () => {
    const canonical = normalizeTeam({
      id: 'team_barcelona',
      name: 'FC Barcelona',
      shortName: 'BAR',
      code: 'FCB',
      country: 'Spain',
      logoUrl: 'https://example.com/barca.png',
      venueName: 'Camp Nou',
    });

    expect(canonical.id).toBe('team_barcelona');
    expect(canonical.shortName).toBe('BAR');
    expect(canonical.venueName).toBe('Camp Nou');
  });

  it('normalizes fixture DTO into canonical entity with score breakdowns', () => {
    const canonical = normalizeFixture({
      id: 'fix_123',
      competitionId: 'comp_laliga',
      seasonId: 'season_laliga_2025',
      homeTeamId: 'team_barcelona',
      awayTeamId: 'team_realmadrid',
      competition: { id: 'comp_laliga', name: 'La Liga' },
      season: { id: 'season_laliga_2025', competitionId: 'comp_laliga', year: 2025, displayYear: '2025/2026', startDate: '2025-08-15', endDate: '2026-05-24', isCurrent: true },
      homeTeam: { id: 'team_barcelona', name: 'FC Barcelona', shortName: 'BAR' },
      awayTeam: { id: 'team_realmadrid', name: 'Real Madrid', shortName: 'RMA' },
      round: 'Regular Season - 26',
      kickoffTime: '2026-03-01T20:00:00.000Z',
      state: 'live',
      elapsedMinute: 65,
      extraMinute: 0,
      score: { home: 2, away: 1, homeHt: 1, awayHt: 0 },
      venue: 'Camp Nou',
    });

    expect(canonical.id).toBe('fix_123');
    expect(canonical.state).toBe('LIVE');
    expect(canonical.homeScore).toBe(2);
    expect(canonical.awayScore).toBe(1);
    expect(canonical.homeScoreHt).toBe(1);
    expect(canonical.awayScoreHt).toBe(0);
    expect(canonical.kickoffTime).toBeInstanceOf(Date);
  });

  it('normalizes match event DTO with deterministic eventUid', () => {
    const canonical = normalizeEvent({
      id: 'evt_1',
      eventUid: 'evt_fix_123_75_0_Goal_ply10',
      fixtureId: 'fix_123',
      teamId: 'team_barcelona',
      type: 'Goal',
      minute: 75,
      playerName: 'L. Messi',
      playerId: 'ply_10',
      description: 'Goal from free kick',
    });

    expect(canonical.eventUid).toBe('evt_fix_123_75_0_Goal_ply10');
    expect(canonical.minute).toBe(75);
    expect(canonical.playerName).toBe('L. Messi');
  });

  it('normalizes standings rows with calculated goal difference', () => {
    const canonicalRows = normalizeStandings({
      competitionId: 'comp_laliga',
      seasonId: 'season_laliga_2025',
      rows: [
        { position: 1, teamId: 'team_barcelona', played: 20, won: 16, drawn: 2, lost: 2, goalsFor: 45, goalsAgainst: 15, points: 50 },
      ],
    });

    expect(canonicalRows.length).toBe(1);
    expect(canonicalRows[0].position).toBe(1);
    expect(canonicalRows[0].goalDifference).toBe(30);
    expect(canonicalRows[0].points).toBe(50);
  });
});
