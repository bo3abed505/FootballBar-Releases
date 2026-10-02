import { sqliteTable, text, integer, real, uniqueIndex, index, primaryKey } from 'drizzle-orm/sqlite-core';
import { relations } from 'drizzle-orm';

// ----------------------------------------------------
// 1. Competitions
// ----------------------------------------------------
export const competitions = sqliteTable('competitions', {
  id: text('id').primaryKey(),
  canonicalId: text('canonical_id').notNull().unique(),
  name: text('name').notNull(),
  code: text('code'),
  type: text('type').default('league'),
  country: text('country'),
  logoUrl: text('logo_url'),
  createdAt: integer('created_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
  updatedAt: integer('updated_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
});

// ----------------------------------------------------
// 2. Competition Seasons
// ----------------------------------------------------
export const competitionSeasons = sqliteTable('competition_seasons', {
  id: text('id').primaryKey(),
  competitionId: text('competition_id').notNull().references(() => competitions.id, { onDelete: 'cascade' }),
  year: integer('year').notNull(),
  displayYear: text('display_year').notNull(),
  startDate: integer('start_date', { mode: 'timestamp_ms' }).notNull(),
  endDate: integer('end_date', { mode: 'timestamp_ms' }).notNull(),
  isCurrent: integer('is_current', { mode: 'boolean' }).default(true).notNull(),
  createdAt: integer('created_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
}, (table) => [
  uniqueIndex('comp_season_unique_idx').on(table.competitionId, table.year),
]);

// ----------------------------------------------------
// 3. Teams
// ----------------------------------------------------
export const teams = sqliteTable('teams', {
  id: text('id').primaryKey(),
  canonicalId: text('canonical_id').notNull().unique(),
  name: text('name').notNull(),
  shortName: text('short_name').notNull(),
  code: text('code'),
  country: text('country'),
  logoUrl: text('logo_url'),
  founded: integer('founded'),
  venueName: text('venue_name'),
  createdAt: integer('created_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
  updatedAt: integer('updated_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
});

// ----------------------------------------------------
// 4. Fixtures
// ----------------------------------------------------
export const fixtures = sqliteTable('fixtures', {
  id: text('id').primaryKey(),
  canonicalId: text('canonical_id').notNull().unique(),
  competitionId: text('competition_id').notNull().references(() => competitions.id),
  seasonId: text('season_id').notNull().references(() => competitionSeasons.id),
  homeTeamId: text('home_team_id').notNull().references(() => teams.id),
  awayTeamId: text('away_team_id').notNull().references(() => teams.id),
  round: text('round'),
  kickoffTime: integer('kickoff_time', { mode: 'timestamp_ms' }).notNull(),
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
  lastPolledAt: integer('last_polled_at', { mode: 'timestamp_ms' }),
  liveSourceProvider: text('live_source_provider'),
  liveObservedAt: integer('live_observed_at', { mode: 'timestamp_ms' }),
  scheduleSourceProvider: text('schedule_source_provider'),
  scheduleObservedAt: integer('schedule_observed_at', { mode: 'timestamp_ms' }),
  createdAt: integer('created_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
  updatedAt: integer('updated_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
}, (table) => [
  index('fixtures_state_idx').on(table.state),
  index('fixtures_kickoff_idx').on(table.kickoffTime),
  index('fixtures_home_team_idx').on(table.homeTeamId),
  index('fixtures_away_team_idx').on(table.awayTeamId),
]);

// ----------------------------------------------------
// 5. Events
// ----------------------------------------------------
export const events = sqliteTable('events', {
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
  timestamp: integer('timestamp', { mode: 'timestamp_ms' }).defaultNow().notNull(),
}, (table) => [
  index('events_fixture_idx').on(table.fixtureId),
  uniqueIndex('events_uid_idx').on(table.eventUid),
]);

// ----------------------------------------------------
// 6. Lineups
// ----------------------------------------------------
export const lineups = sqliteTable('lineups', {
  id: text('id').primaryKey(),
  fixtureId: text('fixture_id').notNull().references(() => fixtures.id, { onDelete: 'cascade' }),
  teamId: text('team_id').notNull().references(() => teams.id),
  formation: text('formation'),
  coachName: text('coach_name'),
  coachId: text('coach_id'),
  createdAt: integer('created_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
}, (table) => [
  uniqueIndex('lineup_fixture_team_idx').on(table.fixtureId, table.teamId),
]);

// ----------------------------------------------------
// 7. Lineup Players
// ----------------------------------------------------
export const lineupPlayers = sqliteTable('lineup_players', {
  id: text('id').primaryKey(),
  lineupId: text('lineup_id').notNull().references(() => lineups.id, { onDelete: 'cascade' }),
  playerId: text('player_id').notNull(),
  name: text('name').notNull(),
  number: integer('number'),
  position: text('position'),
  grid: text('grid'),
  isStarter: integer('is_starter', { mode: 'boolean' }).default(true).notNull(),
}, (table) => [
  index('lineup_players_lineup_idx').on(table.lineupId),
]);

// ----------------------------------------------------
// 8. Fixture Statistics
// ----------------------------------------------------
export const fixtureStatistics = sqliteTable('fixture_statistics', {
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
  updatedAt: integer('updated_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
}, (table) => [
  uniqueIndex('stats_fixture_team_idx').on(table.fixtureId, table.teamId),
]);

// ----------------------------------------------------
// 9. Standings
// ----------------------------------------------------
export const standings = sqliteTable('standings', {
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
  updatedAt: integer('updated_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
}, (table) => [
  uniqueIndex('standings_comp_season_team_idx').on(table.competitionId, table.seasonId, table.teamId),
  index('standings_comp_season_pos_idx').on(table.competitionId, table.seasonId, table.position),
]);

// ----------------------------------------------------
// 10. Provider Coverage
// ----------------------------------------------------
export const providerCoverage = sqliteTable('provider_coverage', {
  id: text('id').primaryKey(),
  provider: text('provider').default('api_football').notNull(),
  competitionId: text('competition_id').notNull().references(() => competitions.id),
  seasonYear: integer('season_year').notNull(),
  hasFixtures: integer('has_fixtures', { mode: 'boolean' }).default(true).notNull(),
  hasLiveOdds: integer('has_live_odds', { mode: 'boolean' }).default(false).notNull(),
  hasLineups: integer('has_lineups', { mode: 'boolean' }).default(true).notNull(),
  hasEvents: integer('has_events', { mode: 'boolean' }).default(true).notNull(),
  hasStatistics: integer('has_statistics', { mode: 'boolean' }).default(true).notNull(),
  hasStandings: integer('has_standings', { mode: 'boolean' }).default(true).notNull(),
  hasPlayers: integer('has_players', { mode: 'boolean' }).default(true).notNull(),
  hasCommentary: integer('has_commentary', { mode: 'boolean' }).default(false).notNull(),
  hasRatings: integer('has_ratings', { mode: 'boolean' }).default(false).notNull(),
  hasXg: integer('has_xg', { mode: 'boolean' }).default(false).notNull(),
  lastAuditedAt: integer('last_audited_at', { mode: 'timestamp_ms' }),
}, (table) => [
  uniqueIndex('provider_comp_season_idx').on(table.provider, table.competitionId, table.seasonYear),
]);

// ----------------------------------------------------
// 11. Acquisition Jobs
// ----------------------------------------------------
export const acquisitionJobs = sqliteTable('acquisition_jobs', {
  id: text('id').primaryKey(),
  jobType: text('job_type').notNull(), // fixture_poll, standings_sync, daily_schedule
  status: text('status').default('pending').notNull(), // pending, running, completed, failed
  targetId: text('target_id'),
  priority: integer('priority').default(0).notNull(),
  attempts: integer('attempts').default(0).notNull(),
  lastError: text('last_error'),
  scheduledAt: integer('scheduled_at', { mode: 'timestamp_ms' }).notNull(),
  executedAt: integer('executed_at', { mode: 'timestamp_ms' }),
  createdAt: integer('created_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
}, (table) => [
  index('jobs_status_scheduled_idx').on(table.status, table.scheduledAt),
]);

// ----------------------------------------------------
// 12. API Request Ledger
// ----------------------------------------------------
export const apiRequestLedger = sqliteTable('api_request_ledger', {
  id: text('id').primaryKey(),
  cycleId: text('cycle_id'),
  competitionId: text('competition_id'),
  provider: text('provider').default('api_football').notNull(),
  endpoint: text('endpoint').notNull(),
  operation: text('operation').default('unknown').notNull(),
  priority: text('priority').default('NORMAL').notNull(), // NORMAL, CRITICAL
  success: integer('success', { mode: 'boolean' }).default(true).notNull(),
  httpStatus: integer('http_status').notNull(),
  responseTimeMs: integer('response_time_ms').notNull(),
  costUnits: integer('cost_units').default(1).notNull(),
  providerDailyRemaining: integer('provider_daily_remaining'),
  providerMinuteRemaining: integer('provider_minute_remaining'),
  requestedAt: integer('requested_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
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
export const schedulerCycles = sqliteTable('scheduler_cycles', {
  id: text('id').primaryKey(),
  action: text('action').notNull(),
  priority: text('priority').default('NORMAL').notNull(),
  reason: text('reason').notNull(),
  targetCompetitionIds: text('target_competition_ids', { mode: 'json' }).$type<number[]>(),
  startedAt: integer('started_at', { mode: 'timestamp_ms' }).notNull(),
  completedAt: integer('completed_at', { mode: 'timestamp_ms' }),
  durationMs: integer('duration_ms'),
  outcome: text('outcome'), // IDLE, SUCCESS, NO_OP, ERROR, QUOTA_BLOCKED, RATE_LIMITED (NULL while uncompleted)
  providerRequestsCount: integer('provider_requests_count').default(0).notNull(),
  providerRequestsCost: integer('provider_requests_cost').default(0).notNull(),
  errorType: text('error_type'),
  errorMessage: text('error_message'),
  fixturesDiscovered: integer('fixtures_discovered').default(0).notNull(),
  fixturesUpdated: integer('fixtures_updated').default(0).notNull(),
  createdAt: integer('created_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
}, (table) => [
  index('scheduler_cycles_started_idx').on(table.startedAt),
  index('scheduler_cycles_action_idx').on(table.action, table.startedAt),
  index('scheduler_cycles_pruning_idx').on(table.outcome, table.startedAt),
]);

// ----------------------------------------------------
// 13. Provider Daily Budget
// ----------------------------------------------------
export const providerDailyBudget = sqliteTable('provider_daily_budget', {
  id: text('id').primaryKey(),
  provider: text('provider').default('api_football').notNull(),
  budgetDate: text('budget_date').notNull(),
  allocatedCalls: integer('allocated_calls').default(100).notNull(),
  consumedCalls: integer('consumed_calls').default(0).notNull(),
  remainingCalls: integer('remaining_calls').default(100).notNull(),
  isExhausted: integer('is_exhausted', { mode: 'boolean' }).default(false).notNull(),
  updatedAt: integer('updated_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
}, (table) => [
  uniqueIndex('budget_provider_date_idx').on(table.provider, table.budgetDate),
]);

// ----------------------------------------------------
// 14. Installations
// ----------------------------------------------------
export const installations = sqliteTable('installations', {
  id: text('id').primaryKey(),
  installationId: text('installation_id').unique().notNull(),
  clientVersion: text('client_version'),
  platform: text('platform').default('Windows 11').notNull(),
  lastActiveAt: integer('last_active_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
  createdAt: integer('created_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
});

// ----------------------------------------------------
// 15. Installation Settings
// ----------------------------------------------------
export const installationSettings = sqliteTable('installation_settings', {
  id: text('id').primaryKey(),
  installationId: text('installation_id').notNull().references(() => installations.installationId, { onDelete: 'cascade' }),
  favoriteTeamId: text('favorite_team_id').references(() => teams.id),
  favoriteCompetitionId: text('favorite_competition_id').references(() => competitions.id),
  enableSpoilers: integer('enable_spoilers', { mode: 'boolean' }).default(true).notNull(),
  pollingIntervalSeconds: integer('polling_interval_seconds').default(60).notNull(),
  updatedAt: integer('updated_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
});

// ----------------------------------------------------
// 16. Competition Provider Aliases
// ----------------------------------------------------
export const competitionProviderAliases = sqliteTable('competition_provider_aliases', {
  canonicalCompetitionId: text('canonical_competition_id').notNull().references(() => competitions.canonicalId, { onDelete: 'cascade' }),
  provider: text('provider').notNull(),
  providerId: text('provider_id').notNull(),
  firstSeenAt: integer('first_seen_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
  lastSeenAt: integer('last_seen_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
}, (table) => [
  primaryKey({ columns: [table.provider, table.providerId] }),
  index('comp_provider_aliases_canonical_idx').on(table.canonicalCompetitionId),
]);

// ----------------------------------------------------
// 17. Team Provider Aliases
// ----------------------------------------------------
export const teamProviderAliases = sqliteTable('team_provider_aliases', {
  canonicalTeamId: text('canonical_team_id').notNull().references(() => teams.canonicalId, { onDelete: 'cascade' }),
  provider: text('provider').notNull(),
  providerId: text('provider_id').notNull(),
  firstSeenAt: integer('first_seen_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
  lastSeenAt: integer('last_seen_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
}, (table) => [
  primaryKey({ columns: [table.provider, table.providerId] }),
  index('team_provider_aliases_canonical_idx').on(table.canonicalTeamId),
]);

// ----------------------------------------------------
// 18. Fixture Provider Aliases
// ----------------------------------------------------
export const fixtureProviderAliases = sqliteTable('fixture_provider_aliases', {
  canonicalFixtureId: text('canonical_fixture_id').notNull().references(() => fixtures.canonicalId, { onDelete: 'cascade' }),
  provider: text('provider').notNull(),
  providerId: text('provider_id').notNull(),
  firstSeenAt: integer('first_seen_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
  lastSeenAt: integer('last_seen_at', { mode: 'timestamp_ms' }).defaultNow().notNull(),
}, (table) => [
  primaryKey({ columns: [table.provider, table.providerId] }),
  index('fix_provider_aliases_canonical_idx').on(table.canonicalFixtureId),
]);

// ----------------------------------------------------
// Relations
// ----------------------------------------------------
export const competitionsRelations = relations(competitions, ({ many }) => ({
  seasons: many(competitionSeasons),
  fixtures: many(fixtures),
  standings: many(standings),
  providerAliases: many(competitionProviderAliases),
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
  providerAliases: many(teamProviderAliases),
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
  providerAliases: many(fixtureProviderAliases),
}));

export const competitionProviderAliasesRelations = relations(competitionProviderAliases, ({ one }) => ({
  competition: one(competitions, {
    fields: [competitionProviderAliases.canonicalCompetitionId],
    references: [competitions.canonicalId],
  }),
}));

export const teamProviderAliasesRelations = relations(teamProviderAliases, ({ one }) => ({
  team: one(teams, {
    fields: [teamProviderAliases.canonicalTeamId],
    references: [teams.canonicalId],
  }),
}));

export const fixtureProviderAliasesRelations = relations(fixtureProviderAliases, ({ one }) => ({
  fixture: one(fixtures, {
    fields: [fixtureProviderAliases.canonicalFixtureId],
    references: [fixtures.canonicalId],
  }),
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
