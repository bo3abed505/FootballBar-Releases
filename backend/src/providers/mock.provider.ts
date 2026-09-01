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

export class MockFootballProvider implements FootballProvider {
  readonly providerName = 'mock';

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

  private readonly teamAlAhly: ProviderTeam = {
    id: 'team_alahly',
    name: 'Al Ahly SC',
    shortName: 'AHL',
    code: 'AHL',
    country: 'Egypt',
    logoUrl: 'https://media.api-sports.io/football/teams/1029.png',
    venueName: 'Cairo International Stadium',
  };

  // Seeded Competitions
  private readonly compLaLiga: ProviderCompetition = {
    id: 'comp_laliga',
    name: 'La Liga (Dev Mock)',
    code: 'ESP_L1',
    type: 'league',
    country: 'Spain',
    logoUrl: 'https://media.api-sports.io/football/leagues/140.png',
  };

  private readonly compInternational: ProviderCompetition = {
    id: 'comp_international',
    name: 'International Friendly (Dev Mock)',
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

  constructor() {
    this.reset();
  }

  /**
   * Resets mock data back to baseline development state
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
        description: '[DEV MOCK] First half kicks off',
      },
      {
        id: 'evt_elclasico_2',
        eventUid: 'evt_fix_elclasico_2026_17_0_Goal_bar10',
        fixtureId: 'fix_elclasico_2026',
        teamId: 'team_barcelona',
        type: 'Goal',
        minute: 17,
        extraMinute: 0,
        playerName: 'L. Martinez (Dev Player)',
        playerId: 'ply_bar_10',
        assistPlayerName: 'P. Gonzalez (Dev Player)',
        assistPlayerId: 'ply_bar_8',
        description: '[DEV MOCK] Goal! L. Martinez slots it into the bottom corner.',
      },
      {
        id: 'evt_elclasico_3',
        eventUid: 'evt_fix_elclasico_2026_35_0_YellowCard_rma5',
        fixtureId: 'fix_elclasico_2026',
        teamId: 'team_realmadrid',
        type: 'YellowCard',
        minute: 35,
        extraMinute: 0,
        playerName: 'V. Silva (Dev Player)',
        playerId: 'ply_rma_5',
        description: '[DEV MOCK] Yellow card for tactical foul',
      },
      {
        id: 'evt_elclasico_4',
        eventUid: 'evt_fix_elclasico_2026_45_0_PeriodEnd_ht',
        fixtureId: 'fix_elclasico_2026',
        teamId: 'team_barcelona',
        type: 'PeriodEnd',
        minute: 45,
        extraMinute: 0,
        description: '[DEV MOCK] Half-time whistle',
      },
      {
        id: 'evt_elclasico_5',
        eventUid: 'evt_fix_elclasico_2026_46_0_PeriodStart_2h',
        fixtureId: 'fix_elclasico_2026',
        teamId: 'team_realmadrid',
        type: 'PeriodStart',
        minute: 46,
        extraMinute: 0,
        description: '[DEV MOCK] Second half kicks off',
      },
      {
        id: 'evt_elclasico_6',
        eventUid: 'evt_fix_elclasico_2026_72_0_Goal_rma9',
        fixtureId: 'fix_elclasico_2026',
        teamId: 'team_realmadrid',
        type: 'Goal',
        minute: 72,
        extraMinute: 0,
        playerName: 'K. Becker (Dev Player)',
        playerId: 'ply_rma_9',
        assistPlayerName: 'R. Hernandez (Dev Player)',
        assistPlayerId: 'ply_rma_7',
        description: '[DEV MOCK] Goal! K. Becker powers a header past the keeper.',
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
      round: '[DEV MOCK] Regular Season - Round 26',
      kickoffTime: new Date(Date.now() - 45 * 60 * 1000).toISOString(),
      state: 'LIVE',
      elapsedMinute: 72,
      extraMinute: 0,
      score: {
        home: 1,
        away: 1,
        homeHt: 1,
        awayHt: 0,
      },
      venue: 'Spotify Camp Nou (Mock)',
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
      round: '[DEV MOCK] International Friendly',
      kickoffTime: new Date(Date.now() + 4 * 60 * 60 * 1000).toISOString(),
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
  }

  /**
   * Helper to simulate a match event dynamically (e.g. 88' goal for Barcelona -> 2-1)
   */
  simulateGoal(fixtureId: string, isHome: boolean, minute: number, scorer: { name: string; id: string }, assist?: { name: string; id: string }): void {
    const fixture = this.fixtures.get(fixtureId);
    if (!fixture) return;

    const team = isHome ? fixture.homeTeam : fixture.awayTeam;
    const newScore = {
      ...fixture.score,
      home: isHome ? fixture.score.home + 1 : fixture.score.home,
      away: !isHome ? fixture.score.away + 1 : fixture.score.away,
    };

    const newEvent: ProviderEvent = {
      id: `evt_${fixtureId}_${minute}_goal`,
      eventUid: `evt_${fixtureId}_${minute}_0_Goal_${scorer.id}`,
      fixtureId,
      teamId: team.id,
      type: 'Goal',
      minute,
      extraMinute: 0,
      playerName: scorer.name,
      playerId: scorer.id,
      assistPlayerName: assist?.name,
      assistPlayerId: assist?.id,
      description: `[DEV MOCK] Goal! ${scorer.name} curls a strike into the top corner.`,
    };

    fixture.score = newScore;
    fixture.elapsedMinute = minute;
    fixture.events.push(newEvent);
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
