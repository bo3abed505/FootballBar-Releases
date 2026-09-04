import { pgTable, text, integer, boolean, timestamp, date, jsonb, uniqueIndex, index } from 'drizzle-orm/pg-core';
import { relations } from 'drizzle-orm';

// ----------------------------------------------------
// 1. Competitions
// ----------------------------------------------------
export const competitions = pgTable('competitions', {
  id: text('id').primaryKey(),
  name: text('name').notNull(),
  code: text('code'),
  type: text('type').default('league'),
  country: text('country'),
  logoUrl: text('logo_url'),
  createdAt: timestamp('created_at', { withTimezone: true }).defaultNow().notNull(),
  updatedAt: timestamp('updated_at', { withTimezone: true }).defaultNow().notNull(),
});

// ----------------------------------------------------
// 2. Competition Seasons
// ----------------------------------------------------
export const competitionSeasons = pgTable('competition_seasons', {
  id: text('id').primaryKey(),
  competitionId: text('competition_id').notNull().references(() => competitions.id, { onDelete: 'cascade' }),
  year: integer('year').notNull(),
  displayYear: text('display_year').notNull(),
  startDate: timestamp('start_date', { withTimezone: true }).notNull(),
  endDate: timestamp('end_date', { withTimezone: true }).notNull(),
  isCurrent: boolean('is_current').default(true).notNull(),
  createdAt: timestamp('created_at', { withTimezone: true }).defaultNow().notNull(),
}, (table) => [
  uniqueIndex('comp_season_unique_idx').on(table.competitionId, table.year),
]);

// ----------------------------------------------------
// 3. Teams
// ----------------------------------------------------
export const teams = pgTable('teams', {
  id: text('id').primaryKey(),
  name: text('name').notNull(),
  shortName: text('short_name').notNull(),
  code: text('code'),
  country: text('country'),
  logoUrl: text('logo_url'),
  founded: integer('founded'),
  venueName: text('venue_name'),
  createdAt: timestamp('created_at', { withTimezone: true }).defaultNow().notNull(),
  updatedAt: timestamp('updated_at', { withTimezone: true }).defaultNow().notNull(),
});

// ----------------------------------------------------
// 4. Fixtures
// ----------------------------------------------------
export const fixtures = pgTable('fixtures', {
  id: text('id').primaryKey(),
  competitionId: text('competition_id').notNull().references(() => competitions.id),
  seasonId: text('season_id').notNull().references(() => competitionSeasons.id),
  homeTeamId: text('home_team_id').notNull().references(() => teams.id),
  awayTeamId: text('away_team_id').notNull().references(() => teams.id),
  round: text('round'),
  kickoffTime: timestamp('kickoff_time', { withTimezone: true }).notNull(),
  state: text('state').notNull().default('PREMATCH'), // PREMATCH, LIVE, HALFTIME, FINISHED, POSTMATCH, POSTPONED, CANCELLED
  elapsedMinute: integer('elapsed_minute').default(0).notNull(),
  extraMinute: integer('extra_minute').default(0).notNull(),
  homeScore: integer('home_score').default(0).notNull(),
  awayScore: integer('away_score').default(0).notNull(),
  homeScoreHt: integer('home_score_ht'),
  awayScoreHt: integer('away_score_ht'),
  homeScoreEt: integer('home_score_et'),
  awayScoreEt: integer('away_score_et'),
  homeScorePen: integer('home_score_pen'),
  awayScorePen: integer('away_score_pen'),
  venue: text('venue'),
  referee: text('referee'),
  lastPolledAt: timestamp('last_polled_at', { withTimezone: true }),
  createdAt: timestamp('created_at', { withTimezone: true }).defaultNow().notNull(),
  updatedAt: timestamp('updated_at', { withTimezone: true }).defaultNow().notNull(),
}, (table) => [
  index('fixtures_state_idx').on(table.state),
  index('fixtures_kickoff_idx').on(table.kickoffTime),
  index('fixtures_home_team_idx').on(table.homeTeamId),
  index('fixtures_away_team_idx').on(table.awayTeamId),
]);

// ----------------------------------------------------
// 5. Events
// ----------------------------------------------------
export const events = pgTable('events', {
  id: text('id').primaryKey(),
  eventUid: text('event_uid').unique().notNull(),
  fixtureId: text('fixture_id').notNull().references(() => fixtures.id, { onDelete: 'cascade' }),
  teamId: text('team_id').notNull().references(() => teams.id),
  type: text('type').notNull(), // Goal, YellowCard, RedCard, Substitution, VarDecision, PeriodStart, PeriodEnd
  minute: integer('minute').notNull(),
  extraMinute: integer('extra_minute').default(0).notNull(),
  playerName: text('player_name'),
  playerId: text('player_id'),
  assistPlayerName: text('assist_player_name'),
  assistPlayerId: text('assist_player_id'),
  description: text('description'),
  timestamp: timestamp('timestamp', { withTimezone: true }).defaultNow().notNull(),
}, (table) => [
  index('events_fixture_idx').on(table.fixtureId),
  uniqueIndex('events_uid_idx').on(table.eventUid),
]);

