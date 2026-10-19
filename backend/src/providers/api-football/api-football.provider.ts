import { IDataProvider, NormalizedMatch, NormalizedLiveMatch, NormalizedMatchDetails, MatchStatus } from '../types.js';
import { ApiFootballClient, ApiFootballClientConfig } from './api-football.client.js';
import { ApiFootballFixtureItem } from './api-football.dto.js';
import { ProviderNetworkError } from '../errors.js';

export class ApiFootballProvider implements IDataProvider {
  public readonly name = 'api-football';
  public readonly providerName = 'api_football';
  private client: ApiFootballClient;

  constructor(apiKeyOrConfig: string | ApiFootballClientConfig, tier?: string) {
    if (typeof apiKeyOrConfig === 'string') {
      this.client = new ApiFootballClient({ apiKey: apiKeyOrConfig });
    } else {
      this.client = new ApiFootballClient(apiKeyOrConfig);
    }
  }

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

  public async getFixturesByDate(date: string, cycleId?: string): Promise<ApiFootballFixtureItem[]> {
    try {
      const dateStr = date.includes('-') ? date : `${date.substring(0, 4)}-${date.substring(4, 6)}-${date.substring(6, 8)}`;
      const response = await this.client.getFixturesByDate(dateStr, 'NORMAL', cycleId);
      if (!response.success || !response.data?.response) {
        throw new ProviderNetworkError('Failed to fetch fixtures by date from API-Football');
      }
      return response.data.response;
    } catch (e: any) {
      throw new ProviderNetworkError(`API-Football getFixturesByDate error: ${e.message}`);
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
