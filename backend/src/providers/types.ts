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

export interface NormalizedUnavailablePlayer {
  id: string;
  name: string;
  position?: string | null;
  shirtNumber?: number | null;
  reason?: string | null;
  expectedReturn?: string | null;
}

export interface NormalizedLineupPlayer {
  id: string;
  name: string;
  number?: number | null;
  position?: string | null;
  grid?: string | null;
  isStarter: boolean;
  rating?: number | null;
  isTopRating?: boolean;
  isCaptain?: boolean;
}

export interface NormalizedTeamLineup {
  teamId: string;
  formation?: string | null;
  coachName?: string | null;
  coachId?: string | null;
  startXi: NormalizedLineupPlayer[];
  substitutes: NormalizedLineupPlayer[];
  unavailable?: NormalizedUnavailablePlayer[];
}

export interface NormalizedH2HMeeting {
  matchId: string;
  dateUtc: string;
  competitionName?: string | null;
  homeTeamName: string;
  awayTeamName: string;
  homeScore: number;
  awayScore: number;
}

export interface NormalizedFormMatch {
  result: 'W' | 'D' | 'L';
  opponentName: string;
  score: string;
  isHome?: boolean;
  dateUtc?: string;
}

export interface NormalizedH2HAndPredictions {
  homeWins: number;
  draws: number;
  awayWins: number;
  lastFiveMeetings: NormalizedH2HMeeting[];
  homeForm: NormalizedFormMatch[];
  awayForm: NormalizedFormMatch[];
  homeWinProbabilityPct: number;
  drawProbabilityPct: number;
  awayWinProbabilityPct: number;
}

export interface NormalizedHeatmapPoint {
  x: number;
  y: number;
  intensity: number;
  teamId?: string | null;
}

export interface NormalizedTeamHeatmap {
  teamId: string;
  attackingThirdPct: number;
  middleThirdPct: number;
  defensiveThirdPct: number;
  points: NormalizedHeatmapPoint[];
}

export interface NormalizedStandingRow {
  rank: number;
  teamId: string;
  teamName: string;
  teamShortName?: string;
  teamLogoUrl?: string | null;
  played: number;
  wins: number;
  draws: number;
  losses: number;
  goalsFor: number;
  goalsAgainst: number;
  goalDiff: number;
  points: number;
  form?: string | null;
  qualificationColor?: string | null;
  isHomeTeam?: boolean;
  isAwayTeam?: boolean;
}

export interface NormalizedPlayerStatLeader {
  rank: number;
  playerId: string;
  playerName: string;
  teamId: string;
  teamName: string;
  teamLogoUrl?: string | null;
  goals?: number;
  assists?: number;
  penalties?: number;
  matchesPlayed?: number;
}

export interface NormalizedCompetitionHub {
  competitionId: string;
  competitionName: string;
  competitionCode?: string | null;
  competitionLogoUrl?: string | null;
  countryCode?: string | null;
  countryName?: string | null;
  season?: string | null;
  standings: NormalizedStandingRow[];
  topScorers: NormalizedPlayerStatLeader[];
  topAssists: NormalizedPlayerStatLeader[];
}

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
  extraMinute?: number | null;
  commentary?: any[];
  cachedAt?: number;
  h2hAndPredictions?: NormalizedH2HAndPredictions | null;
  heatmaps?: NormalizedTeamHeatmap[] | null;
  standings?: NormalizedStandingRow[] | null;
  topScorers?: NormalizedPlayerStatLeader[] | null;
  topAssists?: NormalizedPlayerStatLeader[] | null;
}

export interface IDataProvider {
  name: string;
  getLiveMatches(): Promise<NormalizedLiveMatch[]>;
  getDailyMatches(date: string): Promise<NormalizedMatch[]>;
  getMatchDetails(providerMatchId: string): Promise<NormalizedMatchDetails>;
  getCompetitionHub?(
    competitionId: string | number,
    competitionName?: string | null,
    logoUrl?: string | null
  ): Promise<NormalizedCompetitionHub | null>;
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
