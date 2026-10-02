import * as fs from 'fs';
import * as path from 'path';
import {
  FootballProvider,
  ProviderCompetition,
  ProviderSeason,
  ProviderTeam,
  ProviderFixture,
  ProviderFixtureDetail,
  ProviderEvent,
  ProviderStanding,
  FixtureFilter,
} from './football-provider.js';

interface PersistentFixtureState {
  state: string;
  elapsedMinute: number;
  extraMinute?: number;
  score: {
    home: number;
    away: number;
    homeHt?: number;
    awayHt?: number;
  };
  events: ProviderEvent[];
}

interface PersistentMockState {
  fixtures: Record<string, PersistentFixtureState>;
}

export class MockFootballProvider implements FootballProvider {
  readonly providerName = 'mock';

  private readonly stateFilePath: string | null;

  // Seeded Teams
  private readonly teamBarcelona: ProviderTeam = {
    id: 'team_barcelona',
    name: 'FC Barcelona',
    shortName: 'BAR',
    code: 'FCB',
    country: 'Spain',
    logoUrl: 'https://media.api-sports.io/football/teams/529.png',
    venueName: 'Spotify Camp Nou',
  };

  private readonly teamRealMadrid: ProviderTeam = {
    id: 'team_realmadrid',
    name: 'Real Madrid',
    shortName: 'RMA',
    code: 'RMA',
    country: 'Spain',
    logoUrl: 'https://media.api-sports.io/football/teams/541.png',
    venueName: 'Santiago Bernabéu',
  };

  private readonly teamSpain: ProviderTeam = {
    id: 'team_spain',
    name: 'Spain',
    shortName: 'ESP',
    code: 'ESP',
    country: 'Spain',
    logoUrl: 'https://media.api-sports.io/football/teams/9.png',
    venueName: 'Estadio Metropolitano',
  };

  private readonly teamEgypt: ProviderTeam = {
    id: 'team_egypt',
    name: 'Egypt',
    shortName: 'EGY',
    code: 'EGY',
    country: 'Egypt',
    logoUrl: 'https://media.api-sports.io/football/teams/32.png',
    venueName: 'Cairo International Stadium',
  };

  // Seeded Competitions
  private readonly compLaLiga: ProviderCompetition = {
    id: 'comp_laliga',
    name: 'La Liga',
    code: 'ESP_L1',
    type: 'league',
    country: 'Spain',
    logoUrl: 'https://media.api-sports.io/football/leagues/140.png',
  };

  private readonly compInternational: ProviderCompetition = {
    id: 'comp_international',
    name: 'International Friendly',
    code: 'INT_FRN',
    type: 'cup',
    country: 'World',
    logoUrl: 'https://media.api-sports.io/football/leagues/10.png',
  };

  // Seeded Seasons
  private readonly seasonLaLiga: ProviderSeason = {
    id: 'season_laliga_2025',
    competitionId: 'comp_laliga',
    year: 2025,
    displayYear: '2025/2026',
    startDate: '2025-08-15T00:00:00.000Z',
    endDate: '2026-05-24T00:00:00.000Z',
    isCurrent: true,
  };

  private readonly seasonInternational: ProviderSeason = {
    id: 'season_international_2025',
    competitionId: 'comp_international',
    year: 2025,
    displayYear: '2025/2026',
    startDate: '2025-01-01T00:00:00.000Z',
    endDate: '2025-12-31T00:00:00.000Z',
    isCurrent: true,
  };

  // Mutable state for dynamic test simulation
  private fixtures: Map<string, ProviderFixtureDetail> = new Map();

  constructor(customStateFilePath?: string) {
    if (process.env.NODE_ENV === 'production') {
      this.stateFilePath = null;
    } else if (customStateFilePath) {
      this.stateFilePath = customStateFilePath;
    } else if (process.env.NODE_ENV === 'test') {
      // In automated tests without an explicit customStateFilePath, isolate in-memory
      this.stateFilePath = null;
    } else {
      // In local development CLI execution, default to workspace .mock-state.json
      this.stateFilePath = path.resolve(process.cwd(), '.mock-state.json');
    }

    this.reset();
  }

