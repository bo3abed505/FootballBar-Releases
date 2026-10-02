import {
  FootballProvider,
  ProviderCompetition,
  ProviderFixture,
  ProviderFixtureDetail,
  ProviderEvent,
  ProviderStanding,
  FixtureFilter,
} from './football-provider.js';

/**
 * EmptyFootballProvider
 *
 * Implements the FootballProvider contract with no-op empty responses.
 * Used when running without active provider credentials to prevent mock
 * matches from contaminating production pipelines.
 */
export class EmptyFootballProvider implements FootballProvider {
  readonly providerName = 'empty';

  async getCompetitions(): Promise<ProviderCompetition[]> {
    return [];
  }

  async getFixtures(_filter?: FixtureFilter): Promise<ProviderFixture[]> {
    return [];
  }

  async getFixturesByDate(_date: string, _cycleId?: string): Promise<ProviderFixture[]> {
    return [];
  }

  async getFixtureDetails(_fixtureId: string, _cycleId?: string): Promise<ProviderFixtureDetail | null> {
    return null;
  }

  async getFixtureDetailsBatch(_fixtureIds: string[], _cycleId?: string): Promise<ProviderFixtureDetail[]> {
    return [];
  }

  async getFixtureEvents(_fixtureId: string): Promise<ProviderEvent[]> {
    return [];
  }

  async getStandings(_competitionId: string, _seasonYear: number): Promise<ProviderStanding[]> {
    return [];
  }
}
