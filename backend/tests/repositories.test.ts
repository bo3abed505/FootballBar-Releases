import { describe, it, expect } from 'vitest';
import { CompetitionsRepository } from '../src/repositories/competitions.repository.js';
import { TeamsRepository } from '../src/repositories/teams.repository.js';
import { FixturesRepository } from '../src/repositories/fixtures.repository.js';
import { EventsRepository } from '../src/repositories/events.repository.js';
import { schema } from '../src/db/index.js';

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
