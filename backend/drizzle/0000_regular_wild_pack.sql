CREATE TABLE "acquisition_jobs" (
	"id" text PRIMARY KEY NOT NULL,
	"job_type" text NOT NULL,
	"status" text DEFAULT 'pending' NOT NULL,
	"target_id" text,
	"priority" integer DEFAULT 0 NOT NULL,
	"attempts" integer DEFAULT 0 NOT NULL,
	"last_error" text,
	"scheduled_at" timestamp with time zone NOT NULL,
	"executed_at" timestamp with time zone,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE "api_request_ledger" (
	"id" text PRIMARY KEY NOT NULL,
	"provider" text DEFAULT 'api_football' NOT NULL,
	"endpoint" text NOT NULL,
	"http_status" integer NOT NULL,
	"response_time_ms" integer NOT NULL,
	"cost_units" integer DEFAULT 1 NOT NULL,
	"requested_at" timestamp with time zone DEFAULT now() NOT NULL,
	"error_message" text
);
--> statement-breakpoint
CREATE TABLE "competition_seasons" (
	"id" text PRIMARY KEY NOT NULL,
	"competition_id" text NOT NULL,
	"year" integer NOT NULL,
	"display_year" text NOT NULL,
	"start_date" timestamp with time zone NOT NULL,
	"end_date" timestamp with time zone NOT NULL,
	"is_current" boolean DEFAULT true NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE "competitions" (
	"id" text PRIMARY KEY NOT NULL,
	"name" text NOT NULL,
	"code" text,
	"type" text DEFAULT 'league',
	"country" text,
	"logo_url" text,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE "events" (
	"id" text PRIMARY KEY NOT NULL,
	"event_uid" text NOT NULL,
	"fixture_id" text NOT NULL,
	"team_id" text NOT NULL,
	"type" text NOT NULL,
	"minute" integer NOT NULL,
	"extra_minute" integer DEFAULT 0 NOT NULL,
	"player_name" text,
	"player_id" text,
	"assist_player_name" text,
	"assist_player_id" text,
	"description" text,
	"timestamp" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "events_event_uid_unique" UNIQUE("event_uid")
);
--> statement-breakpoint
CREATE TABLE "fixture_statistics" (
	"id" text PRIMARY KEY NOT NULL,
	"fixture_id" text NOT NULL,
	"team_id" text NOT NULL,
	"shots_on_goal" integer,
	"shots_off_goal" integer,
	"total_shots" integer,
	"possession_percent" integer,
	"fouls" integer,
	"corner_kicks" integer,
	"offsides" integer,
	"yellow_cards" integer,
	"red_cards" integer,
	"goalkeeper_saves" integer,
	"total_passes" integer,
	"passes_accurate" integer,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE "fixtures" (
	"id" text PRIMARY KEY NOT NULL,
	"competition_id" text NOT NULL,
	"season_id" text NOT NULL,
	"home_team_id" text NOT NULL,
	"away_team_id" text NOT NULL,
	"round" text,
	"kickoff_time" timestamp with time zone NOT NULL,
	"state" text DEFAULT 'PREMATCH' NOT NULL,
	"elapsed_minute" integer DEFAULT 0 NOT NULL,
	"extra_minute" integer DEFAULT 0 NOT NULL,
	"home_score" integer DEFAULT 0 NOT NULL,
	"away_score" integer DEFAULT 0 NOT NULL,
	"home_score_ht" integer,
	"away_score_ht" integer,
	"home_score_et" integer,
	"away_score_et" integer,
	"home_score_pen" integer,
	"away_score_pen" integer,
	"venue" text,
	"referee" text,
	"last_polled_at" timestamp with time zone,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE "installation_settings" (
	"id" text PRIMARY KEY NOT NULL,
	"installation_id" text NOT NULL,
	"favorite_team_id" text,
	"favorite_competition_id" text,
	"enable_spoilers" boolean DEFAULT true NOT NULL,
	"polling_interval_seconds" integer DEFAULT 60 NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE "installations" (
	"id" text PRIMARY KEY NOT NULL,
	"installation_id" text NOT NULL,
	"client_version" text,
	"platform" text DEFAULT 'Windows 11' NOT NULL,
	"last_active_at" timestamp with time zone DEFAULT now() NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "installations_installation_id_unique" UNIQUE("installation_id")
);
--> statement-breakpoint
CREATE TABLE "lineup_players" (
	"id" text PRIMARY KEY NOT NULL,
	"lineup_id" text NOT NULL,
	"player_id" text NOT NULL,
	"name" text NOT NULL,
	"number" integer,
	"position" text,
	"grid" text,
	"is_starter" boolean DEFAULT true NOT NULL
);
--> statement-breakpoint
CREATE TABLE "lineups" (
	"id" text PRIMARY KEY NOT NULL,
	"fixture_id" text NOT NULL,
	"team_id" text NOT NULL,
	"formation" text,
	"coach_name" text,
	"coach_id" text,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE "provider_coverage" (
	"id" text PRIMARY KEY NOT NULL,
	"provider" text DEFAULT 'api_football' NOT NULL,
	"competition_id" text NOT NULL,
	"season_year" integer NOT NULL,
	"has_live_odds" boolean DEFAULT false NOT NULL,
	"has_lineups" boolean DEFAULT true NOT NULL,
	"has_events" boolean DEFAULT true NOT NULL,
	"has_statistics" boolean DEFAULT true NOT NULL,
	"has_standings" boolean DEFAULT true NOT NULL,
	"has_players" boolean DEFAULT true NOT NULL,
	"last_audited_at" timestamp with time zone
);
--> statement-breakpoint
CREATE TABLE "provider_daily_budget" (
	"id" text PRIMARY KEY NOT NULL,
	"provider" text DEFAULT 'api_football' NOT NULL,
	"budget_date" date NOT NULL,
	"allocated_calls" integer DEFAULT 100 NOT NULL,
	"consumed_calls" integer DEFAULT 0 NOT NULL,
	"remaining_calls" integer DEFAULT 100 NOT NULL,
	"is_exhausted" boolean DEFAULT false NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE "standings" (
	"id" text PRIMARY KEY NOT NULL,
	"competition_id" text NOT NULL,
	"season_id" text NOT NULL,
	"team_id" text NOT NULL,
	"position" integer NOT NULL,
	"played" integer DEFAULT 0 NOT NULL,
	"won" integer DEFAULT 0 NOT NULL,
	"drawn" integer DEFAULT 0 NOT NULL,
	"lost" integer DEFAULT 0 NOT NULL,
	"goals_for" integer DEFAULT 0 NOT NULL,
	"goals_against" integer DEFAULT 0 NOT NULL,
	"goal_difference" integer DEFAULT 0 NOT NULL,
	"points" integer DEFAULT 0 NOT NULL,
	"form" text,
	"description" text,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE "teams" (
	"id" text PRIMARY KEY NOT NULL,
	"name" text NOT NULL,
	"short_name" text NOT NULL,
	"code" text,
	"country" text,
	"logo_url" text,
	"founded" integer,
	"venue_name" text,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
ALTER TABLE "competition_seasons" ADD CONSTRAINT "competition_seasons_competition_id_competitions_id_fk" FOREIGN KEY ("competition_id") REFERENCES "public"."competitions"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "events" ADD CONSTRAINT "events_fixture_id_fixtures_id_fk" FOREIGN KEY ("fixture_id") REFERENCES "public"."fixtures"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "events" ADD CONSTRAINT "events_team_id_teams_id_fk" FOREIGN KEY ("team_id") REFERENCES "public"."teams"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "fixture_statistics" ADD CONSTRAINT "fixture_statistics_fixture_id_fixtures_id_fk" FOREIGN KEY ("fixture_id") REFERENCES "public"."fixtures"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "fixture_statistics" ADD CONSTRAINT "fixture_statistics_team_id_teams_id_fk" FOREIGN KEY ("team_id") REFERENCES "public"."teams"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "fixtures" ADD CONSTRAINT "fixtures_competition_id_competitions_id_fk" FOREIGN KEY ("competition_id") REFERENCES "public"."competitions"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "fixtures" ADD CONSTRAINT "fixtures_season_id_competition_seasons_id_fk" FOREIGN KEY ("season_id") REFERENCES "public"."competition_seasons"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "fixtures" ADD CONSTRAINT "fixtures_home_team_id_teams_id_fk" FOREIGN KEY ("home_team_id") REFERENCES "public"."teams"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "fixtures" ADD CONSTRAINT "fixtures_away_team_id_teams_id_fk" FOREIGN KEY ("away_team_id") REFERENCES "public"."teams"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "installation_settings" ADD CONSTRAINT "installation_settings_installation_id_installations_installation_id_fk" FOREIGN KEY ("installation_id") REFERENCES "public"."installations"("installation_id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "installation_settings" ADD CONSTRAINT "installation_settings_favorite_team_id_teams_id_fk" FOREIGN KEY ("favorite_team_id") REFERENCES "public"."teams"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "installation_settings" ADD CONSTRAINT "installation_settings_favorite_competition_id_competitions_id_fk" FOREIGN KEY ("favorite_competition_id") REFERENCES "public"."competitions"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "lineup_players" ADD CONSTRAINT "lineup_players_lineup_id_lineups_id_fk" FOREIGN KEY ("lineup_id") REFERENCES "public"."lineups"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "lineups" ADD CONSTRAINT "lineups_fixture_id_fixtures_id_fk" FOREIGN KEY ("fixture_id") REFERENCES "public"."fixtures"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "lineups" ADD CONSTRAINT "lineups_team_id_teams_id_fk" FOREIGN KEY ("team_id") REFERENCES "public"."teams"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "provider_coverage" ADD CONSTRAINT "provider_coverage_competition_id_competitions_id_fk" FOREIGN KEY ("competition_id") REFERENCES "public"."competitions"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "standings" ADD CONSTRAINT "standings_competition_id_competitions_id_fk" FOREIGN KEY ("competition_id") REFERENCES "public"."competitions"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "standings" ADD CONSTRAINT "standings_season_id_competition_seasons_id_fk" FOREIGN KEY ("season_id") REFERENCES "public"."competition_seasons"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "standings" ADD CONSTRAINT "standings_team_id_teams_id_fk" FOREIGN KEY ("team_id") REFERENCES "public"."teams"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
CREATE INDEX "jobs_status_scheduled_idx" ON "acquisition_jobs" USING btree ("status","scheduled_at");--> statement-breakpoint
CREATE INDEX "ledger_requested_at_idx" ON "api_request_ledger" USING btree ("requested_at");--> statement-breakpoint
CREATE UNIQUE INDEX "comp_season_unique_idx" ON "competition_seasons" USING btree ("competition_id","year");--> statement-breakpoint
CREATE INDEX "events_fixture_idx" ON "events" USING btree ("fixture_id");--> statement-breakpoint
CREATE UNIQUE INDEX "events_uid_idx" ON "events" USING btree ("event_uid");--> statement-breakpoint
CREATE UNIQUE INDEX "stats_fixture_team_idx" ON "fixture_statistics" USING btree ("fixture_id","team_id");--> statement-breakpoint
CREATE INDEX "fixtures_state_idx" ON "fixtures" USING btree ("state");--> statement-breakpoint
CREATE INDEX "fixtures_kickoff_idx" ON "fixtures" USING btree ("kickoff_time");--> statement-breakpoint
CREATE INDEX "fixtures_home_team_idx" ON "fixtures" USING btree ("home_team_id");--> statement-breakpoint
CREATE INDEX "fixtures_away_team_idx" ON "fixtures" USING btree ("away_team_id");--> statement-breakpoint
CREATE INDEX "lineup_players_lineup_idx" ON "lineup_players" USING btree ("lineup_id");--> statement-breakpoint
CREATE UNIQUE INDEX "lineup_fixture_team_idx" ON "lineups" USING btree ("fixture_id","team_id");--> statement-breakpoint
CREATE UNIQUE INDEX "provider_comp_season_idx" ON "provider_coverage" USING btree ("provider","competition_id","season_year");--> statement-breakpoint
CREATE UNIQUE INDEX "budget_provider_date_idx" ON "provider_daily_budget" USING btree ("provider","budget_date");--> statement-breakpoint
CREATE UNIQUE INDEX "standings_comp_season_team_idx" ON "standings" USING btree ("competition_id","season_id","team_id");--> statement-breakpoint
CREATE INDEX "standings_comp_season_pos_idx" ON "standings" USING btree ("competition_id","season_id","position");