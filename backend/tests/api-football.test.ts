import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { ApiFootballMapper } from '../src/providers/api-football/api-football.mapper.js';
import { ApiFootballClient } from '../src/providers/api-football/api-football.client.js';
import { ApiFootballProvider } from '../src/providers/api-football/api-football.provider.js';
import { RequestGateway } from '../src/gateway/request-gateway.js';
import { pool } from '../src/db/index.js';
import {
  ApiFootballLeagueItem,
  ApiFootballFixtureItem,
  ApiFootballStandingItem,
} from '../src/providers/api-football/api-football.dto.js';

describe('ApiFootballMapper', () => {
  it('maps raw status codes accurately to canonical MatchState', () => {
    expect(ApiFootballMapper.mapStatusToMatchState('NS')).toBe('PREMATCH');
    expect(ApiFootballMapper.mapStatusToMatchState('TBD')).toBe('PREMATCH');
    expect(ApiFootballMapper.mapStatusToMatchState('1H')).toBe('LIVE');
    expect(ApiFootballMapper.mapStatusToMatchState('2H')).toBe('LIVE');
    expect(ApiFootballMapper.mapStatusToMatchState('LIVE')).toBe('LIVE');
    expect(ApiFootballMapper.mapStatusToMatchState('HT')).toBe('HALFTIME');
    expect(ApiFootballMapper.mapStatusToMatchState('BT')).toBe('HALFTIME');
    expect(ApiFootballMapper.mapStatusToMatchState('FT')).toBe('FINISHED');
    expect(ApiFootballMapper.mapStatusToMatchState('AET')).toBe('FINISHED');
    expect(ApiFootballMapper.mapStatusToMatchState('PEN')).toBe('FINISHED');
    expect(ApiFootballMapper.mapStatusToMatchState('PST')).toBe('POSTPONED');
    expect(ApiFootballMapper.mapStatusToMatchState('SUSP')).toBe('POSTPONED');
    expect(ApiFootballMapper.mapStatusToMatchState('CANC')).toBe('CANCELLED');
    expect(ApiFootballMapper.mapStatusToMatchState('ABD')).toBe('CANCELLED');
    expect(ApiFootballMapper.mapStatusToMatchState('UNKNOWN')).toBe('PREMATCH');
  });

  it('maps league items to ProviderCompetition and ProviderSeason models', () => {
    const rawLeague: ApiFootballLeagueItem = {
      league: {
        id: 39,
        name: 'Premier League',
        type: 'League',
        logo: 'https://media.api-sports.io/football/leagues/39.png',
      },
      country: {
        name: 'England',
        code: 'GB-ENG',
        flag: 'https://media.api-sports.io/flags/gb-eng.svg',
      },
      seasons: [
        {
          year: 2025,
          start: '2025-08-15',
          end: '2026-05-24',
          current: true,
          coverage: {
            fixtures: { events: true, lineups: true, statistics_fixtures: true, statistics_players: true },
            standings: true,
            players: true,
            top_scorers: true,
            top_assists: true,
            top_cards: true,
            predictions: true,
            odds: false,
          },
        },
      ],
    };

    const comp = ApiFootballMapper.mapLeagueToCompetition(rawLeague);
    expect(comp.id).toBe('comp_39');
    expect(comp.name).toBe('Premier League');
    expect(comp.country).toBe('England');

    const seasons = ApiFootballMapper.mapLeagueSeasons(rawLeague);
    expect(seasons.length).toBe(1);
    expect(seasons[0].id).toBe('season_39_2025');
    expect(seasons[0].year).toBe(2025);
    expect(seasons[0].isCurrent).toBe(true);
  });

  it('maps fixture details and chronological events correctly', () => {
    const rawFixture: ApiFootballFixtureItem = {
      fixture: {
        id: 1001,
        referee: 'M. Oliver',
        timezone: 'UTC',
        date: '2026-03-01T20:00:00Z',
        timestamp: 1772481600,
        periods: { first: 1772481600, second: 1772485200 },
        venue: { id: 556, name: 'Anfield', city: 'Liverpool' },
        status: { long: 'Second Half', short: '2H', elapsed: 75, extra: 0 },
      },
      league: {
        id: 39,
        name: 'Premier League',
        country: 'England',
        logo: 'https://media.api-sports.io/football/leagues/39.png',
        flag: 'https://media.api-sports.io/flags/gb-eng.svg',
        season: 2025,
        round: 'Regular Season - 28',
      },
      teams: {
        home: { id: 40, name: 'Liverpool', logo: 'https://media.api-sports.io/football/teams/40.png', winner: null },
        away: { id: 50, name: 'Manchester City', logo: 'https://media.api-sports.io/football/teams/50.png', winner: null },
      },
      goals: { home: 2, away: 1 },
      score: {
        halftime: { home: 1, away: 0 },
        fulltime: { home: null, away: null },
        extratime: { home: null, away: null },
        penalty: { home: null, away: null },
      },
      events: [
        {
          time: { elapsed: 23, extra: null },
          team: { id: 40, name: 'Liverpool', logo: 'https://media.api-sports.io/football/teams/40.png' },
          player: { id: 306, name: 'M. Salah' },
          assist: { id: 308, name: 'T. Alexander-Arnold' },
          type: 'Goal',
          detail: 'Normal Goal',
          comments: null,
        },
        {
          time: { elapsed: 54, extra: null },
          team: { id: 50, name: 'Manchester City', logo: 'https://media.api-sports.io/football/teams/50.png' },
          player: { id: 629, name: 'K. De Bruyne' },
          assist: { id: null, name: null },
          type: 'Card',
          detail: 'Yellow Card',
          comments: null,
        },
      ],
    };

    const mapped = ApiFootballMapper.mapFixtureDetail(rawFixture);
    expect(mapped.id).toBe('fix_1001');
    expect(mapped.state).toBe('LIVE');
    expect(mapped.elapsedMinute).toBe(75);
    expect(mapped.score.home).toBe(2);
    expect(mapped.score.away).toBe(1);
    expect(mapped.score.homeHt).toBe(1);
    expect(mapped.events.length).toBe(2);
    expect(mapped.events[0].type).toBe('Goal');
    expect(mapped.events[0].playerName).toBe('M. Salah');
    expect(mapped.events[1].type).toBe('YellowCard');
  });

  it('maps standings table into canonical ProviderStanding rows', () => {
    const rawStanding: ApiFootballStandingItem = {
      league: {
        id: 39,
        name: 'Premier League',
        country: 'England',
        logo: '',
        flag: null,
        season: 2025,
        standings: [
          [
            {
              rank: 1,
              team: { id: 40, name: 'Liverpool', logo: '' },
              points: 65,
              goalsDiff: 35,
              group: 'Premier League',
              form: 'WWWDW',
              status: 'same',
              description: 'Promotion - Champions League (Group Stage)',
              all: { played: 27, win: 20, draw: 5, lose: 2, goals: { for: 60, against: 25 } },
            },
            {
              rank: 2,
              team: { id: 50, name: 'Manchester City', logo: '' },
              points: 62,
              goalsDiff: 30,
              group: 'Premier League',
              form: 'WWLWW',
              status: 'same',
              description: 'Promotion - Champions League (Group Stage)',
              all: { played: 27, win: 19, draw: 5, lose: 3, goals: { for: 58, against: 28 } },
            },
          ],
        ],
      },
    };

    const standings = ApiFootballMapper.mapStandings(rawStanding);
    expect(standings.length).toBe(1);
    expect(standings[0].rows.length).toBe(2);
    expect(standings[0].rows[0].position).toBe(1);
    expect(standings[0].rows[0].teamId).toBe('team_40');
    expect(standings[0].rows[0].points).toBe(65);
    expect(standings[0].rows[1].position).toBe(2);
  });
});

