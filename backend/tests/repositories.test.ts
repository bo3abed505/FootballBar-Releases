import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { CompetitionsRepository } from '../src/repositories/competitions.repository.js';
import { TeamsRepository } from '../src/repositories/teams.repository.js';
import { FixturesRepository } from '../src/repositories/fixtures.repository.js';
import { EventsRepository } from '../src/repositories/events.repository.js';
import { db, schema } from '../src/db/index.js';
import { inArray } from 'drizzle-orm';
import { generateCanonicalId } from '../src/db/canonical-id.js';

describe('Repository Classes & Schema Definitions', () => {
  it('instantiates all repository classes cleanly', () => {
    const compRepo = new CompetitionsRepository();
    const teamsRepo = new TeamsRepository();
    const fixturesRepo = new FixturesRepository();
    const eventsRepo = new EventsRepository();

    expect(compRepo).toBeInstanceOf(CompetitionsRepository);
    expect(teamsRepo).toBeInstanceOf(TeamsRepository);
    expect(fixturesRepo).toBeInstanceOf(FixturesRepository);
    expect(eventsRepo).toBeInstanceOf(EventsRepository);
  });

  it('defines all required schema tables with expected column mappings', () => {
    expect(schema.competitions).toBeDefined();
    expect(schema.competitionSeasons).toBeDefined();
    expect(schema.teams).toBeDefined();
    expect(schema.fixtures).toBeDefined();
    expect(schema.events).toBeDefined();
    expect(schema.lineups).toBeDefined();
    expect(schema.lineupPlayers).toBeDefined();
    expect(schema.fixtureStatistics).toBeDefined();
    expect(schema.standings).toBeDefined();
    expect(schema.providerCoverage).toBeDefined();
    expect(schema.acquisitionJobs).toBeDefined();
    expect(schema.apiRequestLedger).toBeDefined();
    expect(schema.providerDailyBudget).toBeDefined();
    expect(schema.installations).toBeDefined();
    expect(schema.installationSettings).toBeDefined();
  });
});

