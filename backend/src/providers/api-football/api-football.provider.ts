import { IDataProvider, NormalizedMatch, NormalizedLiveMatch, NormalizedMatchDetails, MatchStatus } from '../types.js';
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
import { ApiFootballFixtureItem } from './api-football.dto.js';
import { ApiFootballMapper } from './api-football.mapper.js';
import { ProviderNetworkError } from '../errors.js';
import { Logger } from '../../diagnostics/logger.js';
import { extractProviderId } from '../../db/provider-alias.js';

function checkApiFootballErrors(response: any): void {
  if (response?.data?.errors) {
    const errs = response.data.errors;
    const hasErrors = Array.isArray(errs)
      ? errs.length > 0
      : typeof errs === 'object' && Object.keys(errs).length > 0;
    if (hasErrors) {
      throw new Error('API-Football Error: ' + JSON.stringify(errs));
    }
  }
}

export class ApiFootballProvider implements IDataProvider, FootballProvider {
  public readonly name = 'api-football';
  public readonly providerName = 'api_football';
  private client: ApiFootballClient;
  private tier: string;
  private readonly inFlight = new Map<string, Promise<ProviderFixture[]>>();

  constructor(apiKeyOrConfig?: string | Partial<ApiFootballClientConfig>, tier?: string) {
    this.tier = tier || (typeof apiKeyOrConfig === 'object' && (apiKeyOrConfig as any)?.tier) || process.env.API_FOOTBALL_TIER || 'paid';
    if (typeof apiKeyOrConfig === 'string') {
      this.client = new ApiFootballClient({ apiKey: apiKeyOrConfig });
    } else {
      this.client = new ApiFootballClient({
        apiKey: apiKeyOrConfig?.apiKey || process.env.API_FOOTBALL_KEY || '',
        baseUrl: apiKeyOrConfig?.baseUrl || process.env.API_FOOTBALL_BASE_URL,
        gateway: apiKeyOrConfig?.gateway,
        fetchFn: apiKeyOrConfig?.fetchFn,
      });
    }
  }

  public getClient(): ApiFootballClient {
    return this.client;
  }

  // --- IDataProvider implementation ---

  public async getLiveMatches(): Promise<NormalizedLiveMatch[]> {
    try {
      const response = await this.client.getFixtures({ live: 'all' }, 'CRITICAL');
      if (!response.success || !response.data?.response) {
        throw new ProviderNetworkError('Failed to fetch live matches from API-Football');
      }
      return this.normalizeFixtures(response.data.response);
    } catch (e: any) {
      throw new ProviderNetworkError(`API-Football getLiveMatches error: ${e.message}`);
    }
  }

  public async getDailyMatches(date: string): Promise<NormalizedMatch[]> {
    try {
      const dateStr = date.includes('-') ? date : `${date.substring(0, 4)}-${date.substring(4, 6)}-${date.substring(6, 8)}`;
      const response = await this.client.getFixturesByDate(dateStr);
      if (!response.success || !response.data?.response) {
        throw new ProviderNetworkError('Failed to fetch daily matches from API-Football');
      }
      return this.normalizeFixtures(response.data.response);
    } catch (e: any) {
      throw new ProviderNetworkError(`API-Football getDailyMatches error: ${e.message}`);
    }
  }

  public async getMatchDetails(providerMatchId: string): Promise<NormalizedMatchDetails> {
    try {
      const response = await this.client.getFixturesByIds([providerMatchId], 'CRITICAL');
      if (!response.success || !response.data?.response || response.data.response.length === 0) {
        throw new ProviderNetworkError('Failed to fetch match details from API-Football');
      }
      const fixture = response.data.response[0];
      return this.normalizeMatchDetails(fixture);
    } catch (e: any) {
      throw new ProviderNetworkError(`API-Football getMatchDetails error: ${e.message}`);
    }
  }

  // --- FootballProvider implementation ---

  public async getCompetitions(): Promise<ProviderCompetition[]> {
    const response = await this.client.getLeagues({ current: true });
    checkApiFootballErrors(response);
    if (!response.success || !response.data?.response) {
      return [];
    }
    return response.data.response.map((item) => ApiFootballMapper.mapLeagueToCompetition(item));
  }

  public async getFixtures(filter?: FixtureFilter): Promise<ProviderFixture[]> {
    const cycleId = filter?.cycleId;

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
      if (filter.competitionIds && filter.competitionIds.length > 0) {
        const allowed = new Set(filter.competitionIds.map((id) => `comp_${id}`));
        return all.filter((f) => allowed.has(f.competitionId));
      }
      return all;
    }

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

    if (filter?.competitionIds && filter.competitionIds.length > 0) {
      const results: ProviderFixture[] = [];
      for (const numericLeagueId of filter.competitionIds) {
        const response = await this.client.getFixtures({
          league: numericLeagueId,
        }, 'NORMAL', cycleId, numericLeagueId.toString());
        checkApiFootballErrors(response);
        if (!response.success || !response.data?.response) {
          continue;
        }
        for (const item of response.data.response) {
          results.push(ApiFootballMapper.mapFixture(item));
        }
      }
      return results;
    }

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