describe('ApiFootballClient & ApiFootballProvider (Mocked Network)', () => {
  let gateway: RequestGateway;

  beforeEach(async () => {
    gateway = new RequestGateway(pool);
    await pool.query(`DELETE FROM api_request_ledger WHERE provider = 'api_football'`);
    await pool.query(`DELETE FROM provider_daily_budget WHERE provider = 'api_football'`);
  });

  afterEach(async () => {
    await pool.query(`DELETE FROM api_request_ledger WHERE provider = 'api_football'`);
    await pool.query(`DELETE FROM provider_daily_budget WHERE provider = 'api_football'`);
  });

  it('getStatus calls /status through RequestGateway with mock fetch', async () => {
    const mockFetch = async () => ({
      status: 200,
      headers: new Headers({ 'x-ratelimit-requests-remaining': '98' }),
      json: async () => ({
        get: 'status',
        parameters: [],
        errors: [],
        results: 1,
        paging: { current: 1, total: 1 },
        response: {
          account: { firstname: 'Test', lastname: 'User', email: 'test@example.com' },
          subscription: { plan: 'Free', end: '2026-12-31', active: true },
          requests: { current: 2, limit_day: 100 },
        },
      }),
    }) as any;

    const client = new ApiFootballClient({
      apiKey: 'test_api_key_123',
      gateway,
      fetchFn: mockFetch,
    });

    const resp = await client.getStatus();
    expect(resp.success).toBe(true);
    expect(resp.data?.response?.account?.firstname).toBe('Test');
    expect(resp.providerDailyRemaining).toBe(98);

    // Verify ledger record was created
    const ledgerCheck = await pool.query(
      `SELECT endpoint, operation, success FROM api_request_ledger WHERE provider = 'api_football'`
    );
    expect(ledgerCheck.rows.length).toBe(1);
    expect(ledgerCheck.rows[0].endpoint).toBe('/status');
    expect(ledgerCheck.rows[0].operation).toBe('status_probe');
    expect(ledgerCheck.rows[0].success).toBe(true);
  });

  it('ApiFootballProvider integrates cleanly and returns canonical models', async () => {
    const mockFetch = async (url: string) => {
      if (url.includes('/leagues')) {
        return {
          status: 200,
          headers: new Headers({ 'x-ratelimit-requests-remaining': '97' }),
          json: async () => ({
            get: 'leagues',
            parameters: {},
            errors: [],
            results: 1,
            paging: { current: 1, total: 1 },
            response: [
              {
                league: { id: 140, name: 'La Liga', type: 'League', logo: '' },
                country: { name: 'Spain', code: 'ES', flag: null },
                seasons: [{ year: 2025, start: '2025-08-15', end: '2026-05-24', current: true, coverage: { fixtures: { events: true, lineups: true, statistics_fixtures: true, statistics_players: true }, standings: true, players: true, top_scorers: true, top_assists: true, top_cards: true, predictions: true, odds: false } }],
              },
            ],
          }),
        };
      }

      if (url.includes('/fixtures')) {
        return {
          status: 200,
          headers: new Headers({ 'x-ratelimit-requests-remaining': '96' }),
          json: async () => ({
            get: 'fixtures',
            parameters: {},
            errors: [],
            results: 1,
            paging: { current: 1, total: 1 },
            response: [
              {
                fixture: {
                  id: 5001,
                  referee: null,
                  timezone: 'UTC',
                  date: '2026-03-01T20:00:00Z',
                  timestamp: 1772481600,
                  periods: { first: null, second: null },
                  venue: { id: null, name: 'Camp Nou', city: 'Barcelona' },
                  status: { long: 'Not Started', short: 'NS', elapsed: 0, extra: 0 },
                },
                league: { id: 140, name: 'La Liga', country: 'Spain', logo: '', flag: null, season: 2025, round: 'Round 26' },
                teams: {
                  home: { id: 529, name: 'Barcelona', logo: '', winner: null },
                  away: { id: 541, name: 'Real Madrid', logo: '', winner: null },
                },
                goals: { home: null, away: null },
                score: {
                  halftime: { home: null, away: null },
                  fulltime: { home: null, away: null },
                  extratime: { home: null, away: null },
                  penalty: { home: null, away: null },
                },
                events: [],
              },
            ],
          }),
        };
      }

      return {
        status: 404,
        headers: new Headers(),
        json: async () => ({ errors: ['Not Found'] }),
      };
    };

    const provider = new ApiFootballProvider({
      apiKey: 'test_key',
      gateway,
      fetchFn: mockFetch as any,
    });

    const comps = await provider.getCompetitions();
    expect(comps.length).toBe(1);
    expect(comps[0].id).toBe('comp_140');
    expect(comps[0].name).toBe('La Liga');

    const fixtures = await provider.getFixtures();
    expect(fixtures.length).toBe(1);
    expect(fixtures[0].id).toBe('fix_5001');
    expect(fixtures[0].homeTeam.name).toBe('Barcelona');
  });

  it('detects and records target competition coverage from mocked /leagues batch query', async () => {
    const targetIds = [39, 140, 233, 2, 6, 1];
    const mockLeaguesResponse = [
      {
        league: { id: 39, name: 'Premier League', type: 'League', logo: '' },
        country: { name: 'England', code: 'GB-ENG', flag: null },
        seasons: [{ year: 2025, start: '2025-08-15', end: '2026-05-24', current: true, coverage: { fixtures: { events: true, lineups: true, statistics_fixtures: true, statistics_players: true }, standings: true, players: true, top_scorers: true, top_assists: true, top_cards: true, predictions: true, odds: false } }],
      },
      {
        league: { id: 140, name: 'La Liga', type: 'League', logo: '' },
        country: { name: 'Spain', code: 'ES', flag: null },
        seasons: [{ year: 2025, start: '2025-08-15', end: '2026-05-24', current: true, coverage: { fixtures: { events: true, lineups: true, statistics_fixtures: true, statistics_players: true }, standings: true, players: true, top_scorers: true, top_assists: true, top_cards: true, predictions: true, odds: false } }],
      },
      {
        league: { id: 999, name: 'Unrelated League', type: 'League', logo: '' },
        country: { name: 'Nowhere', code: null, flag: null },
        seasons: [{ year: 2025, start: '2025-01-01', end: '2025-12-31', current: true, coverage: { fixtures: { events: false, lineups: false, statistics_fixtures: false, statistics_players: false }, standings: false, players: false, top_scorers: false, top_assists: false, top_cards: false, predictions: false, odds: false } }],
      },
    ];

    const mockFetch = async () => ({
      status: 200,
      headers: new Headers({ 'x-ratelimit-requests-remaining': '95' }),
      json: async () => ({
        get: 'leagues',
        parameters: { current: 'true', season: '2025' },
        errors: [],
        results: 3,
        paging: { current: 1, total: 1 },
        response: mockLeaguesResponse,
      }),
    }) as any;

    const client = new ApiFootballClient({
      apiKey: 'test_key',
      gateway,
      fetchFn: mockFetch,
    });

    const resp = await client.getLeagues({ current: true, season: 2025 });
    expect(resp.success).toBe(true);

    const discovered = (resp.data?.response || []).filter((item) => targetIds.includes(item.league.id));
    expect(discovered.length).toBe(2);
    expect(discovered.map((d) => d.league.id)).toEqual([39, 140]);
    // 999 was filtered out
    expect(discovered.some((d) => d.league.id === 999)).toBe(false);
  });

  it('queries fixtures per competition ID when competitionIds is provided', async () => {
    const requestedUrls: string[] = [];

    const mockFetch = async (url: string) => {
      requestedUrls.push(url);

      const parsedUrl = new URL(url);
      const leagueParam = parsedUrl.searchParams.get('league');

      const fixtureId = leagueParam === '39' ? 3901 : 14001;
      const leagueName = leagueParam === '39' ? 'Premier League' : 'La Liga';

      return {
        status: 200,
        headers: new Headers({ 'x-ratelimit-requests-remaining': '90' }),
        json: async () => ({
          get: 'fixtures',
          parameters: { league: leagueParam },
          errors: [],
          results: 1,
          paging: { current: 1, total: 1 },
          response: [
            {
              fixture: {
                id: fixtureId,
                referee: null,
                timezone: 'UTC',
                date: '2026-03-01T15:00:00Z',
                timestamp: 1772463600,
                periods: { first: null, second: null },
                venue: { id: null, name: 'Stadium', city: 'City' },
                status: { long: 'Not Started', short: 'NS', elapsed: 0, extra: 0 },
              },
              league: { id: Number(leagueParam), name: leagueName, country: 'Country', logo: '', flag: null, season: 2025, round: 'Round 1' },
              teams: {
                home: { id: 10, name: 'Home Team', logo: '', winner: null },
                away: { id: 20, name: 'Away Team', logo: '', winner: null },
              },
              goals: { home: null, away: null },
              score: {
                halftime: { home: null, away: null },
                fulltime: { home: null, away: null },
                extratime: { home: null, away: null },
                penalty: { home: null, away: null },
              },
              events: [],
            },
          ],
        }),
      };
    };

    const provider = new ApiFootballProvider({
      apiKey: 'test_key',
      gateway,
      fetchFn: mockFetch as any,
    });

    const fixtures = await provider.getFixtures({ competitionIds: [39, 140] });

    // Must make exactly 2 requests — one for league=39, one for league=140
    expect(requestedUrls.length).toBe(2);
    expect(requestedUrls.some((u) => u.includes('league=39'))).toBe(true);
    expect(requestedUrls.some((u) => u.includes('league=140'))).toBe(true);

    // Results aggregated from both competitions
    expect(fixtures.length).toBe(2);
    expect(fixtures.map((f) => f.id)).toEqual(['fix_3901', 'fix_14001']);
    expect(fixtures.map((f) => f.competitionId)).toEqual(['comp_39', 'comp_140']);
  });

  it('uses live=all endpoint and post-filters to competitionIds in memory when liveOnly is true', async () => {
    const requestedUrls: string[] = [];

    const mockFetch = async (url: string) => {
      requestedUrls.push(url);

      return {
        status: 200,
        headers: new Headers({ 'x-ratelimit-requests-remaining': '90' }),
        json: async () => ({
          get: 'fixtures',
          parameters: { live: 'all' },
          errors: [],
          results: 3,
          paging: { current: 1, total: 1 },
          response: [
            {
              fixture: {
                id: 101,
                referee: null,
                timezone: 'UTC',
                date: '2026-03-01T15:00:00Z',
                timestamp: 1772463600,
                periods: { first: null, second: null },
                venue: { id: null, name: 'Stadium 1', city: 'London' },
                status: { long: 'First Half', short: '1H', elapsed: 25, extra: 0 },
              },
              league: { id: 39, name: 'Premier League', country: 'England', logo: '', flag: null, season: 2025, round: 'Round 1' },
              teams: {
                home: { id: 1, name: 'Arsenal', logo: '', winner: null },
                away: { id: 2, name: 'Chelsea', logo: '', winner: null },
              },
              goals: { home: 1, away: 0 },
              score: { halftime: { home: null, away: null }, fulltime: { home: null, away: null }, extratime: { home: null, away: null }, penalty: { home: null, away: null } },
              events: [],
            },
            {
              fixture: {
                id: 102,
                referee: null,
                timezone: 'UTC',
                date: '2026-03-01T15:00:00Z',
                timestamp: 1772463600,
                periods: { first: null, second: null },
                venue: { id: null, name: 'Stadium 2', city: 'Madrid' },
                status: { long: 'First Half', short: '1H', elapsed: 10, extra: 0 },
              },
              league: { id: 140, name: 'La Liga', country: 'Spain', logo: '', flag: null, season: 2025, round: 'Round 1' },
              teams: {
                home: { id: 3, name: 'Atletico', logo: '', winner: null },
                away: { id: 4, name: 'Sevilla', logo: '', winner: null },
              },
              goals: { home: 0, away: 0 },
              score: { halftime: { home: null, away: null }, fulltime: { home: null, away: null }, extratime: { home: null, away: null }, penalty: { home: null, away: null } },
              events: [],
            },
            {
              fixture: {
                id: 103,
                referee: null,
                timezone: 'UTC',
                date: '2026-03-01T15:00:00Z',
                timestamp: 1772463600,
                periods: { first: null, second: null },
                venue: { id: null, name: 'Stadium 3', city: 'Unrelated' },
                status: { long: 'First Half', short: '1H', elapsed: 5, extra: 0 },
              },
              league: { id: 999, name: 'Unrelated League', country: 'Other', logo: '', flag: null, season: 2025, round: 'Round 1' },
              teams: {
                home: { id: 5, name: 'Team A', logo: '', winner: null },
                away: { id: 6, name: 'Team B', logo: '', winner: null },
              },
              goals: { home: 0, away: 0 },
              score: { halftime: { home: null, away: null }, fulltime: { home: null, away: null }, extratime: { home: null, away: null }, penalty: { home: null, away: null } },
              events: [],
            },
          ],
        }),
      };
    };

    const provider = new ApiFootballProvider({
      apiKey: 'test_key',
      gateway,
      fetchFn: mockFetch as any,
    });

    const fixtures = await provider.getFixtures({ liveOnly: true, competitionIds: [39] });

    // Exactly 1 network request made to live=all
    expect(requestedUrls.length).toBe(1);
    expect(requestedUrls[0]).toContain('live=all');

    // Filtered in-memory to only competition 39 (La Liga and Unrelated League removed)
    expect(fixtures.length).toBe(1);
    expect(fixtures[0].id).toBe('fix_101');
    expect(fixtures[0].competitionId).toBe('comp_39');
  });
});