// ----------------------------------------------------
// 6. Lineups
// ----------------------------------------------------
export const lineups = pgTable('lineups', {
  id: text('id').primaryKey(),
  fixtureId: text('fixture_id').notNull().references(() => fixtures.id, { onDelete: 'cascade' }),
  teamId: text('team_id').notNull().references(() => teams.id),
  formation: text('formation'),
  coachName: text('coach_name'),
  coachId: text('coach_id'),
  createdAt: timestamp('created_at', { withTimezone: true }).defaultNow().notNull(),
}, (table) => [
  uniqueIndex('lineup_fixture_team_idx').on(table.fixtureId, table.teamId),
]);

// ----------------------------------------------------
// 7. Lineup Players
// ----------------------------------------------------
export const lineupPlayers = pgTable('lineup_players', {
  id: text('id').primaryKey(),
  lineupId: text('lineup_id').notNull().references(() => lineups.id, { onDelete: 'cascade' }),
  playerId: text('player_id').notNull(),
  name: text('name').notNull(),
  number: integer('number'),
  position: text('position'),
  grid: text('grid'),
  isStarter: boolean('is_starter').default(true).notNull(),
}, (table) => [
  index('lineup_players_lineup_idx').on(table.lineupId),
]);

// ----------------------------------------------------
// 8. Fixture Statistics
// ----------------------------------------------------
export const fixtureStatistics = pgTable('fixture_statistics', {
  id: text('id').primaryKey(),
  fixtureId: text('fixture_id').notNull().references(() => fixtures.id, { onDelete: 'cascade' }),
  teamId: text('team_id').notNull().references(() => teams.id),
  shotsOnGoal: integer('shots_on_goal'),
  shotsOffGoal: integer('shots_off_goal'),
  totalShots: integer('total_shots'),
  possessionPercent: integer('possession_percent'),
  fouls: integer('fouls'),
  cornerKicks: integer('corner_kicks'),
  offsides: integer('offsides'),
  yellowCards: integer('yellow_cards'),
  redCards: integer('red_cards'),
  goalkeeperSaves: integer('goalkeeper_saves'),
  totalPasses: integer('total_passes'),
  passesAccurate: integer('passes_accurate'),
  updatedAt: timestamp('updated_at', { withTimezone: true }).defaultNow().notNull(),
}, (table) => [
  uniqueIndex('stats_fixture_team_idx').on(table.fixtureId, table.teamId),
]);

// ----------------------------------------------------
// 9. Standings
// ----------------------------------------------------
export const standings = pgTable('standings', {
  id: text('id').primaryKey(),
  competitionId: text('competition_id').notNull().references(() => competitions.id),
  seasonId: text('season_id').notNull().references(() => competitionSeasons.id),
  teamId: text('team_id').notNull().references(() => teams.id),
  position: integer('position').notNull(),
  played: integer('played').default(0).notNull(),
  won: integer('won').default(0).notNull(),
  drawn: integer('drawn').default(0).notNull(),
  lost: integer('lost').default(0).notNull(),
  goalsFor: integer('goals_for').default(0).notNull(),
  goalsAgainst: integer('goals_against').default(0).notNull(),
  goalDifference: integer('goal_difference').default(0).notNull(),
  points: integer('points').default(0).notNull(),
  form: text('form'),
  description: text('description'),
  updatedAt: timestamp('updated_at', { withTimezone: true }).defaultNow().notNull(),
}, (table) => [
  uniqueIndex('standings_comp_season_team_idx').on(table.competitionId, table.seasonId, table.teamId),
  index('standings_comp_season_pos_idx').on(table.competitionId, table.seasonId, table.position),
]);

// ----------------------------------------------------
// 10. Provider Coverage
// ----------------------------------------------------
export const providerCoverage = pgTable('provider_coverage', {
  id: text('id').primaryKey(),
  provider: text('provider').default('api_football').notNull(),
  competitionId: text('competition_id').notNull().references(() => competitions.id),
  seasonYear: integer('season_year').notNull(),
  hasFixtures: boolean('has_fixtures').default(true).notNull(),
  hasLiveOdds: boolean('has_live_odds').default(false).notNull(),
  hasLineups: boolean('has_lineups').default(true).notNull(),
  hasEvents: boolean('has_events').default(true).notNull(),
  hasStatistics: boolean('has_statistics').default(true).notNull(),
  hasStandings: boolean('has_standings').default(true).notNull(),
  hasPlayers: boolean('has_players').default(true).notNull(),
  hasCommentary: boolean('has_commentary').default(false).notNull(),
  hasRatings: boolean('has_ratings').default(false).notNull(),
  hasXg: boolean('has_xg').default(false).notNull(),
  lastAuditedAt: timestamp('last_audited_at', { withTimezone: true }),
}, (table) => [
  uniqueIndex('provider_comp_season_idx').on(table.provider, table.competitionId, table.seasonYear),
]);

