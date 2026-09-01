import {
  FootballProvider,
  ProviderCompetition,
  ProviderFixture,
  ProviderFixtureDetail,
  ProviderEvent,
  ProviderStanding,
  FixtureFilter,
} from '../football-provider.js';
import { ApiFootballClient, ApiFootballClientConfig } from './api-football.client.js';
import { ApiFootballMapper } from './api-football.mapper.js';

export class ApiFootballProvider implements FootballProvider {
  readonly providerName = 'api_football';

  private readonly client: ApiFootballClient;

  constructor(config?: Partial<ApiFootballClientConfig>) {
    this.client = new ApiFootballClient({
      apiKey: config?.apiKey || process.env.API_FOOTBALL_KEY || '',
      baseUrl: config?.baseUrl || process.env.API_FOOTBALL_BASE_URL,
      gateway: config?.gateway,
      fetchFn: config?.fetchFn,
    });
  }

  async getCompetitions(): Promise<ProviderCompetition[]> {
    const response = await this.client.getLeagues({ current: true });
    if (!response.success || !response.data?.response) {
      return [];
    }
    return response.data.response.map((item) => ApiFootballMapper.mapLeagueToCompetition(item));
  }

  async getFixtures(filter?: FixtureFilter): Promise<ProviderFixture[]> {
    const leagueId = filter?.competitionId ? parseInt(filter.competitionId.replace(/^comp_/, ''), 10) : undefined;
    const dateStr = filter?.date ? filter.date.slice(0, 10) : undefined;
    const live = filter?.liveOnly ? 'all' : undefined;

    const response = await this.client.getFixtures({
      league: isNaN(leagueId as any) ? undefined : leagueId,
      date: dateStr,
      live,
    });

    if (!response.success || !response.data?.response) {
      return [];
    }

    return response.data.response.map((item) => ApiFootballMapper.mapFixture(item));
  }

  async getFixtureDetails(fixtureId: string): Promise<ProviderFixtureDetail | null> {
    const numericId = parseInt(fixtureId.replace(/^fix_/, ''), 10);
    if (isNaN(numericId)) return null;

    const response = await this.client.getFixtures({ id: numericId });
    if (!response.success || !response.data?.response || response.data.response.length === 0) {
      return null;
    }

    return ApiFootballMapper.mapFixtureDetail(response.data.response[0]);
  }

  async getFixtureEvents(fixtureId: string): Promise<ProviderEvent[]> {
    const details = await this.getFixtureDetails(fixtureId);
    return details?.events || [];
  }

  async getStandings(competitionId: string, seasonYear: number): Promise<ProviderStanding[]> {
    const numericId = parseInt(competitionId.replace(/^comp_/, ''), 10);
    if (isNaN(numericId)) return [];

    const response = await this.client.getStandings(numericId, seasonYear);
    if (!response.success || !response.data?.response || response.data.response.length === 0) {
      return [];
    }

    return ApiFootballMapper.mapStandings(response.data.response[0]);
  }
}