  public async getFixturesByDate(date: string, cycleId?: string): Promise<ProviderFixture[]> {
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

  public async getFixturesByDateRange(fromDate: string, toDate: string, cycleId?: string): Promise<ProviderFixture[]> {
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

  public async getFixtureDetails(fixtureId: string, cycleId?: string): Promise<ProviderFixtureDetail | null> {
    const numericId = parseInt(extractProviderId(fixtureId, 'fix'), 10);
    if (isNaN(numericId)) return null;

    const response = await this.client.getFixtures({ id: numericId }, 'NORMAL', cycleId);
    checkApiFootballErrors(response);
    if (!response.success || !response.data?.response || response.data.response.length === 0) {
      return null;
    }

    return ApiFootballMapper.mapFixtureDetail(response.data.response[0]);
  }

  public async getFixtureDetailsBatch(fixtureIds: string[], cycleId?: string): Promise<ProviderFixtureDetail[]> {
    if (!fixtureIds || fixtureIds.length === 0) {
      return [];
    }

    const results: ProviderFixtureDetail[] = [];
    const isFreeTier = this.tier === 'free' || process.env.API_FOOTBALL_TIER === 'free';

    if (isFreeTier) {
      const candidates = fixtureIds.slice(0, 5);
      for (const id of candidates) {
        try {
          const detail = await this.getFixtureDetails(id, cycleId);
          if (detail) {
            results.push(detail);
          }
          await new Promise((resolve) => setTimeout(resolve, 500));
        } catch (err: any) {
          Logger.warn('ApiFootballProvider', 'SingleDetailError', `Failed fetching individual fixture detail for ${id}: ${err?.message || err}`, cycleId);
        }
      }
      return results;
    }

    for (let i = 0; i < fixtureIds.length; i += 20) {
      const chunk = fixtureIds.slice(i, i + 20);
      const numericIds = chunk
        .map((id) => parseInt(extractProviderId(id, 'fix'), 10))
        .filter((n) => !isNaN(n));

      if (numericIds.length === 0) continue;

      try {
        const response = await this.client.getFixturesByIds(numericIds, 'NORMAL', cycleId);
        checkApiFootballErrors(response);
        if (!response.success || !response.data?.response) {
          continue;
        }

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
      } catch (err: any) {
        Logger.warn('ApiFootballProvider', 'BatchFetchError', `Failed batch fixtures fetch: ${err?.message || err}`, cycleId);
      }
    }

    return results;
  }

  public async getFixtureEvents(fixtureId: string): Promise<ProviderEvent[]> {
    const details = await this.getFixtureDetails(fixtureId);
    return details?.events || [];
  }

  public async getStandings(competitionId: string, seasonYear: number): Promise<ProviderStanding[]> {
    const numericId = parseInt(extractProviderId(competitionId, 'comp'), 10);
    if (isNaN(numericId)) return [];

    const response = await this.client.getStandings(numericId, seasonYear);
    checkApiFootballErrors(response);
    if (!response.success || !response.data?.response || response.data.response.length === 0) {
      return [];
    }

    return ApiFootballMapper.mapStandings(response.data.response[0]);
  }

  private normalizeFixtures(fixtures: ApiFootballFixtureItem[]): NormalizedMatch[] {
    return fixtures.map(fixture => {
      const fixId = String(fixture.fixture.id);
      const compId = String(fixture.league.id);
      const homeId = String(fixture.teams.home.id);
      const awayId = String(fixture.teams.away.id);
      return {
        providerMatchId: fixId.startsWith('fix_') ? fixId : `fix_${fixId}`,
        competitionId: compId.startsWith('comp_') ? compId : `comp_${compId}`,
        competitionName: fixture.league.name,
        competitionLogoUrl: fixture.league.logo,
        homeTeam: {
          providerId: homeId.startsWith('team_') ? homeId : `team_${homeId}`,
          name: fixture.teams.home.name,
          score: fixture.goals.home ?? null,
          logoUrl: fixture.teams.home.logo,
        },
        awayTeam: {
          providerId: awayId.startsWith('team_') ? awayId : `team_${awayId}`,
          name: fixture.teams.away.name,
          score: fixture.goals.away ?? null,
          logoUrl: fixture.teams.away.logo,
        },
        time: {
          status: this.mapStatus(fixture.fixture.status.short),
          minute: fixture.fixture.status.elapsed,
          addedTime: fixture.fixture.status.extra,
          displayClock: fixture.fixture.status.elapsed ? `${fixture.fixture.status.elapsed}'` : fixture.fixture.status.short,
          scheduledKickoff: fixture.fixture.date,
        }
      };
    });
  }

  private normalizeMatchDetails(fixture: ApiFootballFixtureItem): NormalizedMatchDetails {
    const base = this.normalizeFixtures([fixture])[0];
    return {
      providerMatchId: base.providerMatchId,
      homeTeam: base.homeTeam,
      awayTeam: base.awayTeam,
      time: base.time,
      stats: fixture.statistics,
      lineups: fixture.lineups
    };
  }

  private mapStatus(shortStatus: string): MatchStatus {
    const map: Record<string, MatchStatus> = {
      'TBD': MatchStatus.SCHEDULED,
      'NS': MatchStatus.SCHEDULED,
      '1H': MatchStatus.FIRST_HALF,
      'HT': MatchStatus.HALFTIME,
      '2H': MatchStatus.SECOND_HALF,
      'ET': MatchStatus.EXTRA_TIME,
      'BT': MatchStatus.EXTRA_TIME_HALFTIME,
      'P': MatchStatus.PENALTIES,
      'SUSP': MatchStatus.SUSPENDED,
      'INT': MatchStatus.INTERRUPTED,
      'FT': MatchStatus.FINISHED,
      'AET': MatchStatus.FINISHED_AET,
      'PEN': MatchStatus.FINISHED_PEN,
      'PST': MatchStatus.POSTPONED,
      'CANC': MatchStatus.CANCELLED,
      'ABD': MatchStatus.ABANDONED,
      'AWD': MatchStatus.FINISHED,
      'WO': MatchStatus.FINISHED,
      'LIVE': MatchStatus.FIRST_HALF, // Generic live fallback
    };
    return map[shortStatus] || MatchStatus.UNKNOWN;
  }
}