// ----------------------------------------------------
// 11. Acquisition Jobs
// ----------------------------------------------------
export const acquisitionJobs = pgTable('acquisition_jobs', {
  id: text('id').primaryKey(),
  jobType: text('job_type').notNull(), // fixture_poll, standings_sync, daily_schedule
  status: text('status').default('pending').notNull(), // pending, running, completed, failed
  targetId: text('target_id'),
  priority: integer('priority').default(0).notNull(),
  attempts: integer('attempts').default(0).notNull(),
  lastError: text('last_error'),
  scheduledAt: timestamp('scheduled_at', { withTimezone: true }).notNull(),
  executedAt: timestamp('executed_at', { withTimezone: true }),
  createdAt: timestamp('created_at', { withTimezone: true }).defaultNow().notNull(),
}, (table) => [
  index('jobs_status_scheduled_idx').on(table.status, table.scheduledAt),
]);

// ----------------------------------------------------
// 12. API Request Ledger
// ----------------------------------------------------
export const apiRequestLedger = pgTable('api_request_ledger', {
  id: text('id').primaryKey(),
  cycleId: text('cycle_id'),
  competitionId: text('competition_id'),
  provider: text('provider').default('api_football').notNull(),
  endpoint: text('endpoint').notNull(),
  operation: text('operation').default('unknown').notNull(),
  priority: text('priority').default('NORMAL').notNull(), // NORMAL, CRITICAL
  success: boolean('success').default(true).notNull(),
  httpStatus: integer('http_status').notNull(),
  responseTimeMs: integer('response_time_ms').notNull(),
  costUnits: integer('cost_units').default(1).notNull(),
  providerDailyRemaining: integer('provider_daily_remaining'),
  providerMinuteRemaining: integer('provider_minute_remaining'),
  requestedAt: timestamp('requested_at', { withTimezone: true }).defaultNow().notNull(),
  errorMessage: text('error_message'),
}, (table) => [
  index('ledger_requested_at_idx').on(table.requestedAt),
  index('ledger_provider_requested_idx').on(table.provider, table.requestedAt),
  index('ledger_cycle_id_idx').on(table.cycleId),
  index('ledger_competition_id_idx').on(table.competitionId),
]);

// ----------------------------------------------------
// 12b. Scheduler Cycles
// ----------------------------------------------------
export const schedulerCycles = pgTable('scheduler_cycles', {
  id: text('id').primaryKey(),
  action: text('action').notNull(),
  priority: text('priority').default('NORMAL').notNull(),
  reason: text('reason').notNull(),
  targetCompetitionIds: jsonb('target_competition_ids').$type<number[]>(),
  startedAt: timestamp('started_at', { withTimezone: true }).notNull(),
  completedAt: timestamp('completed_at', { withTimezone: true }),
  durationMs: integer('duration_ms'),
  outcome: text('outcome'), // IDLE, SUCCESS, NO_OP, ERROR, QUOTA_BLOCKED, RATE_LIMITED (NULL while uncompleted)
  providerRequestsCount: integer('provider_requests_count').default(0).notNull(),
  providerRequestsCost: integer('provider_requests_cost').default(0).notNull(),
  errorType: text('error_type'),
  errorMessage: text('error_message'),
  fixturesDiscovered: integer('fixtures_discovered').default(0).notNull(),
  fixturesUpdated: integer('fixtures_updated').default(0).notNull(),
  createdAt: timestamp('created_at', { withTimezone: true }).defaultNow().notNull(),
}, (table) => [
  index('scheduler_cycles_started_idx').on(table.startedAt),
  index('scheduler_cycles_action_idx').on(table.action, table.startedAt),
  index('scheduler_cycles_pruning_idx').on(table.outcome, table.startedAt),
]);

// ----------------------------------------------------
// 13. Provider Daily Budget
// ----------------------------------------------------
export const providerDailyBudget = pgTable('provider_daily_budget', {
  id: text('id').primaryKey(),
  provider: text('provider').default('api_football').notNull(),
  budgetDate: date('budget_date').notNull(),
  allocatedCalls: integer('allocated_calls').default(100).notNull(),
  consumedCalls: integer('consumed_calls').default(0).notNull(),
  remainingCalls: integer('remaining_calls').default(100).notNull(),
  isExhausted: boolean('is_exhausted').default(false).notNull(),
  updatedAt: timestamp('updated_at', { withTimezone: true }).defaultNow().notNull(),
}, (table) => [
  uniqueIndex('budget_provider_date_idx').on(table.provider, table.budgetDate),
]);

