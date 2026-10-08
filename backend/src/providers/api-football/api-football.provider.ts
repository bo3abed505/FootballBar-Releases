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
import { Logger } from '../../diagnostics/logger.js';
import { extractProviderId } from '../../db/provider-alias.js';

function checkApiFootballErrors(response: any): void {
  if (response?.data?.errors) {
    const errs = response.data.errors;
    const hasErrors = Array.isArray(errs)
      ? errs.length > 0
      : typeof errs === 'object' && Object.keys(errs).length > 0;
    if (hasErrors) {
      throw new Error("API-Football Error: " + JSON.stringify(errs));
    }
  }
}

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
    checkApiFootballErrors(response);
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
        checkApiFootballErrors(response);
        if (!response.success || !response.data?.response) {
          if (!response.success) {
            console.warn(`Failed schedule fetch for ${dateStr} via API-Football: ${response.error || 'Request unsuccessful'}`);
            throw new Error(response.error || `[API_FOOTBALL] Request denied or failed with HTTP ${response.httpStatus}`);
          }
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

  /**
   * Fetches fixtures across all competitions for a date range via GET /fixtures?from=YYYY-MM-DD&to=YYYY-MM-DD.
   * Deduplicates concurrent in-flight requests for the same date range.
   */
  async getFixturesByDateRange(fromDate: string, toDate: string, cycleId?: string): Promise<ProviderFixture[]> {
    const fromStr = fromDate.slice(0, 10);
    const toStr = toDate.slice(0, 10);
    const key = `range:${fromStr}:${toStr}`;
    const existing = this.inFlight.get(key);
    if (existing) {
      return existing;
    }

    const promise = (async () => {
      try {
        const response = await this.client.getFixturesByDateRange(fromStr, toStr, 'NORMAL', cycleId);
        checkApiFootballErrors(response);
        if (!response.success || !response.data?.response) {
          if (!response.success) {
            console.warn(`Failed schedule range fetch for ${fromStr}..${toStr} via API-Football: ${response.error || 'Request unsuccessful'}`);
            throw new Error(response.error || `[API_FOOTBALL] Request denied or failed with HTTP ${response.httpStatus}`);
          }
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

    if (filter?.fromDate && filter?.toDate) {
      return this.getFixturesByDateRange(filter.fromDate, filter.toDate, cycleId);
    }

    // --- Path 1: liveOnly with an allowlist ---
    // Use the single cheap "live=all" endpoint and post-filter in memory.
    if (filter?.liveOnly) {
      const response = await this.client.getFixtures({ live: 'all' }, 'NORMAL', cycleId);
      checkApiFootballErrors(response);
      if (!response.success || !response.data?.response) {
        if (!response.success) {
          console.warn(`Failed live fixtures fetch via API-Football: ${response.error || 'Request unsuccessful'}`);
          throw new Error(response.error || `[API_FOOTBALL] Live request failed with HTTP ${response.httpStatus}`);
        }
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
          const rawLeagueId = f.competition?.providerId || (f.competition?.id ? extractProviderId(f.competition.id, 'comp') : '') || (f.competitionId ? extractProviderId(f.competitionId, 'comp') : '');
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
        checkApiFootballErrors(response);
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
      ? parseInt(extractProviderId(filter.competitionId, 'comp'), 10)
      : undefined;

    const response = await this.client.getFixtures({
      league: isNaN(leagueId as any) ? undefined : leagueId,
    }, 'NORMAL', cycleId, leagueId ? leagueId.toString() : undefined);
    checkApiFootballErrors(response);

    if (!response.success || !response.data?.response) {
      return [];
    }

    return response.data.response.map((item) => ApiFootballMapper.mapFixture(item));
  }


  async getFixtureDetails(fixtureId: string, cycleId?: string): Promise<ProviderFixtureDetail | null> {
    const numericId = parseInt(extractProviderId(fixtureId, 'fix'), 10);
    if (isNaN(numericId)) return null;

    const response = await this.client.getFixtures({ id: numericId }, 'NORMAL', cycleId);
    checkApiFootballErrors(response);
    if (!response.success || !response.data?.response || response.data.response.length === 0) {
      return null;
    }

    return ApiFootballMapper.mapFixtureDetail(response.data.response[0]);
  }

  /**
   * Fetches deep match details for multiple fixtures in batches of up to 20 IDs.
   * Implements partial failure isolation: invalid or malformed fixtures are skipped and logged,
   * while all valid fixtures in the batch are mapped and returned.
   */
  async getFixtureDetailsBatch(fixtureIds: string[], cycleId?: string): Promise<ProviderFixtureDetail[]> {
    if (!fixtureIds || fixtureIds.length === 0) {
      return [];
    }

    const results: ProviderFixtureDetail[] = [];

    // Process in batches of up to 20 fixture IDs
    for (let i = 0; i < fixtureIds.length; i += 20) {
      const chunk = fixtureIds.slice(i, i + 20);
      const numericIds = chunk
        .map((id) => parseInt(extractProviderId(id, 'fix'), 10))
        .filter((n) => !isNaN(n));

      if (numericIds.length === 0) continue;

      const response = await this.client.getFixturesByIds(numericIds, 'NORMAL', cycleId);
      checkApiFootballErrors(response);
      if (!response.success || !response.data?.response) {
        continue;
      }

      // Partial failure isolation: parse each fixture individually
      for (const item of response.data.response) {
        try {
          if (!item || !item.fixture?.id) {
            Logger.warn('ApiFootballProvider', 'MalformedBatchFixture', 'Encountered malformed fixture item in batch details response', cycleId);
            continue;
          }
          results.push(ApiFootballMapper.mapFixtureDetail(item));
        } catch (err) {
          Logger.warn('ApiFootballProvider', 'BatchParseError', `Failed to parse fixture detail for fixture ${item?.fixture?.id}: ${err}`, cycleId, {
            error: err instanceof Error ? err.message : String(err),
            fixtureId: item?.fixture?.id,
          });
        }
      }
    }

    return results;
  }

  async getFixtureEvents(fixtureId: string): Promise<ProviderEvent[]> {
    const details = await this.getFixtureDetails(fixtureId);
    return details?.events || [];
  }

  async getStandings(competitionId: string, seasonYear: number): Promise<ProviderStanding[]> {
    const numericId = parseInt(extractProviderId(competitionId, 'comp'), 10);
    if (isNaN(numericId)) return [];

    const response = await this.client.getStandings(numericId, seasonYear);
    checkApiFootballErrors(response);
    if (!response.success || !response.data?.response || response.data.response.length === 0) {
      return [];
    }

    return ApiFootballMapper.mapStandings(response.data.response[0]);
  }
}