  /**
   * Resets mock data back to baseline development state.
   */
  reset(): void {
    this.fixtures.clear();

    // 1. Barcelona vs Real Madrid (LIVE 72', score 1-1)
    const elClasicoEvents: ProviderEvent[] = [
      {
        id: 'evt_elclasico_1',
        eventUid: 'evt_fix_elclasico_2026_1_0_PeriodStart_init',
        fixtureId: 'fix_elclasico_2026',
        teamId: 'team_barcelona',
        type: 'PeriodStart',
        minute: 1,
        extraMinute: 0,
        description: 'First half kicks off',
      },
      {
        id: 'evt_elclasico_2',
        eventUid: 'evt_fix_elclasico_2026_17_0_Goal_bar10',
        fixtureId: 'fix_elclasico_2026',
        teamId: 'team_barcelona',
        type: 'Goal',
        minute: 17,
        extraMinute: 0,
        playerName: 'L. Martinez',
        playerId: 'ply_bar_10',
        assistPlayerName: 'P. Gonzalez',
        assistPlayerId: 'ply_bar_8',
        description: 'Goal! L. Martinez slots it into the bottom corner.',
      },
      {
        id: 'evt_elclasico_3',
        eventUid: 'evt_fix_elclasico_2026_35_0_YellowCard_rma5',
        fixtureId: 'fix_elclasico_2026',
        teamId: 'team_realmadrid',
        type: 'YellowCard',
        minute: 35,
        extraMinute: 0,
        playerName: 'V. Silva',
        playerId: 'ply_rma_5',
        description: 'Yellow card for tactical foul',
      },
      {
        id: 'evt_elclasico_4',
        eventUid: 'evt_fix_elclasico_2026_45_0_PeriodEnd_ht',
        fixtureId: 'fix_elclasico_2026',
        teamId: 'team_barcelona',
        type: 'PeriodEnd',
        minute: 45,
        extraMinute: 0,
        description: 'Half-time whistle',
      },
      {
        id: 'evt_elclasico_5',
        eventUid: 'evt_fix_elclasico_2026_46_0_PeriodStart_2h',
        fixtureId: 'fix_elclasico_2026',
        teamId: 'team_realmadrid',
        type: 'PeriodStart',
        minute: 46,
        extraMinute: 0,
        description: 'Second half kicks off',
      },
      {
        id: 'evt_elclasico_6',
        eventUid: 'evt_fix_elclasico_2026_72_0_Goal_rma9',
        fixtureId: 'fix_elclasico_2026',
        teamId: 'team_realmadrid',
        type: 'Goal',
        minute: 72,
        extraMinute: 0,
        playerName: 'K. Becker',
        playerId: 'ply_rma_9',
        assistPlayerName: 'R. Hernandez',
        assistPlayerId: 'ply_rma_7',
        description: 'Goal! K. Becker powers a header past the keeper.',
      },
    ];

    const elClasico: ProviderFixtureDetail = {
      id: 'fix_elclasico_2026',
      competitionId: 'comp_laliga',
      seasonId: 'season_laliga_2025',
      homeTeamId: 'team_barcelona',
      awayTeamId: 'team_realmadrid',
      competition: this.compLaLiga,
      season: this.seasonLaLiga,
      homeTeam: this.teamBarcelona,
      awayTeam: this.teamRealMadrid,
      round: 'Regular Season - Round 26',
      kickoffTime: '2026-09-01T01:42:51.804Z',
      state: 'LIVE',
      elapsedMinute: 72,
      extraMinute: 0,
      score: {
        home: 1,
        away: 1,
        homeHt: 1,
        awayHt: 0,
      },
      venue: 'Spotify Camp Nou',
      referee: 'J. Manzano',
      events: elClasicoEvents,
    };

    // 2. Spain vs Egypt (PREMATCH, score 0-0)
    const spainEgypt: ProviderFixtureDetail = {
      id: 'fix_spain_egypt_2026',
      competitionId: 'comp_international',
      seasonId: 'season_international_2025',
      homeTeamId: 'team_spain',
      awayTeamId: 'team_egypt',
      competition: this.compInternational,
      season: this.seasonInternational,
      homeTeam: this.teamSpain,
      awayTeam: this.teamEgypt,
      round: 'International Friendly',
      kickoffTime: '2026-09-01T06:27:51.804Z',
      state: 'PREMATCH',
      elapsedMinute: 0,
      extraMinute: 0,
      score: {
        home: 0,
        away: 0,
      },
      venue: 'Estadio Metropolitano (Mock)',
      referee: 'M. Oliver',
      events: [],
    };

    this.fixtures.set(elClasico.id, elClasico);
    this.fixtures.set(spainEgypt.id, spainEgypt);

    // If a persistent development state exists, load it on startup (dev only)
    this.loadPersistentState();
  }

  /**
   * Loads simulated fixture state from local file if configured.
   */
  loadPersistentState(): boolean {
    if (!this.stateFilePath) {
      return false;
    }

    try {
      if (!fs.existsSync(this.stateFilePath)) {
        return false;
      }

      const content = fs.readFileSync(this.stateFilePath, 'utf-8');
      const parsed: PersistentMockState = JSON.parse(content);

      if (!parsed?.fixtures) {
        return false;
      }

      for (const [id, saved] of Object.entries(parsed.fixtures)) {
        const existing = this.fixtures.get(id);
        if (existing) {
          existing.state = saved.state;
          existing.elapsedMinute = saved.elapsedMinute;
          existing.extraMinute = saved.extraMinute ?? 0;
          existing.score = { ...saved.score };
          existing.events = [...saved.events];
        }
      }

      return true;
    } catch {
      // Invalid/corrupt state file is safely ignored
      return false;
    }
  }

