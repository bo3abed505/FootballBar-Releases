export enum MatchStatus {
  // Pre-Match
  SCHEDULED = 'SCHEDULED',
  DELAYED = 'DELAYED',
  POSTPONED = 'POSTPONED',
  CANCELLED = 'CANCELLED',

  // Live Play
  FIRST_HALF = 'FIRST_HALF',
  HALFTIME = 'HALFTIME',
  SECOND_HALF = 'SECOND_HALF',
  EXTRA_TIME = 'EXTRA_TIME',
  EXTRA_TIME_HALFTIME = 'EXTRA_TIME_HALFTIME',
  PENALTIES = 'PENALTIES',
  SUSPENDED = 'SUSPENDED',
  INTERRUPTED = 'INTERRUPTED',

  // Concluded
  FINISHED = 'FINISHED',
  FINISHED_AET = 'FINISHED_AET',
  FINISHED_PEN = 'FINISHED_PEN',
  ABANDONED = 'ABANDONED',

  // Fallback
  UNKNOWN = 'UNKNOWN',
}

export interface NormalizedMatchTime {
  status: MatchStatus;
  minute: number | null;
  addedTime: number | null;
  displayClock: string;
  scheduledKickoff: string;
}

export interface NormalizedTeam {
  providerId: string;
  name: string;
  /** 3-letter uppercase code (TLA), e.g. "MCI", "PSG". */
  shortName?: string | null;
  score: number | null;
  logoUrl?: string | null;
}

export interface NormalizedLiveMatch {
  providerMatchId: string;
  homeTeam: NormalizedTeam;
  awayTeam: NormalizedTeam;
  time: NormalizedMatchTime;
  competitionId?: string;
  competitionName?: string;
  competitionLogoUrl?: string | null;
  /** Provider country code (FotMob `ccode`, e.g. "ENG", "INT"). */
  competitionCountry?: string | null;
  competitionCode?: string | null;
}

export type NormalizedMatch = NormalizedLiveMatch;

export interface NormalizedMatchDetails {
  providerMatchId: string;
  time: NormalizedMatchTime;
  homeTeam: NormalizedTeam;
  awayTeam: NormalizedTeam;
  stats?: any;
  lineups?: any;
  events?: any[];
  venue?: string;
  referee?: string;
  /** Flattened parent-fixture status used to refresh schema.fixtures during detail sync. */
  state?: MatchStatus;
  homeScore?: number | null;
  awayScore?: number | null;
  elapsedMinute?: number | null;
}

export interface IDataProvider {
  name: string;
  getLiveMatches(): Promise<NormalizedLiveMatch[]>;
  getDailyMatches(date: string): Promise<NormalizedMatch[]>;
  getMatchDetails(providerMatchId: string): Promise<NormalizedMatchDetails>;
}

export interface FixtureFilter {
  competitionId?: string;
  competitionIds?: number[];
  date?: string;
  fromDate?: string;
  toDate?: string;
  liveOnly?: boolean;
  cycleId?: string;
}
