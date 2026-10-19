import { IDataProvider, NormalizedMatch, NormalizedLiveMatch, NormalizedMatchDetails, MatchStatus } from '../types.js';
import { ProviderChallengeError, ProviderNetworkError, ProviderSchemaError, ProviderResourceNotFoundError } from '../errors.js';
import { FotmobMatchesResponseSchema, FotmobMatchesResponse } from './schemas/matches.schema.js';
import { FotmobMatchDetailsResponseSchema, FotmobMatchDetailsResponse } from './schemas/match-details.schema.js';
import { CircuitBreaker } from '../../resilience/circuit-breaker.js';

/** Well-known clubs whose conventional TLA cannot be derived algorithmically. */
const TLA_OVERRIDES: Record<string, string> = {
  'paris saint-germain': 'PSG',
  'paris saint germain': 'PSG',
  'psg': 'PSG',
  'manchester city': 'MCI',
  'man city': 'MCI',
  'manchester united': 'MUN',
  'man united': 'MUN',
  'man utd': 'MUN',
  'tottenham hotspur': 'TOT',
  'tottenham': 'TOT',
  'wolverhampton wanderers': 'WOL',
  'wolves': 'WOL',
  'nottingham forest': 'NFO',
  'aston villa': 'AVL',
  'bayern munich': 'FCB',
  'bayern münchen': 'FCB',
  'fc bayern münchen': 'FCB',
  'borussia dortmund': 'BVB',
  'borussia mönchengladbach': 'BMG',
  'bayer leverkusen': 'B04',
  'rb leipzig': 'RBL',
  'inter': 'INT',
  'inter milan': 'INT',
  'internazionale': 'INT',
  'ac milan': 'MIL',
  'milan': 'MIL',
  'juventus': 'JUV',
  'atletico madrid': 'ATM',
  'atlético madrid': 'ATM',
  'real madrid': 'RMA',
  'barcelona': 'BAR',
  'fc barcelona': 'BAR',
  'olympique lyonnais': 'LYO',
  'lyon': 'LYO',
  'olympique marseille': 'MAR',
  'marseille': 'MAR',
};

/** Prefixes/suffixes that carry no identity and should be ignored when deriving a TLA. */
const TLA_STOPWORDS = new Set([
  'fc', 'cf', 'ac', 'as', 'sc', 'afc', 'ssc', 'ss', 'cd', 'sd', 'ud', 'rc', 'fk', 'sk', 'nk', 'bk', 'if', 'ik',
  'al', 'el', 'de', 'del', 'la', 'le', 'the', 'club', 'sv', 'vfb', 'vfl', 'tsg', 'calcio',
]);

/** Event markers that are not real incidents and must not render as timeline rows. */
const NON_INCIDENT_EVENT_TYPES = new Set(['half', 'addedtime', 'halftime', 'fulltime', 'periodstart', 'periodend']);

export class FotmobProvider implements IDataProvider {
  public readonly name = 'fotmob';
  public readonly circuitBreaker: CircuitBreaker;
  
  private readonly baseUrl: string;
  private readonly headers = {
    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
    'Accept': 'application/json',
    'Referer': 'https://www.fotmob.com/',
  };

  constructor(options?: { circuitBreaker?: CircuitBreaker; baseUrl?: string }) {
    this.baseUrl = options?.baseUrl ?? 'https://www.fotmob.com/api/data';
    this.circuitBreaker = options?.circuitBreaker ?? new CircuitBreaker({ failureThreshold: 1, resetTimeoutMs: 60000 });
  }

  private formatDate(d: Date): string {
    const y = d.getFullYear();
    const m = String(d.getMonth() + 1).padStart(2, '0');
    const day = String(d.getDate()).padStart(2, '0');
    return `${y}${m}${day}`;
  }

