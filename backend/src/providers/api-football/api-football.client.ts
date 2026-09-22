import { RequestGateway } from '../../gateway/request-gateway.js';
import { GatewayResponse, RequestPriority } from '../../gateway/types.js';
import {
  ApiFootballEnvelope,
  ApiFootballStatusResponse,
  ApiFootballLeagueItem,
  ApiFootballFixtureItem,
  ApiFootballStandingItem,
} from './api-football.dto.js';

export interface ApiFootballClientConfig {
  apiKey: string;
  baseUrl?: string;
  gateway?: RequestGateway;
  fetchFn?: typeof fetch;
}

export class ApiFootballClient {
  private readonly apiKey: string;
  private readonly baseUrl: string;
  private readonly gateway: RequestGateway;
  private readonly fetchFn: typeof fetch;
  private readonly inFlight = new Map<string, Promise<any>>();

  constructor(config: ApiFootballClientConfig) {
    this.apiKey = config.apiKey || '';
    this.baseUrl = (config.baseUrl || 'https://v3.football.api-sports.io').replace(/\/+$/, '');
    this.gateway = config.gateway || new RequestGateway();
    this.fetchFn = config.fetchFn || globalThis.fetch;
  }

  /**
   * Internal request executor routing exclusively through RequestGateway.
   * In-flight single-flight promise coalescing deduplicates identical concurrent requests.
   */
  private async executeRequest<T>(
    endpoint: string,
    operation: string,
    priority: RequestPriority = 'NORMAL',
    cycleId?: string,
    competitionId?: string
  ): Promise<GatewayResponse<ApiFootballEnvelope<T>>> {
    const key = endpoint;
    const existing = this.inFlight.get(key);
    if (existing) {
      return existing as Promise<GatewayResponse<ApiFootballEnvelope<T>>>;
    }

    const promise = (async () => {
      try {
        const url = `${this.baseUrl}${endpoint.startsWith('/') ? '' : '/'}${endpoint}`;

        return await this.gateway.execute<ApiFootballEnvelope<T>>(
          {
            provider: 'api_football',
            endpoint,
            operation,
            priority,
            cycleId,
            competitionId,
          },
          async () => {
            const response = await this.fetchFn(url, {
              method: 'GET',
              headers: {
                'x-apisports-key': this.apiKey,
                'Accept': 'application/json',
              },
            });

            const data = (await response.json()) as ApiFootballEnvelope<T>;
            return {
              status: response.status,
              headers: response.headers,
              data,
            };
          }
        );
      } finally {
        this.inFlight.delete(key);
      }
    })();

    this.inFlight.set(key, promise);
    return promise;
  }

  async getStatus(priority: RequestPriority = 'NORMAL', cycleId?: string): Promise<GatewayResponse<ApiFootballEnvelope<ApiFootballStatusResponse>>> {
    return this.executeRequest<ApiFootballStatusResponse>('/status', 'status_probe', priority, cycleId);
  }

  async getLeagues(
    params?: { id?: number; current?: boolean; season?: number },
    priority: RequestPriority = 'NORMAL',
    cycleId?: string
  ): Promise<GatewayResponse<ApiFootballEnvelope<ApiFootballLeagueItem[]>>> {
    const query = new URLSearchParams();
    if (params?.id) query.set('id', params.id.toString());
    if (params?.current !== undefined) query.set('current', params.current ? 'true' : 'false');
    if (params?.season) query.set('season', params.season.toString());

    const qs = query.toString();
    const endpoint = `/leagues${qs ? `?${qs}` : ''}`;
    return this.executeRequest<ApiFootballLeagueItem[]>(endpoint, 'get_leagues', priority, cycleId, params?.id ? params.id.toString() : undefined);
  }

  /**
   * Fetches full date-wide fixtures across all competitions via GET /fixtures?date=YYYY-MM-DD.
   */
  async getFixturesByDate(
    date: string,
    priority: RequestPriority = 'NORMAL',
    cycleId?: string
  ): Promise<GatewayResponse<ApiFootballEnvelope<ApiFootballFixtureItem[]>>> {
    const dateStr = date.slice(0, 10);
    const endpoint = `/fixtures?date=${dateStr}`;
    return this.executeRequest<ApiFootballFixtureItem[]>(endpoint, 'get_fixtures', priority, cycleId);
  }

  /**
   * Fetches multiple fixtures in a single batch request via GET /fixtures?ids={id1}-{id2}-...-{idN}
   * Supports up to 20 fixture IDs per request.
   */
  async getFixturesByIds(
    fixtureIds: (number | string)[],
    priority: RequestPriority = 'NORMAL',
    cycleId?: string
  ): Promise<GatewayResponse<ApiFootballEnvelope<ApiFootballFixtureItem[]>>> {
    const cleanIds = fixtureIds
      .map((id) => (typeof id === 'string' ? id.replace(/^fix_/, '') : id.toString()))
      .filter((id) => id.trim().length > 0 && !isNaN(Number(id)))
      .slice(0, 20);

    if (cleanIds.length === 0) {
      return {
        success: true,
        httpStatus: 200,
        responseTimeMs: 0,
        ledgerId: '',
        data: {
          get: 'fixtures',
          parameters: { ids: '' },
          errors: [],
          results: 0,
          paging: { current: 1, total: 1 },
          response: [],
        },
      };
    }

    const idsParam = cleanIds.join('-');
    const endpoint = `/fixtures?ids=${idsParam}`;
    return this.executeRequest<ApiFootballFixtureItem[]>(endpoint, 'get_fixtures_batch', priority, cycleId);
  }

  async getFixtures(
    params: { id?: number; ids?: string | (number | string)[]; date?: string; league?: number; season?: number; live?: string },
    priority: RequestPriority = 'NORMAL',
    cycleId?: string,
    competitionId?: string
  ): Promise<GatewayResponse<ApiFootballEnvelope<ApiFootballFixtureItem[]>>> {
    if (params.date && !params.league && !params.id && !params.ids && !params.season && !params.live) {
      return this.getFixturesByDate(params.date, priority, cycleId);
    }

    const query = new URLSearchParams();
    if (params.id) query.set('id', params.id.toString());
    if (params.ids) {
      const idsStr = Array.isArray(params.ids)
        ? params.ids.map((id) => (typeof id === 'string' ? id.replace(/^fix_/, '') : id.toString())).join('-')
        : params.ids;
      query.set('ids', idsStr);
    }
    if (params.date) query.set('date', params.date);
    if (params.league) query.set('league', params.league.toString());
    if (params.season) query.set('season', params.season.toString());
    if (params.live) query.set('live', params.live);

    const qs = query.toString();
    const endpoint = `/fixtures${qs ? `?${qs}` : ''}`;
    const effectiveCompId = competitionId ?? (params.league ? params.league.toString() : undefined);
    return this.executeRequest<ApiFootballFixtureItem[]>(endpoint, 'get_fixtures', priority, cycleId, effectiveCompId);
  }

  async getStandings(
    league: number,
    season: number,
    priority: RequestPriority = 'NORMAL',
    cycleId?: string
  ): Promise<GatewayResponse<ApiFootballEnvelope<ApiFootballStandingItem[]>>> {
    const endpoint = `/standings?league=${league}&season=${season}`;
    return this.executeRequest<ApiFootballStandingItem[]>(endpoint, 'get_standings', priority, cycleId, league.toString());
  }
}