// ----------------------------------------------------
// 14. Installations
// ----------------------------------------------------
export const installations = pgTable('installations', {
  id: text('id').primaryKey(),
  installationId: text('installation_id').unique().notNull(),
  clientVersion: text('client_version'),
  platform: text('platform').default('Windows 11').notNull(),
  lastActiveAt: timestamp('last_active_at', { withTimezone: true }).defaultNow().notNull(),
  createdAt: timestamp('created_at', { withTimezone: true }).defaultNow().notNull(),
});

// ----------------------------------------------------
// 15. Installation Settings
// ----------------------------------------------------
export const installationSettings = pgTable('installation_settings', {
  id: text('id').primaryKey(),
  installationId: text('installation_id').notNull().references(() => installations.installationId, { onDelete: 'cascade' }),
  favoriteTeamId: text('favorite_team_id').references(() => teams.id),
  favoriteCompetitionId: text('favorite_competition_id').references(() => competitions.id),
  enableSpoilers: boolean('enable_spoilers').default(true).notNull(),
  pollingIntervalSeconds: integer('polling_interval_seconds').default(60).notNull(),
  updatedAt: timestamp('updated_at', { withTimezone: true }).defaultNow().notNull(),
});

// ----------------------------------------------------
// Relations
// ----------------------------------------------------
export const competitionsRelations = relations(competitions, ({ many }) => ({
  seasons: many(competitionSeasons),
  fixtures: many(fixtures),
  standings: many(standings),
}));

export const competitionSeasonsRelations = relations(competitionSeasons, ({ one, many }) => ({
  competition: one(competitions, {
    fields: [competitionSeasons.competitionId],
    references: [competitions.id],
  }),
  fixtures: many(fixtures),
  standings: many(standings),
}));

export const teamsRelations = relations(teams, ({ many }) => ({
  homeFixtures: many(fixtures, { relationName: 'homeTeam' }),
  awayFixtures: many(fixtures, { relationName: 'awayTeam' }),
  events: many(events),
  standings: many(standings),
  lineups: many(lineups),
  statistics: many(fixtureStatistics),
}));

export const fixturesRelations = relations(fixtures, ({ one, many }) => ({
  competition: one(competitions, {
    fields: [fixtures.competitionId],
    references: [competitions.id],
  }),
  season: one(competitionSeasons, {
    fields: [fixtures.seasonId],
    references: [competitionSeasons.id],
  }),
  homeTeam: one(teams, {
    fields: [fixtures.homeTeamId],
    references: [teams.id],
    relationName: 'homeTeam',
  }),
  awayTeam: one(teams, {
    fields: [fixtures.awayTeamId],
    references: [teams.id],
    relationName: 'awayTeam',
  }),
  events: many(events),
  lineups: many(lineups),
  statistics: many(fixtureStatistics),
}));

export const eventsRelations = relations(events, ({ one }) => ({
  fixture: one(fixtures, {
    fields: [events.fixtureId],
    references: [fixtures.id],
  }),
  team: one(teams, {
    fields: [events.teamId],
    references: [teams.id],
  }),
}));

export const lineupsRelations = relations(lineups, ({ one, many }) => ({
  fixture: one(fixtures, {
    fields: [lineups.fixtureId],
    references: [fixtures.id],
  }),
  team: one(teams, {
    fields: [lineups.teamId],
    references: [teams.id],
  }),
  players: many(lineupPlayers),
}));

export const lineupPlayersRelations = relations(lineupPlayers, ({ one }) => ({
  lineup: one(lineups, {
    fields: [lineupPlayers.lineupId],
    references: [lineups.id],
  }),
}));

export const fixtureStatisticsRelations = relations(fixtureStatistics, ({ one }) => ({
  fixture: one(fixtures, {
    fields: [fixtureStatistics.fixtureId],
    references: [fixtures.id],
  }),
  team: one(teams, {
    fields: [fixtureStatistics.teamId],
    references: [teams.id],
  }),
}));

export const standingsRelations = relations(standings, ({ one }) => ({
  competition: one(competitions, {
    fields: [standings.competitionId],
    references: [competitions.id],
  }),
  season: one(competitionSeasons, {
    fields: [standings.seasonId],
    references: [competitionSeasons.id],
  }),
  team: one(teams, {
    fields: [standings.teamId],
    references: [teams.id],
  }),
}));