  /**
   * Derives a 3-letter uppercase team code (TLA).
   *   "Manchester City"      -> "MCI"
   *   "Paris Saint-Germain"  -> "PSG"
   *   "Al-Raed"              -> "RAE"
   *   "Arsenal"              -> "ARS"
   */
  public static deriveTla(name: string | null | undefined, explicit?: string | null): string {
    const exp = (explicit || '').trim();
    if (/^[A-Za-z0-9]{2,4}$/.test(exp)) {
      return exp.toUpperCase().slice(0, 3);
    }

    const raw = String(name || '').trim();
    if (!raw) return 'TBD';

    const override = TLA_OVERRIDES[raw.toLowerCase()];
    if (override) return override;

    const ascii = raw.normalize('NFD').replace(/[\u0300-\u036f]/g, '');
    const allWords = ascii
      .split(/[\s\-–_.'’/&]+/)
      .map((w) => w.replace(/[^A-Za-z0-9]/g, ''))
      .filter((w) => w.length > 0);

    let words = allWords.filter((w) => !TLA_STOPWORDS.has(w.toLowerCase()) && !/^\d+$/.test(w));
    if (words.length === 0) words = allWords;
    if (words.length === 0) return raw.slice(0, 3).toUpperCase().padEnd(3, 'X');

    let code: string;
    if (words.length === 1) {
      code = words[0].slice(0, 3);
    } else if (words.length === 2) {
      code = words[0][0] + words[1].slice(0, 2);
    } else {
      code = words.slice(0, 3).map((w) => w[0]).join('');
    }

    return code.toUpperCase().padEnd(3, 'X');
  }

  public async getLiveMatches(): Promise<NormalizedLiveMatch[]> {
    const now = new Date();
    const datesToQuery = [this.formatDate(now)];
    
    // Date Crossing for Night Fixtures
    if (now.getHours() < 3) {
      const yesterday = new Date(now.getTime() - 86400000);
      datesToQuery.push(this.formatDate(yesterday));
    }
    
    // Deduplication step
    const allMatchesMap = new Map<string, NormalizedMatch>();
    const dailyResults = await Promise.all(datesToQuery.map(d => this.getDailyMatches(d)));
    
    for (const daily of dailyResults) {
      for (const m of daily) {
        allMatchesMap.set(m.providerMatchId, m);
      }
    }
    
    const allLive: NormalizedLiveMatch[] = [];
    for (const m of allMatchesMap.values()) {
      if ([
        MatchStatus.FIRST_HALF, MatchStatus.SECOND_HALF, MatchStatus.HALFTIME,
        MatchStatus.EXTRA_TIME, MatchStatus.EXTRA_TIME_HALFTIME,
        MatchStatus.PENALTIES, MatchStatus.SUSPENDED, MatchStatus.INTERRUPTED
      ].includes(m.time.status)) {
        allLive.push(m);
      }
    }
    return allLive;
  }

  public async getDailyMatches(date: string): Promise<NormalizedMatch[]> {
    const cleanDate = date.replace(/-/g, '');
    let raw: unknown;
    try {
      raw = await this.fetchJson(`/matches?date=${cleanDate}`);
    } catch (err) {
      if (err instanceof ProviderResourceNotFoundError && !this.baseUrl.includes('/matches')) {
        raw = await this.fetchJson(`https://www.fotmob.com/api/matches?date=${cleanDate}`);
      } else {
        throw err;
      }
    }
    const parsed = FotmobMatchesResponseSchema.safeParse(raw);
    if (!parsed.success) {
      throw new ProviderSchemaError(`Fotmob schema validation failed: ${parsed.error.message}`);
    }
    return this.normalizeMatches(parsed.data);
  }

  public async getMatchDetails(providerMatchId: string): Promise<NormalizedMatchDetails> {
    let raw: unknown;
    try {
      raw = await this.fetchJson(`/matchDetails?matchId=${providerMatchId}`);
    } catch (err) {
      if (err instanceof ProviderResourceNotFoundError && !this.baseUrl.includes('/matchDetails')) {
        raw = await this.fetchJson(`https://www.fotmob.com/api/matchDetails?matchId=${providerMatchId}`);
      } else {
        throw err;
      }
    }
    const parsed = FotmobMatchDetailsResponseSchema.safeParse(raw);
    if (!parsed.success) {
      throw new ProviderSchemaError(`Fotmob match-details schema validation failed: ${parsed.error.message}`);
    }
    return this.normalizeMatchDetails(parsed.data);
  }

  private async fetchJson(pathOrUrl: string): Promise<unknown> {
    return this.circuitBreaker.execute(async () => {
      const controller = new AbortController();
      const timeoutId = setTimeout(() => controller.abort(), 8000);

      try {
        const url = pathOrUrl.startsWith('http') ? pathOrUrl : `${this.baseUrl}${pathOrUrl}`;
        const response = await fetch(url, { 
          headers: this.headers,
          signal: controller.signal
        });

        if (response.status === 404) {
          throw new ProviderResourceNotFoundError(`FotMob returned 404 Not Found`);
        }
        if (response.status === 403 || response.status === 429 || response.status === 503) {
          throw new ProviderChallengeError(`FotMob blocked request (${response.status})`);
        }
        if (!response.ok) {
          throw new ProviderNetworkError(`Fotmob HTTP ${response.status}`);
        }
        
        const text = await response.text();
        const contentType = response.headers.get('content-type') || '';
        
        if (text.trim().toLowerCase().startsWith('<') && contentType.includes('text/html')) {
           throw new ProviderChallengeError('FotMob returned HTML challenge block');
        }
        
        return JSON.parse(text);
      } catch (e: any) {
        if (e.name === 'AbortError') {
          throw new ProviderNetworkError('Fotmob fetch timed out after 8000ms');
        }
        if (
          e instanceof ProviderChallengeError || 
          e instanceof ProviderNetworkError || 
          e instanceof ProviderResourceNotFoundError
        ) {
           throw e;
        }
        throw new ProviderNetworkError(`Fotmob fetch error: ${e.message}`);
      } finally {
        clearTimeout(timeoutId);
      }
    });
  }

  private normalizeMatches(data: FotmobMatchesResponse): NormalizedMatch[] {
    const normalized: NormalizedMatch[] = [];
    for (const league of data.leagues) {
      // Stage/group IDs (e.g. UCL league-phase 943230) have no logo; use the parent league (primaryId 42).
      const logoLeagueId = league.primaryId || league.id;
      const ccode = league.ccode ? league.ccode.trim().toUpperCase() : null;

      for (const match of league.matches) {
        const parsedTime = this.parseLiveTime(match.status);
        normalized.push({
          providerMatchId: match.id,
          competitionId: String(league.primaryId || league.id),
          competitionName: league.name,
          competitionLogoUrl: `https://images.fotmob.com/image_resources/logo/leaguelogo/dark/${logoLeagueId}.png`,
          competitionCountry: ccode,
          competitionCode: ccode,
          homeTeam: {
            providerId: match.home.id,
            name: match.home.name,
            shortName: FotmobProvider.deriveTla(match.home.longName || match.home.name, (match.home as any).tla ?? null),
            score: match.home.score ?? null,
            logoUrl: `https://images.fotmob.com/image_resources/logo/teamlogo/${match.home.id}.png`,
          },
          awayTeam: {
            providerId: match.away.id,
            name: match.away.name,
            shortName: FotmobProvider.deriveTla(match.away.longName || match.away.name, (match.away as any).tla ?? null),
            score: match.away.score ?? null,
            logoUrl: `https://images.fotmob.com/image_resources/logo/teamlogo/${match.away.id}.png`,
          },
          time: {
            status: parsedTime.matchStatus,
            minute: parsedTime.minute,
            addedTime: parsedTime.addedTime,
            displayClock: parsedTime.displayClock,
            scheduledKickoff: match.status.utcTime ?? '',
          }
        });
      }
    }
    return normalized;
  }

  private normalizeMatchDetails(data: FotmobMatchDetailsResponse): NormalizedMatchDetails {
    let teams: Array<{ id: string; name: string; score?: number | null }> | null | undefined = data.header?.teams;

    // Fallback to general.homeTeam/awayTeam only when the header teams branch is absent entirely.
    if (teams == null) {
      const gh = data.general.homeTeam;
      const ga = data.general.awayTeam;
      if (gh?.id != null && gh?.name && ga?.id != null && ga?.name) {
        teams = [
          { id: String(gh.id), name: String(gh.name), score: null },
          { id: String(ga.id), name: String(ga.name), score: null },
        ];
      }
    }

    if (!teams || teams.length < 2) {
      throw new ProviderSchemaError('Fotmob match-details missing valid teams header');
    }
    const homeTeamRaw = teams[0];
    const awayTeamRaw = teams[1];

    const statusObj = data.header?.status;
    const parsedTime = statusObj
      ? this.parseLiveTime(statusObj)
      : {
          matchStatus: data.general.finished ? MatchStatus.FINISHED : data.general.started ? MatchStatus.FIRST_HALF : MatchStatus.SCHEDULED,
          minute: data.general.finished ? 90 : null,
          addedTime: null,
          displayClock: data.general.finished ? 'FT' : '',
        };

    // Scores: header.teams[].score first, then parse header.status.scoreStr ("2 - 1").
    let homeScore: number | null = homeTeamRaw.score ?? null;
    let awayScore: number | null = awayTeamRaw.score ?? null;
    if ((homeScore == null || awayScore == null) && statusObj?.scoreStr) {
      const m = String(statusObj.scoreStr).match(/(\d+)\s*[-–:]\s*(\d+)/);
      if (m) {
        homeScore = homeScore ?? parseInt(m[1], 10);
        awayScore = awayScore ?? parseInt(m[2], 10);
      }
    }

    const events = this.extractEvents(data, homeTeamRaw.id, awayTeamRaw.id);
    const stats = this.extractStats(data, homeTeamRaw.id, awayTeamRaw.id);
    const lineups = this.extractLineups(data, homeTeamRaw.id, awayTeamRaw.id);

    return {
      providerMatchId: data.general.matchId,
      homeTeam: {
        providerId: homeTeamRaw.id,
        name: homeTeamRaw.name,
        shortName: FotmobProvider.deriveTla(homeTeamRaw.name),
        score: homeScore,
        logoUrl: `https://images.fotmob.com/image_resources/logo/teamlogo/${homeTeamRaw.id}.png`,
      },
      awayTeam: {
        providerId: awayTeamRaw.id,
        name: awayTeamRaw.name,
        shortName: FotmobProvider.deriveTla(awayTeamRaw.name),
        score: awayScore,
        logoUrl: `https://images.fotmob.com/image_resources/logo/teamlogo/${awayTeamRaw.id}.png`,
      },
      time: {
        status: parsedTime.matchStatus,
        minute: parsedTime.minute,
        addedTime: parsedTime.addedTime,
        displayClock: parsedTime.displayClock,
        scheduledKickoff: data.general.matchTimeUTCDate ?? statusObj?.utcTime ?? '',
      },
      // Flattened parent-fixture status (consumed by SyncService.syncFixtureDetails)
      state: parsedTime.matchStatus,
      homeScore,
      awayScore,
      elapsedMinute: parsedTime.minute,
      stats, 
      lineups,
      events,
    };
  }

  private extractEvents(data: FotmobMatchDetailsResponse, homeTeamId: string, awayTeamId: string): any[] {
    const rawEvents = data.content?.matchFacts?.events?.events ??
      data.content?.matchFacts?.events ??
      data.header?.events;

    if (!rawEvents) return [];

    let eventList: any[] = [];
    if (Array.isArray(rawEvents)) {
      eventList = rawEvents;
    } else if (typeof rawEvents === 'object') {
      if (Array.isArray(rawEvents.home)) {
        eventList.push(...rawEvents.home.map((e: any) => ({ ...e, teamId: homeTeamId })));
      }
      if (Array.isArray(rawEvents.away)) {
        eventList.push(...rawEvents.away.map((e: any) => ({ ...e, teamId: awayTeamId })));
      }
      if (Array.isArray(rawEvents.events)) {
        eventList.push(...rawEvents.events);
      }
    }

    const results: any[] = [];
    eventList.forEach((ev, idx) => {
      if (!ev || typeof ev !== 'object') return;

      const rawType = String(ev.type || ev.eventType || 'Event');
      const typeLower = rawType.toLowerCase().replace(/[\s_-]/g, '');

      // Drop non-incident markers ("Half", "AddedTime") that would render as blank minute rows.
      if (NON_INCIDENT_EVENT_TYPES.has(typeLower)) return;

      const teamId = typeof ev.isHome === 'boolean'
        ? (ev.isHome ? homeTeamId : awayTeamId)
        : (ev.teamId ? String(ev.teamId) : (ev.team?.id ? String(ev.team.id) : homeTeamId));

      let type = rawType;
      let detail: string | null = null;
      let playerName: string | null = ev.player?.name ?? ev.nameStr ?? ev.playerName ?? ev.player?.fullName ?? null;
      let playerId: string | null = ev.player?.id ? String(ev.player.id) : (ev.playerId ? String(ev.playerId) : null);
      let assistName: string | null = ev.assist?.name ?? ev.assistStr ?? ev.assistPlayerName ?? null;
      let assistPlayerId: string | null = ev.assist?.id ? String(ev.assist.id) : (ev.assistPlayerId ? String(ev.assistPlayerId) : null);

      if (typeLower.includes('goal')) {
        type = 'Goal';
        detail = ev.ownGoal ? 'Own Goal' : (ev.isPenalty || ev.penalty || /pen/i.test(String(ev.goalDescription ?? '')) ? 'Penalty' : 'Normal Goal');
        if (typeof assistName === 'string') {
          assistName = assistName.replace(/^assist by\s+/i, '').trim() || null;
        }
      } else if (typeLower.includes('card')) {
        const cardRaw = String(ev.card || ev.cardColor || rawType).toLowerCase().replace(/[\s_-]/g, '');
        if (cardRaw.includes('yellowred') || cardRaw.includes('secondyellow')) {
          type = 'RedCard';
          detail = 'Second Yellow Card';
        } else if (cardRaw.includes('red')) {
          type = 'RedCard';
          detail = 'Red Card';
        } else {
          type = 'YellowCard';
          detail = 'Yellow Card';
        }
        playerName = ev.player?.name || ev.nameStr || playerName;
      } else if (typeLower.includes('sub')) {
        type = 'Substitution';
        detail = 'Substitution';
        const swap = Array.isArray(ev.swap) ? ev.swap : [];
        if (swap.length >= 2) {
          // FotMob event.swap: swap[0] is incoming player (In), swap[1] is outgoing player (Out).
          // Client MatchCenterViewModel formats PlayerName as "Out:" and AssistPlayerName as "In:".
          playerName = swap[1]?.name || ev.player?.name || playerName;
          playerId = swap[1]?.id != null ? String(swap[1].id) : (ev.player?.id ? String(ev.player.id) : playerId);
          assistName = swap[0]?.name ?? assistName;
          assistPlayerId = swap[0]?.id != null ? String(swap[0].id) : assistPlayerId;
        } else {
          playerName = swap[1]?.name || ev.player?.name || playerName;
          playerId = swap[1]?.id != null ? String(swap[1].id) : (ev.player?.id ? String(ev.player.id) : playerId);
          assistName = swap[0]?.name ?? assistName;
          assistPlayerId = swap[0]?.id != null ? String(swap[0].id) : assistPlayerId;
        }
      } else if (typeLower.includes('var')) {
        type = 'VarDecision';
        detail = ev.VAR?.decision?.value ?? ev.decision ?? 'VAR';
      }

      const minute = typeof ev.time === 'number' ? ev.time : (parseInt(String(ev.time || ev.minute || '0'), 10) || 0);
      const extraMinute = typeof ev.overloadTime === 'number' ? ev.overloadTime : (parseInt(String(ev.overloadTime || ev.extraMinute || '0'), 10) || 0);
      const description = detail ?? ev.description ?? rawType ?? null;
      const eventUid = String(ev.eventId ?? ev.id ?? `${data.general.matchId}_${minute}_${extraMinute}_${type}_${playerId || playerName || idx}`);

      results.push({
        id: `ev_${data.general.matchId}_${eventUid}`,
        eventUid,
        fixtureId: data.general.matchId,
        teamId,
        type,
        detail,
        minute,
        extraMinute,
        playerName,
        playerId,
        assistName,
        assistPlayerName: assistName,
        assistPlayerId,
        description,
        timestamp: ev.timestamp ? new Date(ev.timestamp).toISOString() : new Date().toISOString(),
      });
    });

    return results;
  }

  private extractStats(data: FotmobMatchDetailsResponse, homeTeamId: string, awayTeamId: string): any {
    const periodStats = data.content?.stats?.Periods?.All?.stats;

    const parseNum = (val: any): number | null => {
      if (val == null) return null;
      const cleaned = String(val).replace(/%/g, '').trim();
      const n = parseFloat(cleaned);
      return isNaN(n) ? null : n;
    };

    const parseIntNum = (val: any): number | null => {
      const n = parseNum(val);
      return n != null ? Math.round(n) : null;
    };

    const homeStats: Record<string, any> = {
      teamId: homeTeamId,
      shotsOnGoal: null,
      shotsOffGoal: null,
      totalShots: null,
      possessionPercent: null,
      fouls: null,
      cornerKicks: null,
      offsides: null,
      yellowCards: null,
      redCards: null,
      goalkeeperSaves: null,
      totalPasses: null,
      passesAccurate: null,
      expectedGoals: null,
    };

    const awayStats: Record<string, any> = {
      teamId: awayTeamId,
      shotsOnGoal: null,
      shotsOffGoal: null,
      totalShots: null,
      possessionPercent: null,
      fouls: null,
      cornerKicks: null,
      offsides: null,
      yellowCards: null,
      redCards: null,
      goalkeeperSaves: null,
      totalPasses: null,
      passesAccurate: null,
      expectedGoals: null,
    };

    let hasAnyStat = false;

    if (Array.isArray(periodStats)) {
      for (const group of periodStats) {
        for (const item of (group?.stats || [])) {
          if (!item?.key || !Array.isArray(item.stats) || item.stats.length < 2) continue;
          const key = item.key.toLowerCase().trim();
          const hRaw = item.stats[0];
          const aRaw = item.stats[1];

          if (/expected_goals|xg/i.test(key)) {
            homeStats.expectedGoals = parseNum(hRaw);
            awayStats.expectedGoals = parseNum(aRaw);
            hasAnyStat = true;
          } else if (/possession|ballpossession/i.test(key)) {
            homeStats.possessionPercent = parseIntNum(hRaw);
            awayStats.possessionPercent = parseIntNum(aRaw);
            hasAnyStat = true;
          } else if (/total.*shots|^shots$/i.test(key)) {
            homeStats.totalShots = parseIntNum(hRaw);
            awayStats.totalShots = parseIntNum(aRaw);
            hasAnyStat = true;
          } else if (/shots.*on.*target|shots.*on.*goal/i.test(key)) {
            homeStats.shotsOnGoal = parseIntNum(hRaw);
            awayStats.shotsOnGoal = parseIntNum(aRaw);
            hasAnyStat = true;
          } else if (/shots.*off.*target|shots.*off.*goal/i.test(key)) {
            homeStats.shotsOffGoal = parseIntNum(hRaw);
            awayStats.shotsOffGoal = parseIntNum(aRaw);
            hasAnyStat = true;
          } else if (/foul/i.test(key)) {
            homeStats.fouls = parseIntNum(hRaw);
            awayStats.fouls = parseIntNum(aRaw);
            hasAnyStat = true;
          } else if (/corner/i.test(key)) {
            homeStats.cornerKicks = parseIntNum(hRaw);
            awayStats.cornerKicks = parseIntNum(aRaw);
            hasAnyStat = true;
          } else if (/offside/i.test(key)) {
            homeStats.offsides = parseIntNum(hRaw);
            awayStats.offsides = parseIntNum(aRaw);
            hasAnyStat = true;
          } else if (/yellow.*card/i.test(key)) {
            homeStats.yellowCards = parseIntNum(hRaw);
            awayStats.yellowCards = parseIntNum(aRaw);
            hasAnyStat = true;
          } else if (/red.*card/i.test(key)) {
            homeStats.redCards = parseIntNum(hRaw);
            awayStats.redCards = parseIntNum(aRaw);
            hasAnyStat = true;
          } else if (/keeper.*save|goalkeeper.*save|^saves$/i.test(key)) {
            homeStats.goalkeeperSaves = parseIntNum(hRaw);
            awayStats.goalkeeperSaves = parseIntNum(aRaw);
            hasAnyStat = true;
          } else if (/total.*pass|^passes$/i.test(key)) {
            homeStats.totalPasses = parseIntNum(hRaw);
            awayStats.totalPasses = parseIntNum(aRaw);
            hasAnyStat = true;
          } else if (/accurate.*pass/i.test(key)) {
            homeStats.passesAccurate = parseIntNum(hRaw);
            awayStats.passesAccurate = parseIntNum(aRaw);
            hasAnyStat = true;
          }
        }
      }
    }

    const statsList: any = hasAnyStat ? [homeStats, awayStats] : [];
    if (data.content?.stats?.Periods) {
      statsList.Periods = data.content.stats.Periods;
    }
    return statsList;
  }

  /**
   * Parses a formation string ("4-2-3-1") into outfield row sizes ([4, 2, 3, 1]).
   * Falls back to [4, 4, 2] when missing/invalid or when it does not describe 10 outfielders.
   */
  public static parseFormation(formation: unknown): number[] {
    const fallback = [4, 4, 2];
    if (formation == null) return fallback;
    const parts = String(formation)
      .split(/[-\s]+/)
      .map((p) => parseInt(p, 10))
      .filter((n) => Number.isFinite(n) && n > 0);
    if (parts.length === 0) return fallback;
    const total = parts.reduce((a, b) => a + b, 0);
    return total === 10 ? parts : fallback;
  }

  /**
   * Assigns pitch grid coordinates ("row:col") and positional letters to starters.
   * Row 1 = goalkeeper ("1:1", "G"); rows 2..N follow the formation (D, M..., F).
   */
  public static assignFormationGrid(starters: any[], formation: unknown): any[] {
    const rows = FotmobProvider.parseFormation(formation);
    const lastRowIndex = rows.length + 1; // rows are 2..(rows.length + 1)

    const positionForRow = (row: number): string => {
      if (row === 1) return 'G';
      if (row === 2) return 'D';
      if (row === lastRowIndex) return 'F';
      return 'M';
    };

    return starters.map((p, idx) => {
      if (idx === 0) {
        return { ...p, grid: '1:1', position: 'G' };
      }
      // Find the row this outfield index falls into
      let remaining = idx - 1;
      let row = 2;
      let col = 1;
      for (let r = 0; r < rows.length; r++) {
        if (remaining < rows[r]) {
          row = r + 2;
          col = remaining + 1;
          break;
        }
        remaining -= rows[r];
        // Overflow (more than 11 starters): stack into the final row
        if (r === rows.length - 1) {
          row = lastRowIndex;
          col = rows[r] + remaining + 1;
        }
      }
      return { ...p, grid: `${row}:${col}`, position: positionForRow(row) };
    });
  }

  private extractLineups(data: FotmobMatchDetailsResponse, homeTeamId: string, awayTeamId: string): any[] {
    const rawLineup = data.content?.lineup;
    if (!rawLineup) return [];

    const normalizePlayer = (p: any, isStarter: boolean) => {
      const rawPos = p.position ?? p.role ?? p.primaryPosition ?? null;
      const shirt = p.number ?? p.shirtNumber ?? p.jerseyNumber;
      return {
        id: String(p.id ?? p.playerId ?? p.name ?? Math.random().toString(36).slice(2)),
        name: String(p.name ?? p.usualName ?? p.fullName ?? 'Unknown'),
        number: shirt != null && shirt !== '' ? (parseInt(String(shirt), 10) || null) : null,
        position: typeof rawPos === 'string' ? rawPos : (rawPos?.key ?? rawPos?.label ?? null),
        grid: null as string | null,
        isStarter,
      };
    };

    let teamLineups: any[] = [];
    if (Array.isArray(rawLineup)) {
      teamLineups = rawLineup;
    } else if (Array.isArray(rawLineup.lineup) && rawLineup.lineup.length > 0 && typeof rawLineup.lineup[0] === 'object' && ('starters' in rawLineup.lineup[0] || 'startXi' in rawLineup.lineup[0] || 'teamId' in rawLineup.lineup[0])) {
      teamLineups = rawLineup.lineup;
    } else if (rawLineup.homeTeam || rawLineup.awayTeam) {
      teamLineups = [
        { teamId: homeTeamId, ...(rawLineup.homeTeam || {}) },
        { teamId: awayTeamId, ...(rawLineup.awayTeam || {}) },
      ];
    } else if (Array.isArray(rawLineup.teams)) {
      teamLineups = rawLineup.teams;
    }

    if (teamLineups.length === 0) return [];

    return teamLineups.slice(0, 2).map((teamRaw, idx) => {
      const teamId = teamRaw.teamId ? String(teamRaw.teamId) : (idx === 0 ? homeTeamId : awayTeamId);
      const formation = teamRaw.formation ?? null;
      const coachName = teamRaw.coach?.name ?? teamRaw.coachName ?? null;
      const coachId = teamRaw.coach?.id ? String(teamRaw.coach.id) : (teamRaw.coachId ? String(teamRaw.coachId) : null);

      const startersRaw = teamRaw.starters ?? teamRaw.startXi ?? teamRaw.starting ?? [];
      const subsRaw = teamRaw.substitutes ?? teamRaw.subs ?? teamRaw.bench ?? [];

      const starters = Array.isArray(startersRaw) ? startersRaw.map((p: any) => normalizePlayer(p, true)) : [];

      return {
        teamId,
        formation,
        coachName,
        coachId,
        startXi: FotmobProvider.assignFormationGrid(starters, formation),
        substitutes: Array.isArray(subsRaw) ? subsRaw.map((p: any) => normalizePlayer(p, false)) : [],
      };
    });
  }

  public parseLiveTime(status: any): {
    matchStatus: MatchStatus;
    minute: number | null;
    addedTime: number | null;
    displayClock: string;
  } {
    if (!status) {
      return {
        matchStatus: MatchStatus.SCHEDULED,
        minute: null,
        addedTime: null,
        displayClock: '',
      };
    }

    if (status.cancelled) {
      return {
        matchStatus: MatchStatus.CANCELLED,
        minute: null,
        addedTime: null,
        displayClock: 'CAN',
      };
    }

    const reasonShort = String(status.reason?.short || '').toUpperCase().trim();
    // FotMob liveTime.short looks like "67'", "45+2'", "HT" — sometimes with a typographic
    // apostrophe (’) or invisible bidi marks. Strip those before parsing.
    const liveShort = String(status.liveTime?.short || '')
      .replace(/[\u2019\u2032\u00B4`'\u200E\u200F\u202A-\u202E]/g, '')
      .replace(/\s+/g, '')
      .trim();
    const liveUpper = liveShort.toUpperCase();

    if (status.finished) {
      let matchStatus = MatchStatus.FINISHED;
      let displayClock = 'FT';
      if (reasonShort === 'AET' || reasonShort.includes('AET')) {
        matchStatus = MatchStatus.FINISHED_AET;
        displayClock = 'AET';
      } else if (reasonShort === 'PEN' || reasonShort.includes('PEN')) {
        matchStatus = MatchStatus.FINISHED_PEN;
        displayClock = 'PEN';
      }
      return {
        matchStatus,
        minute: 90,
        addedTime: null,
        displayClock,
      };
    }

    if (status.started || status.ongoing) {
      // 1. Halftime
      if (reasonShort === 'HT' || liveUpper === 'HT') {
        return {
          matchStatus: MatchStatus.HALFTIME,
          minute: 45,
          addedTime: null,
          displayClock: 'HT',
        };
      }

      // 2. Extra Time Halftime
      if (reasonShort === 'ETHT' || liveUpper === 'ETHT') {
        return {
          matchStatus: MatchStatus.EXTRA_TIME_HALFTIME,
          minute: 105,
          addedTime: null,
          displayClock: 'ETHT',
        };
      }

      // 3. Penalties shootout
      if (reasonShort === 'PEN' || liveUpper === 'PEN') {
        return {
          matchStatus: MatchStatus.PENALTIES,
          minute: 120,
          addedTime: null,
          displayClock: 'PEN',
        };
      }

      // 4. Suspended / Interrupted
      if (reasonShort === 'SUSP' || liveUpper === 'SUSP') {
        return {
          matchStatus: MatchStatus.SUSPENDED,
          minute: null,
          addedTime: null,
          displayClock: 'SUSP',
        };
      }
      if (reasonShort === 'INT' || liveUpper === 'INT') {
        return {
          matchStatus: MatchStatus.INTERRUPTED,
          minute: null,
          addedTime: null,
          displayClock: 'INT',
        };
      }

      // 5. Numeric clock: "67", "45+2", "90+4", "105+1"
      const clockMatch = liveShort.match(/^(\d+)(?:\+(\d+))?/);
      if (clockMatch) {
        const baseMin = parseInt(clockMatch[1], 10);
        const extraMin = clockMatch[2] ? parseInt(clockMatch[2], 10) : null;
        const display = extraMin != null ? `${baseMin}+${extraMin}'` : `${baseMin}'`;

        let matchStatus: MatchStatus;
        if (reasonShort === 'ET' || baseMin > 90) {
          matchStatus = MatchStatus.EXTRA_TIME;
        } else if (reasonShort === '2H' || baseMin > 45) {
          matchStatus = MatchStatus.SECOND_HALF;
        } else {
          matchStatus = MatchStatus.FIRST_HALF;
        }

        return {
          matchStatus,
          minute: baseMin,
          addedTime: extraMin,
          displayClock: display,
        };
      }

      // 6. Extra time without a numeric clock
      if (reasonShort === 'ET') {
        return {
          matchStatus: MatchStatus.EXTRA_TIME,
          minute: 91,
          addedTime: null,
          displayClock: 'ET',
        };
      }

      // 7. Fallback in-play state
      const isSecondHalf = reasonShort === '2H';
      return {
        matchStatus: isSecondHalf ? MatchStatus.SECOND_HALF : MatchStatus.FIRST_HALF,
        minute: isSecondHalf ? 46 : 1,
        addedTime: null,
        displayClock: liveShort || (isSecondHalf ? '2H' : '1H'),
      };
    }

    return {
      matchStatus: MatchStatus.SCHEDULED,
      minute: null,
      addedTime: null,
      displayClock: '',
    };
  }

  private mapStatus(rawStatus: any): MatchStatus {
    return this.parseLiveTime(rawStatus).matchStatus;
  }
}
