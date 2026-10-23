import { describe, it, expect, beforeAll, afterAll, beforeEach, afterEach, vi } from 'vitest';
import { buildServer } from '../src/server.js';
import { FastifyInstance } from 'fastify';
import { db, schema } from '../src/db/index.js';
import { inArray, eq, and } from 'drizzle-orm';
import { SyncService } from '../src/acquisition/sync.service.js';
import { FotmobProvider } from '../src/providers/fotmob/fotmob.provider.js';
import { MatchStatus } from '../src/providers/types.js';

describe('Phase 10A — FotMob Match Details Normalization & On-Demand Hydration', () => {
  let app: FastifyInstance;

  const TEST_COMP_ID = 'comp_fotmob_hydr_test';
  const TEST_SEASON_ID = 'season_fotmob_hydr_2026';
  const TEST_HOME_TEAM = 'team_fotmob_home';
  const TEST_AWAY_TEAM = 'team_fotmob_away';
  const FIXTURE_ID = 'fix_fotmob_4193850';
  const CANONICAL_ID = 'ft_fix_fotmob_4193850';
  const PROVIDER_MATCH_ID = '4193850';

  const allFixtureIds = [FIXTURE_ID];
  const allTeamIds = [TEST_HOME_TEAM, TEST_AWAY_TEAM];

  const cleanup = async () => {
    try {
      await db.delete(schema.events).where(inArray(schema.events.fixtureId, allFixtureIds));
      const existingLineups = await db.select({ id: schema.lineups.id }).from(schema.lineups).where(inArray(schema.lineups.fixtureId, allFixtureIds));
      if (existingLineups.length > 0) {
        await db.delete(schema.lineupPlayers).where(inArray(schema.lineupPlayers.lineupId, existingLineups.map(l => l.id)));
      }
      await db.delete(schema.lineups).where(inArray(schema.lineups.fixtureId, allFixtureIds));
      await db.delete(schema.fixtureStatistics).where(inArray(schema.fixtureStatistics.fixtureId, allFixtureIds));
      await db.delete(schema.fixtureProviderAliases).where(inArray(schema.fixtureProviderAliases.providerId, [PROVIDER_MATCH_ID, FIXTURE_ID]));
      await db.delete(schema.fixtures).where(inArray(schema.fixtures.id, allFixtureIds));
      await db.delete(schema.teamProviderAliases).where(inArray(schema.teamProviderAliases.providerId, allTeamIds));
      await db.delete(schema.teams).where(inArray(schema.teams.id, allTeamIds));
      await db.delete(schema.competitionSeasons).where(eq(schema.competitionSeasons.id, TEST_SEASON_ID));
      await db.delete(schema.competitions).where(eq(schema.competitions.id, TEST_COMP_ID));
    } catch {
      // Ignore during teardown
    }
  };

  beforeAll(async () => {
    app = await buildServer();
    await app.ready();
    await cleanup();
  });

  afterAll(async () => {
    await cleanup();
    await app.close();
  });

  beforeEach(async () => {
    await cleanup();

    // 1. Seed competition & season
    await db.insert(schema.competitions).values({
      id: TEST_COMP_ID,
      canonicalId: `ft_${TEST_COMP_ID}`,
      name: 'Premier League',
      code: 'PL',
      type: 'LEAGUE',
      country: 'England',
    }).onConflictDoNothing();

    await db.insert(schema.competitionSeasons).values({
      id: TEST_SEASON_ID,
      competitionId: TEST_COMP_ID,
      year: 2026,
      displayYear: '2025/2026',
      startDate: new Date('2025-08-01'),
      endDate: new Date('2026-05-31'),
      isCurrent: true,
    }).onConflictDoNothing();

    // 2. Seed teams
    await db.insert(schema.teams).values({
      id: TEST_HOME_TEAM,
      canonicalId: `ft_${TEST_HOME_TEAM}`,
      name: 'Arsenal',
      shortName: 'ARS',
    }).onConflictDoNothing();

    await db.insert(schema.teams).values({
      id: TEST_AWAY_TEAM,
      canonicalId: `ft_${TEST_AWAY_TEAM}`,
      name: 'Manchester City',
      shortName: 'MCI',
    }).onConflictDoNothing();
  });

  afterEach(async () => {
    vi.restoreAllMocks();
    await cleanup();
  });

  it('FotmobProvider normalizes events, statistics (including xG), and lineups correctly', async () => {
    const provider = new FotmobProvider();

    const sampleFotmobPayload = {
      general: {
        matchId: PROVIDER_MATCH_ID,
        matchName: 'Arsenal - Manchester City',
        matchTimeUTCDate: '2026-10-09T19:00:00.000Z',
        started: true,
        finished: false,
      },
      header: {
        teams: [
          { id: '9825', name: 'Arsenal', score: 2 },
          { id: '8456', name: 'Manchester City', score: 1 },
        ],
        status: {
          started: true,
          finished: false,
          liveTime: { short: "68'" },
        },
      },
      content: {
        matchFacts: {
          events: {
            events: [
              {
                eventId: 'ev_101',
                type: 'Goal',
                time: 23,
                overloadTime: 0,
                teamId: '9825',
                player: { id: 'p_10', name: 'Bukayo Saka' },
                description: 'Goal! 1-0',
              },
              {
                eventId: 'ev_102',
                type: 'Card',
                card: 'Yellow',
                time: 41,
                overloadTime: 0,
                teamId: '8456',
                player: { id: 'p_20', name: 'Rodri' },
                description: 'Yellow Card',
              },
              {
                eventId: 'ev_103',
                type: 'Substitution',
                time: 60,
                overloadTime: 0,
                teamId: '9825',
                player: { id: 'p_30', name: 'Gabriel Martinelli' },
                description: 'Sub in',
              },
            ],
          },
        },
        lineup: {
          lineup: [
            {
              teamId: '9825',
              formation: '4-3-3',
              coachName: 'Mikel Arteta',
              starters: [
                { id: 'p_1', name: 'David Raya', number: 22, position: 'Keeper', row: 1, col: 2 },
                { id: 'p_10', name: 'Bukayo Saka', number: 7, position: 'Attacker', row: 4, col: 3 },
              ],
              substitutes: [
                { id: 'p_30', name: 'Gabriel Martinelli', number: 11, position: 'Attacker' },
              ],
            },
            {
              teamId: '8456',
              formation: '4-2-3-1',
              coachName: 'Pep Guardiola',
              starters: [
                { id: 'p_2', name: 'Ederson', number: 31, position: 'Keeper', row: 1, col: 2 },
                { id: 'p_20', name: 'Rodri', number: 16, position: 'Midfielder', row: 2, col: 2 },
              ],
              substitutes: [
                { id: 'p_40', name: 'Kevin De Bruyne', number: 17, position: 'Midfielder' },
              ],
            },
          ],
        },
        stats: {
          Periods: {
            All: {
              stats: [
                {
                  title: 'Top stats',
                  stats: [
                    { key: 'expected_goals', stats: ['1.85', '0.92'] },
                    { key: 'possession', stats: ['54%', '46%'] },
                    { key: 'total_shots', stats: ['14', '9'] },
                    { key: 'shots_on_target', stats: ['6', '3'] },
                    { key: 'fouls', stats: ['10', '12'] },
                    { key: 'corner_kicks', stats: ['7', '4'] },
                  ],
                },
              ],
            },
          },
        },
      },
    };

    (provider as any).fetchJson = vi.fn().mockResolvedValue(sampleFotmobPayload);

    const details = await provider.getMatchDetails(PROVIDER_MATCH_ID);

    expect(details.providerMatchId).toBe(PROVIDER_MATCH_ID);
    expect(details.homeTeam.name).toBe('Arsenal');
    expect(details.awayTeam.name).toBe('Manchester City');
    expect(details.time.minute).toBe(68);

    // Verify Events
    expect(details.events).toHaveLength(3);
    expect(details.events[0].type).toBe('Goal');
    expect(details.events[0].playerName).toBe('Bukayo Saka');
    expect(details.events[1].type).toBe('YellowCard');
    expect(details.events[2].type).toBe('Substitution');

    // Verify Stats array with xG and possession
    expect(details.stats).toHaveLength(2);
    const homeStat = details.stats.find((s: any) => s.teamId === '9825');
    const awayStat = details.stats.find((s: any) => s.teamId === '8456');
    expect(homeStat).toBeDefined();
    expect(homeStat.expectedGoals).toBe(1.85);
    expect(homeStat.possessionPercent).toBe(54);
    expect(homeStat.totalShots).toBe(14);
    expect(homeStat.shotsOnGoal).toBe(6);
    expect(awayStat.expectedGoals).toBe(0.92);
    expect(awayStat.possessionPercent).toBe(46);

    // Verify Lineups
    expect(details.lineups).toHaveLength(2);
    const homeLineup = details.lineups.find((l: any) => l.teamId === '9825');
    expect(homeLineup.formation).toBe('4-3-3');
    expect(homeLineup.coachName).toBe('Mikel Arteta');
    expect(homeLineup.startXi).toHaveLength(2);
    expect(homeLineup.startXi[0].name).toBe('David Raya');
    expect(homeLineup.startXi[0].grid).toBe('1:1');
    expect(homeLineup.substitutes).toHaveLength(1);
    expect(homeLineup.substitutes[0].name).toBe('Gabriel Martinelli');
  });

  it('GET /api/v2/matches/:id on-demand hydrates missing events, stats (with xG), and lineups from FotMob provider into SQLite', async () => {
    const fixedNow = new Date(Date.now() - 30 * 60 * 1000);

    // Seed fixture with state LIVE and NO stats or lineups
    await db.insert(schema.fixtures).values({
      id: FIXTURE_ID,
      canonicalId: CANONICAL_ID,
      competitionId: TEST_COMP_ID,
      seasonId: TEST_SEASON_ID,
      homeTeamId: TEST_HOME_TEAM,
      awayTeamId: TEST_AWAY_TEAM,
      state: 'LIVE',
      elapsedMinute: 65,
      homeScore: 1,
      awayScore: 0,
      kickoffTime: fixedNow,
      updatedAt: fixedNow,
    });

    // Seed alias mapping to provider ID
    await db.insert(schema.fixtureProviderAliases).values({
      canonicalFixtureId: CANONICAL_ID,
      providerId: PROVIDER_MATCH_ID,
      provider: 'fotmob',
    });

    // Create a mock provider returning FotMob details
    const mockProvider = {
      name: 'fotmob',
      circuitBreaker: { getState: () => 0 },
      getMatchDetails: vi.fn().mockResolvedValue({
        providerMatchId: PROVIDER_MATCH_ID,
        homeTeam: { providerId: '9825', name: 'Arsenal', score: 2 },
        awayTeam: { providerId: '8456', name: 'Manchester City', score: 1 },
        time: {
          status: MatchStatus.SECOND_HALF,
          minute: 70,
          addedTime: null,
          displayClock: "70'",
          scheduledKickoff: fixedNow.toISOString(),
        },
        events: [
          {
            id: `ev_${FIXTURE_ID}_1`,
            eventUid: 'fotmob_ev_101',
            fixtureId: FIXTURE_ID,
            teamId: '9825',
            type: 'Goal',
            minute: 23,
            extraMinute: 0,
            playerName: 'Bukayo Saka',
            playerId: 'p_10',
            assistPlayerName: 'Martin Odegaard',
            assistPlayerId: 'p_8',
            description: 'Goal! 1-0',
            timestamp: new Date().toISOString(),
          },
        ],
        stats: [
          {
            teamId: '9825',
            shotsOnGoal: 5,
            shotsOffGoal: 4,
            totalShots: 11,
            possessionPercent: 58,
            fouls: 8,
            cornerKicks: 6,
            offsides: 1,
            yellowCards: 1,
            redCards: 0,
            goalkeeperSaves: 2,
            totalPasses: 450,
            passesAccurate: 400,
            expectedGoals: 1.76,
          },
          {
            teamId: '8456',
            shotsOnGoal: 2,
            shotsOffGoal: 3,
            totalShots: 7,
            possessionPercent: 42,
            fouls: 11,
            cornerKicks: 3,
            offsides: 2,
            yellowCards: 2,
            redCards: 0,
            goalkeeperSaves: 3,
            totalPasses: 320,
            passesAccurate: 280,
            expectedGoals: 0.65,
          },
        ],
        lineups: [
          {
            teamId: '9825',
            formation: '4-3-3',
            coachName: 'Mikel Arteta',
            coachId: 'c_arteta',
            startXi: [
              { id: 'p_1', name: 'David Raya', number: 22, position: 'G', grid: '1:2', isStarter: true },
              { id: 'p_10', name: 'Bukayo Saka', number: 7, position: 'F', grid: '4:3', isStarter: true },
            ],
            substitutes: [
              { id: 'p_30', name: 'Gabriel Martinelli', number: 11, position: 'F', grid: null, isStarter: false },
            ],
          },
          {
            teamId: '8456',
            formation: '4-2-3-1',
            coachName: 'Pep Guardiola',
            coachId: 'c_pep',
            startXi: [
              { id: 'p_2', name: 'Ederson', number: 31, position: 'G', grid: '1:2', isStarter: true },
              { id: 'p_20', name: 'Rodri', number: 16, position: 'M', grid: '2:2', isStarter: true },
            ],
            substitutes: [
              { id: 'p_40', name: 'Kevin De Bruyne', number: 17, position: 'M', grid: null, isStarter: false },
            ],
          },
        ],
      }),
    };

    // Attach syncService with our mock provider to the Fastify instance
    (app as any).syncService = new SyncService(mockProvider as any);

    // Initial cold request to /api/v2/matches/:id
    const res = await app.inject({
      method: 'GET',
      url: `/api/v2/matches/${FIXTURE_ID}`,
    });

    expect(res.statusCode).toBe(200);
    const body = JSON.parse(res.payload);
    expect(body.schemaVersion).toBe('2.0');
    expect(body.data.id).toBe(CANONICAL_ID);
    expect(body.data.score.home).toBe(2);
    expect(body.data.score.away).toBe(1);

    // Verify Events in response
    expect(body.data.events).toHaveLength(1);
    expect(body.data.events[0].playerName).toBe('Bukayo Saka');
    expect(body.data.events[0].type).toBe('Goal');

    // Verify Lineups in response
    expect(body.data.lineups).toHaveLength(2);
    const homeLineup = body.data.lineups.find((l: any) => l.teamId === TEST_HOME_TEAM);
    expect(homeLineup).toBeDefined();
    expect(homeLineup.formation).toBe('4-3-3');
    expect(homeLineup.coachName).toBe('Mikel Arteta');
    expect(homeLineup.players.length).toBeGreaterThanOrEqual(2);

    // Verify Statistics in response with explicit expectedGoals and xg
    expect(body.data.statistics).toHaveLength(2);
    const homeStat = body.data.statistics.find((s: any) => s.teamId === TEST_HOME_TEAM);
    expect(homeStat).toBeDefined();
    expect(homeStat.possessionPercent).toBe(58);
    expect(homeStat.expectedGoals).toBe(1.76);
    expect(homeStat.xg).toBe(1.76);

    // Verify ETag header is present with detailsDigest
    const etag1 = res.headers['etag'] as string;
    expect(etag1).toBeDefined();
    expect(etag1).toMatch(/^W\/"match-.*"$/);

    // Verify SQLite persistence: records actually exist in canonical tables
    const dbStats = await db.select().from(schema.fixtureStatistics).where(eq(schema.fixtureStatistics.fixtureId, FIXTURE_ID));
    expect(dbStats).toHaveLength(2);
    const dbHomeStat = dbStats.find(s => s.teamId === TEST_HOME_TEAM);
    expect(dbHomeStat?.expectedGoals).toBe(1.76);
    expect(dbHomeStat?.possessionPercent).toBe(58);

    const dbEvents = await db.select().from(schema.events).where(eq(schema.events.fixtureId, FIXTURE_ID));
    expect(dbEvents).toHaveLength(1);
    expect(dbEvents[0].playerName).toBe('Bukayo Saka');

    // Re-poll with identical ETag -> MUST return 304 Not Modified
    const res304 = await app.inject({
      method: 'GET',
      url: `/api/v2/matches/${FIXTURE_ID}`,
      headers: {
        'if-none-match': etag1,
      },
    });
    expect(res304.statusCode).toBe(304);
    expect(res304.payload).toBe('');

    // Now mutate only statistics in SQLite (simulate possession or xG update)
    await db.update(schema.fixtureStatistics)
      .set({
        expectedGoals: 2.15,
        possessionPercent: 62,
        updatedAt: new Date(),
      })
      .where(and(eq(schema.fixtureStatistics.fixtureId, FIXTURE_ID), eq(schema.fixtureStatistics.teamId, TEST_HOME_TEAM)));

    // Re-poll with old etag1 -> MUST return 200 OK because detailsDigest changed!
    const resMutated = await app.inject({
      method: 'GET',
      url: `/api/v2/matches/${FIXTURE_ID}`,
      headers: {
        'if-none-match': etag1,
      },
    });
    expect(resMutated.statusCode).toBe(200);
    const etag2 = resMutated.headers['etag'] as string;
    expect(etag2).not.toBe(etag1);

    const mutatedBody = JSON.parse(resMutated.payload);
    const mutatedHomeStat = mutatedBody.data.statistics.find((s: any) => s.teamId === TEST_HOME_TEAM);
    expect(mutatedHomeStat.expectedGoals).toBe(2.15);
    expect(mutatedHomeStat.xg).toBe(2.15);
  });

  it('Gracefully falls back to existing SQLite data when upstream provider fails or circuit breaker trips', async () => {
    const fixedNow = new Date();

    // Seed fixture with basic data
    await db.insert(schema.fixtures).values({
      id: FIXTURE_ID,
      canonicalId: CANONICAL_ID,
      competitionId: TEST_COMP_ID,
      seasonId: TEST_SEASON_ID,
      homeTeamId: TEST_HOME_TEAM,
      awayTeamId: TEST_AWAY_TEAM,
      state: 'LIVE',
      elapsedMinute: 75,
      homeScore: 3,
      awayScore: 1,
      kickoffTime: fixedNow,
      updatedAt: fixedNow,
    });

    // Wire a failing provider (e.g. 503 challenge / network error / open circuit)
    const failingProvider = {
      name: 'fotmob',
      circuitBreaker: { getState: () => 1 }, // CircuitState.OPEN
      getMatchDetails: vi.fn().mockRejectedValue(new Error('Circuit breaker is OPEN')),
    };

    (app as any).syncService = new SyncService(failingProvider as any);

    const res = await app.inject({
      method: 'GET',
      url: `/api/v2/matches/${FIXTURE_ID}`,
    });

    // Must return 200 OK with existing database data rather than crashing
    expect(res.statusCode).toBe(200);
    const body = JSON.parse(res.payload);
    expect(body.data.id).toBe(CANONICAL_ID);
    expect(body.data.score.home).toBe(3);
    expect(body.data.score.away).toBe(1);
  });
});
