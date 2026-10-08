export interface ProviderCompetition {
  id: string;
  name: string;
  code?: string;
  type?: string;
  country?: string;
  logoUrl?: string;
  provider?: string;
  providerId?: string;
}

export interface ProviderSeason {
  id: string;
  competitionId: string;
  year: number;
  displayYear: string;
  startDate: string; // ISO date string
  endDate: string;
  isCurrent: boolean;
}

export interface ProviderTeam {
  id: string;
  name: string;
  shortName: string;
  code?: string;
  country?: string;
  logoUrl?: string;
  venueName?: string;
  provider?: string;
  providerId?: string;
}

export interface ProviderScore {
  home: number;
  away: number;
  homeHt?: number;
  awayHt?: number;
  homeEt?: number;
  awayEt?: number;
  homePen?: number;
  awayPen?: number;
}

export interface ProviderEvent {
  id: string;
  eventUid: string;
  fixtureId: string;
  teamId: string;
  type: string; // PeriodStart, PeriodEnd, Goal, YellowCard, RedCard, Substitution, VarDecision
  minute: number;
  extraMinute?: number;
  playerName?: string;
  playerId?: string;
  assistPlayerName?: string;
  assistPlayerId?: string;
  description?: string;
  timestamp?: string; // ISO date string
}

export interface ProviderFixture {
  id: string;
  competitionId: string;
  seasonId: string;
  homeTeamId: string;
  awayTeamId: string;
  competition: ProviderCompetition;
  season: ProviderSeason;
  homeTeam: ProviderTeam;
  awayTeam: ProviderTeam;
  round?: string;
  kickoffTime: string; // ISO date string
  state: string; // PREMATCH, LIVE, HALFTIME, FINISHED, POSTMATCH, POSTPONED, CANCELLED
  elapsedMinute: number;
  extraMinute?: number;
  score: ProviderScore;
  venue?: string;
  referee?: string;
  events?: ProviderEvent[];
  provider?: string;
  providerId?: string;
}

export interface ProviderLineupPlayer {
  id: string;
  name: string;
  number?: number;
  position?: string;
  grid?: string;
  isStarter: boolean;
  rating?: string | number | null;
}

export interface ProviderLineup {
  teamId: string;
  formation?: string;
  coachName?: string;
  coachId?: string;
  startXi: ProviderLineupPlayer[];
  substitutes: ProviderLineupPlayer[];
}

export interface ProviderTeamStatistics {
  teamId: string;
  shotsOnGoal?: number;
  shotsOffGoal?: number;
  totalShots?: number;
  possessionPercent?: number;
  fouls?: number;
  cornerKicks?: number;
  offsides?: number;
  yellowCards?: number;
  redCards?: number;
  goalkeeperSaves?: number;
  totalPasses?: number;
  passesAccurate?: number;
}

export interface ProviderPlayerRating {
  playerId: string;
  teamId: string;
  name: string;
  rating?: string | number | null;
}

export interface ProviderFixtureDetail extends ProviderFixture {
  events: ProviderEvent[];
  lineups?: ProviderLineup[];
  statistics?: ProviderTeamStatistics[];
  playerRatings?: ProviderPlayerRating[];
}

export interface ProviderStandingRow {
  position: number;
  teamId: string;
  played: number;
  won: number;
  drawn: number;
  lost: number;
  goalsFor: number;
  goalsAgainst: number;
  points: number;
  form?: string;
}

export interface ProviderStanding {
  competitionId: string;
  seasonId: string;
  rows: ProviderStandingRow[];
}

export interface FixtureFilter {
  /** Internal comp_* ID for a single competition (legacy, kept for backward compat). */
  competitionId?: string;
  /**
   * Numeric provider-level competition IDs (e.g. 39, 140).
   * Used by ApiFootballProvider to issue one request per competition ID.
   * MockFootballProvider ignores this field — it always returns its full fixture set.
   */
  competitionIds?: number[];
  date?: string;
  fromDate?: string;
  toDate?: string;
  liveOnly?: boolean;
  cycleId?: string;
}

/**
 * Common contract for all football data providers (Mock, API-Football, etc.)
 */
export interface FootballProvider {
  readonly providerName: string;
  getCompetitions(): Promise<ProviderCompetition[]>;
  getFixtures(filter?: FixtureFilter): Promise<ProviderFixture[]>;
  getFixturesByDate?(date: string, cycleId?: string): Promise<ProviderFixture[]>;
  getFixturesByDateRange?(fromDate: string, toDate: string, cycleId?: string): Promise<ProviderFixture[]>;
  getFixtureDetails(fixtureId: string, cycleId?: string): Promise<ProviderFixtureDetail | null>;
  getFixtureDetailsBatch?(fixtureIds: string[], cycleId?: string): Promise<ProviderFixtureDetail[]>;
  getFixtureEvents(fixtureId: string): Promise<ProviderEvent[]>;
  getStandings(competitionId: string, seasonYear: number): Promise<ProviderStanding[]>;
}
