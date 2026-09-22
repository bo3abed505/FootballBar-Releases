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
  private readonly inFlight = new Map<string, Promise<ProviderFixture[]>>();

  constructor(config?: Partial<ApiFootballClientConfig>) {
    this.client = new ApiFootballClient({
      apiKey: config?.apiKey || process.env.API_FOOTBALL_KEY || '',
      baseUrl: config?.baseUrl || process.env.API_FOOTBALL_BASE_URL,
      gateway: config?.gateway,
      fetchFn: config?.fetchFn,
    });
  }

  getClient(): ApiFootballClient {
    return this.client;
  }

  async getCompetitions(): Promise<ProviderCompetition[]> {
    const response = await this.client.getLeagues({ current: true });
    if (!response.success || !response.data?.response) {
      return [];
    }
    return response.data.response.map((item) => ApiFootballMapper.mapLeagueToCompetition(item));
  }

  /**
   * Fetches full date-wide fixtures across all competitions via GET /fixtures?date=YYYY-MM-DD.
   * Deduplicates concurrent in-flight requests for the same date.
   */
  async getFixturesByDate(date: string, cycleId?: string): Promise<ProviderFixture[]> {
    const dateStr = date.slice(0, 10);
    const key = `date:${dateStr}`;
    const existing = this.inFlight.get(key);
    if (existing) {
      return existing;
    }

    const promise = (async () => {
      try {
        const response = await this.client.getFixturesByDate(dateStr, 'NORMAL', cycleId);
        if (!response.success || !response.data?.response) {
          return [];
        }
        return response.data.response.map((item) => ApiFootballMapper.mapFixture(item));
      } finally {
        this.inFlight.delete(key);
      }
    })();

    this.inFlight.set(key, promise);
    return promise;
  }

  async getFixtures(filter?: FixtureFilter): Promise<ProviderFixture[]> {
    const cycleId = filter?.cycleId;

    // --- Path 1: liveOnly with an allowlist ---
    // Use the single cheap "live=all" endpoint and post-filter in memory.
    if (filter?.liveOnly) {
      const response = await this.client.getFixtures({ live: 'all' }, 'NORMAL', cycleId);
      if (!response.success || !response.data?.response) {
        return [];
      }
      const all = response.data.response.map((item) => ApiFootballMapper.mapFixture(item));
      // If a numeric allowlist was provided, narrow to it; otherwise return everything.
      if (filter.competitionIds && filter.competitionIds.length > 0) {
        const allowed = new Set(filter.competitionIds.map((id) => `comp_${id}`));
        return all.filter((f) => allowed.has(f.competitionId));
      }
      return all;
    }

    // --- Path 2: Date-wide discovery (Single date-wide fetch) ---
    // When date is specified, use a single date-wide request instead of looping over leagues
    if (filter?.date) {
      const dateStr = filter.date.slice(0, 10);
      const all = await this.getFixturesByDate(dateStr, cycleId);
      if (filter.competitionIds && filter.competitionIds.length > 0) {
        const allowed = new Set(filter.competitionIds.map(String));
        return all.filter((f) => {
          const rawLeagueId = f.competition?.providerId || f.competition?.id?.replace(/^comp_/, '') || f.competitionId?.replace(/^comp_/, '');
          return rawLeagueId && allowed.has(rawLeagueId);
        });
      }
      return all;
    }

    // --- Path 3: multiple competition IDs without date (one request per ID) ---
    if (filter?.competitionIds && filter.competitionIds.length > 0) {
      const results: ProviderFixture[] = [];

      for (const numericLeagueId of filter.competitionIds) {
        const response = await this.client.getFixtures({
          league: numericLeagueId,
        }, 'NORMAL', cycleId, numericLeagueId.toString());
        if (!response.success || !response.data?.response) {
          // Log and continue — a single competition failure should not abort all others.
          continue;
        }
        for (const item of response.data.response) {
          results.push(ApiFootballMapper.mapFixture(item));
        }
      }

      return results;
    }

    // --- Path 4: legacy single competitionId (internal comp_* string) ---
    const leagueId = filter?.competitionId
      ? parseInt(filter.competitionId.replace(/^comp_/, ''), 10)
      : undefined;

    const response = await this.client.getFixtures({
      league: isNaN(leagueId as any) ? undefined : leagueId,
    }, 'NORMAL', cycleId, leagueId ? leagueId.toString() : undefined);

    if (!response.success || !response.data?.response) {
      return [];
    }

    return response.data.response.map((item) => ApiFootballMapper.mapFixture(item));
  }


  async getFixtureDetails(fixtureId: string, cycleId?: string): Promise<ProviderFixtureDetail | null> {
    const numericId = parseInt(fixtureId.replace(/^fix_/, ''), 10);
    if (isNaN(numericId)) return null;

    const response = await this.client.getFixtures({ id: numericId }, 'NORMAL', cycleId);
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