  /**
   * Saves simulated fixture state to local file (dev only).
   */
  savePersistentState(): boolean {
    if (!this.stateFilePath) {
      return false;
    }

    try {
      const persistent: PersistentMockState = { fixtures: {} };

      for (const [id, f] of this.fixtures.entries()) {
        persistent.fixtures[id] = {
          state: f.state,
          elapsedMinute: f.elapsedMinute,
          extraMinute: f.extraMinute ?? 0,
          score: { ...f.score },
          events: [...f.events],
        };
      }

      fs.writeFileSync(this.stateFilePath, JSON.stringify(persistent, null, 2), 'utf-8');
      return true;
    } catch {
      return false;
    }
  }

  /**
   * Clears the persistent mock state file from disk.
   */
  clearPersistentState(): void {
    if (!this.stateFilePath) {
      return;
    }

    try {
      if (fs.existsSync(this.stateFilePath)) {
        fs.unlinkSync(this.stateFilePath);
      }
    } catch {
      // ignore
    }
  }

  /**
   * Simulates a match event dynamically (e.g. 89' goal for Barcelona -> 2-1).
   * Fully idempotent: will not double-apply if the same event already exists.
   */
  simulateGoal(
    fixtureId: string,
    isHome: boolean,
    minute: number,
    scorer: { name: string; id: string },
    assist?: { name: string; id: string }
  ): boolean {
    const fixture = this.fixtures.get(fixtureId);
    if (!fixture) return false;

    const eventUid = `evt_${fixtureId}_${minute}_0_Goal_${scorer.id}`;

    // Idempotency check: if event is already recorded, do nothing
    if (fixture.events.some((e) => e.eventUid === eventUid)) {
      return false;
    }

    const team = isHome ? fixture.homeTeam : fixture.awayTeam;
    const newScore = {
      ...fixture.score,
      home: isHome ? fixture.score.home + 1 : fixture.score.home,
      away: !isHome ? fixture.score.away + 1 : fixture.score.away,
    };

    const newEvent: ProviderEvent = {
      id: `evt_${fixtureId}_${minute}_goal`,
      eventUid,
      fixtureId,
      teamId: team.id,
      type: 'Goal',
      minute,
      extraMinute: 0,
      playerName: scorer.name,
      playerId: scorer.id,
      assistPlayerName: assist?.name,
      assistPlayerId: assist?.id,
      description: `Goal! ${scorer.name} curls a strike into the top corner.`,
    };

    fixture.score = newScore;
    fixture.elapsedMinute = minute;
    fixture.events.push(newEvent);

    // Save development state across CLI executions
    this.savePersistentState();
    return true;
  }

  async getCompetitions(): Promise<ProviderCompetition[]> {
    return [this.compLaLiga, this.compInternational];
  }

  async getFixtures(filter?: FixtureFilter): Promise<ProviderFixture[]> {
    let list = Array.from(this.fixtures.values());
    if (filter?.competitionId) {
      list = list.filter((f) => f.competitionId === filter.competitionId);
    }
    if (filter?.liveOnly) {
      list = list.filter((f) => f.state === 'LIVE' || f.state === 'HALFTIME');
    }
    return list;
  }

  async getFixtureDetails(fixtureId: string): Promise<ProviderFixtureDetail | null> {
    return this.fixtures.get(fixtureId) ?? null;
  }

  async getFixtureDetailsBatch(fixtureIds: string[]): Promise<ProviderFixtureDetail[]> {
    const results: ProviderFixtureDetail[] = [];
    for (const id of fixtureIds) {
      const f = this.fixtures.get(id);
      if (f) {
        results.push(f);
      }
    }
    return results;
  }

  async getFixtureEvents(fixtureId: string): Promise<ProviderEvent[]> {
    const fixture = this.fixtures.get(fixtureId);
    return fixture ? [...fixture.events] : [];
  }

  async getStandings(competitionId: string, seasonYear: number): Promise<ProviderStanding[]> {
    return [
      {
        competitionId: 'comp_laliga',
        seasonId: 'season_laliga_2025',
        rows: [
          { position: 1, teamId: 'team_barcelona', played: 24, won: 18, drawn: 4, lost: 2, goalsFor: 58, goalsAgainst: 20, points: 58, form: 'WWWDW' },
          { position: 2, teamId: 'team_realmadrid', played: 24, won: 17, drawn: 5, lost: 2, goalsFor: 54, goalsAgainst: 18, points: 56, form: 'WWLWW' },
        ],
      },
    ];
  }
}