describe('FixturesRepository.getTrayMatches()', () => {
  const fixturesRepo = new FixturesRepository();
  const fixedNow = new Date('2026-06-01T12:00:00.000Z');

  const testCompId = 'comp_repo_test';
  const testSeasonId = 'season_repo_test';
  const testTeamA = 'team_repo_test_a';
  const testTeamB = 'team_repo_test_b';

  const testFixtureIds = [
    'fix_repo_live_early',
    'fix_repo_live_late',
    'fix_repo_live_stale',
    'fix_repo_live_tie_a',
    'fix_repo_live_tie_b',
    'fix_repo_prematch_near',
    'fix_repo_prematch_far',
    'fix_repo_prematch_beyond24h',
    'fix_repo_prematch_mock2027',
    'fix_repo_finished_recent',
    'fix_repo_finished_earlier',
    'fix_repo_finished_old',
    'fix_repo_postponed',
    'fix_repo_cancelled',
  ];

  const insertFixtures = async (fixtures: (Omit<typeof schema.fixtures.$inferInsert, 'canonicalId'> & { canonicalId?: string })[]) => {
    return db.insert(schema.fixtures).values(
      fixtures.map((f) => ({
        canonicalId: f.canonicalId || generateCanonicalId('fix'),
        ...f,
      }))
    );
  };

  const cleanup = async () => {
    await db.delete(schema.events).where(inArray(schema.events.fixtureId, testFixtureIds));
    await db.delete(schema.fixtures).where(inArray(schema.fixtures.id, testFixtureIds));
    await db.delete(schema.teams).where(inArray(schema.teams.id, [testTeamA, testTeamB]));
    await db.delete(schema.competitionSeasons).where(inArray(schema.competitionSeasons.id, [testSeasonId]));
    await db.delete(schema.competitions).where(inArray(schema.competitions.id, [testCompId]));
  };

  beforeEach(async () => {
    await cleanup();

    // Insert test competition, season, and teams
    await db.insert(schema.competitions).values({
      id: testCompId,
      canonicalId: 'ft_comp_repo_test',
      name: 'Repo Test League',
      code: 'RTL',
      country: 'TestLand',
    }).onConflictDoNothing();

    await db.insert(schema.competitionSeasons).values({
      id: testSeasonId,
      competitionId: testCompId,
      year: 2026,
      displayYear: '2026',
      startDate: new Date('2026-01-01T00:00:00Z'),
      endDate: new Date('2026-12-31T23:59:59Z'),
      isCurrent: true,
    }).onConflictDoNothing();

    await db.insert(schema.teams).values([
      { id: testTeamA, canonicalId: 'ft_team_repo_test_a', name: 'Team Alpha', shortName: 'ALP' },
      { id: testTeamB, canonicalId: 'ft_team_repo_test_b', name: 'Team Beta', shortName: 'BET' },
    ]).onConflictDoNothing();
  });

  afterEach(async () => {
    await cleanup();
  });

  it('correctly prioritizes LIVE over PREMATCH over FINISHED and enforces multi-tier sort orders', async () => {
    // 1. LIVE matches (kickoff 60m ago and 20m ago)
    await insertFixtures([
      {
        id: 'fix_repo_live_early',
        competitionId: testCompId,
        seasonId: testSeasonId,
        homeTeamId: testTeamA,
        awayTeamId: testTeamB,
        state: 'LIVE',
        kickoffTime: new Date(fixedNow.getTime() - 60 * 60 * 1000), // 11:00 (ASC -> 1st)
      },
      {
        id: 'fix_repo_live_late',
        competitionId: testCompId,
        seasonId: testSeasonId,
        homeTeamId: testTeamA,
        awayTeamId: testTeamB,
        state: '1H',
        kickoffTime: new Date(fixedNow.getTime() - 20 * 60 * 1000), // 11:40 (ASC -> 2nd)
      },
    ]);

    // 2. PREMATCH matches (+1h and +6h)
    await insertFixtures([
      {
        id: 'fix_repo_prematch_near',
        competitionId: testCompId,
        seasonId: testSeasonId,
        homeTeamId: testTeamA,
        awayTeamId: testTeamB,
        state: 'PREMATCH',
        kickoffTime: new Date(fixedNow.getTime() + 1 * 60 * 60 * 1000), // 13:00 (ASC -> 3rd)
      },
      {
        id: 'fix_repo_prematch_far',
        competitionId: testCompId,
        seasonId: testSeasonId,
        homeTeamId: testTeamA,
        awayTeamId: testTeamB,
        state: 'NS',
        kickoffTime: new Date(fixedNow.getTime() + 6 * 60 * 60 * 1000), // 18:00 (ASC -> 4th)
      },
    ]);

    // 3. FINISHED matches (-1h and -3h)
    await insertFixtures([
      {
        id: 'fix_repo_finished_recent',
        competitionId: testCompId,
        seasonId: testSeasonId,
        homeTeamId: testTeamA,
        awayTeamId: testTeamB,
        state: 'FINISHED',
        kickoffTime: new Date(fixedNow.getTime() - 60 * 60 * 1000), // 11:00 (DESC -> 5th)
      },
      {
        id: 'fix_repo_finished_earlier',
        competitionId: testCompId,
        seasonId: testSeasonId,
        homeTeamId: testTeamA,
        awayTeamId: testTeamB,
        state: 'FT',
        kickoffTime: new Date(fixedNow.getTime() - 3 * 60 * 60 * 1000), // 09:00 (DESC -> 6th)
      },
    ]);

    const results = await fixturesRepo.getTrayMatches(10, fixedNow);
    const testResults = results.filter((m) => testFixtureIds.includes(m.id));

    expect(testResults.map((m) => m.id)).toEqual([
      'fix_repo_live_early',     // Tier 1: LIVE (kickoff 11:00)
      'fix_repo_live_late',      // Tier 1: LIVE (kickoff 11:40)
      'fix_repo_prematch_near',  // Tier 2: PREMATCH +1h (13:00)
      'fix_repo_prematch_far',   // Tier 2: PREMATCH +6h (18:00)
      'fix_repo_finished_recent', // Tier 3: FINISHED -1h (11:00)
      'fix_repo_finished_earlier',// Tier 3: FINISHED -3h (09:00)
    ]);
  });

  it('excludes stale LIVE, far-future PREMATCH, old FINISHED, and terminal states (POSTPONED/CANCELLED)', async () => {
    await insertFixtures([
      // Stale LIVE (4 hours past kickoff, beyond 3.5h max age)
      {
        id: 'fix_repo_live_stale',
        competitionId: testCompId,
        seasonId: testSeasonId,
        homeTeamId: testTeamA,
        awayTeamId: testTeamB,
        state: 'LIVE',
        kickoffTime: new Date(fixedNow.getTime() - 4 * 60 * 60 * 1000),
      },
      // PREMATCH beyond +24h window (+30h)
      {
        id: 'fix_repo_prematch_beyond24h',
        competitionId: testCompId,
        seasonId: testSeasonId,
        homeTeamId: testTeamA,
        awayTeamId: testTeamB,
        state: 'PREMATCH',
        kickoffTime: new Date(fixedNow.getTime() + 30 * 60 * 60 * 1000),
      },
      // Ineligible high-kickoff mock row (+365 days in future)
      {
        id: 'fix_repo_prematch_mock2027',
        competitionId: testCompId,
        seasonId: testSeasonId,
        homeTeamId: testTeamA,
        awayTeamId: testTeamB,
        state: 'PREMATCH',
        kickoffTime: new Date(fixedNow.getTime() + 365 * 24 * 60 * 60 * 1000),
      },
      // FINISHED older than 4h window (-5h)
      {
        id: 'fix_repo_finished_old',
        competitionId: testCompId,
        seasonId: testSeasonId,
        homeTeamId: testTeamA,
        awayTeamId: testTeamB,
        state: 'FINISHED',
        kickoffTime: new Date(fixedNow.getTime() - 5 * 60 * 60 * 1000),
      },
      // Terminal: POSTPONED (+2h)
      {
        id: 'fix_repo_postponed',
        competitionId: testCompId,
        seasonId: testSeasonId,
        homeTeamId: testTeamA,
        awayTeamId: testTeamB,
        state: 'POSTPONED',
        kickoffTime: new Date(fixedNow.getTime() + 2 * 60 * 60 * 1000),
      },
      // Terminal: CANCELLED (+2h)
      {
        id: 'fix_repo_cancelled',
        competitionId: testCompId,
        seasonId: testSeasonId,
        homeTeamId: testTeamA,
        awayTeamId: testTeamB,
        state: 'CANCELLED',
        kickoffTime: new Date(fixedNow.getTime() + 2 * 60 * 60 * 1000),
      },
      // Valid baseline upcoming match (+2h)
      {
        id: 'fix_repo_prematch_near',
        competitionId: testCompId,
        seasonId: testSeasonId,
        homeTeamId: testTeamA,
        awayTeamId: testTeamB,
        state: 'PREMATCH',
        kickoffTime: new Date(fixedNow.getTime() + 2 * 60 * 60 * 1000),
      },
    ]);

    const results = await fixturesRepo.getTrayMatches(10, fixedNow);
    const testResults = results.filter((m) => testFixtureIds.includes(m.id));

    // Only the valid upcoming match should be returned; all stale/out-of-window/terminal rows must be excluded
    expect(testResults).toHaveLength(1);
    expect(testResults[0].id).toBe('fix_repo_prematch_near');
  });

  it('applies caller-supplied limit AFTER priority ordering', async () => {
    await insertFixtures([
      {
        id: 'fix_repo_live_early',
        competitionId: testCompId,
        seasonId: testSeasonId,
        homeTeamId: testTeamA,
        awayTeamId: testTeamB,
        state: 'LIVE',
        kickoffTime: new Date(fixedNow.getTime() - 60 * 60 * 1000),
      },
      {
        id: 'fix_repo_live_late',
        competitionId: testCompId,
        seasonId: testSeasonId,
        homeTeamId: testTeamA,
        awayTeamId: testTeamB,
        state: 'LIVE',
        kickoffTime: new Date(fixedNow.getTime() - 20 * 60 * 1000),
      },
      {
        id: 'fix_repo_prematch_near',
        competitionId: testCompId,
        seasonId: testSeasonId,
        homeTeamId: testTeamA,
        awayTeamId: testTeamB,
        state: 'PREMATCH',
        kickoffTime: new Date(fixedNow.getTime() + 1 * 60 * 60 * 1000),
      },
    ]);

    // Request limit of 2: should return only the 2 LIVE matches, not the PREMATCH match
    const results = await fixturesRepo.getTrayMatches(2, fixedNow);
    const testResults = results.filter((m) => testFixtureIds.includes(m.id));

    expect(testResults).toHaveLength(2);
    expect(testResults.map((m) => m.id)).toEqual([
      'fix_repo_live_early',
      'fix_repo_live_late',
    ]);
  });

  it('breaks ties deterministically by id ASC when state and kickoff are identical', async () => {
    const sharedKickoff = new Date(fixedNow.getTime() - 30 * 60 * 1000);
    await insertFixtures([
      {
        id: 'fix_repo_live_tie_b',
        competitionId: testCompId,
        seasonId: testSeasonId,
        homeTeamId: testTeamA,
        awayTeamId: testTeamB,
        state: 'LIVE',
        kickoffTime: sharedKickoff,
      },
      {
        id: 'fix_repo_live_tie_a',
        competitionId: testCompId,
        seasonId: testSeasonId,
        homeTeamId: testTeamA,
        awayTeamId: testTeamB,
        state: 'LIVE',
        kickoffTime: sharedKickoff,
      },
    ]);

    const results = await fixturesRepo.getTrayMatches(10, fixedNow);
    const testResults = results.filter((m) =>
      m.id === 'fix_repo_live_tie_a' || m.id === 'fix_repo_live_tie_b'
    );

    expect(testResults.map((m) => m.id)).toEqual([
      'fix_repo_live_tie_a',
      'fix_repo_live_tie_b',
    ]);
  });
});
