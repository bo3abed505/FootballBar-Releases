ALTER TABLE "competitions" ADD COLUMN "canonical_id" text;--> statement-breakpoint
ALTER TABLE "fixtures" ADD COLUMN "canonical_id" text;--> statement-breakpoint
ALTER TABLE "teams" ADD COLUMN "canonical_id" text;--> statement-breakpoint
ALTER TABLE "competitions" ADD CONSTRAINT "competitions_canonical_id_unique" UNIQUE("canonical_id");--> statement-breakpoint
ALTER TABLE "fixtures" ADD CONSTRAINT "fixtures_canonical_id_unique" UNIQUE("canonical_id");--> statement-breakpoint
ALTER TABLE "teams" ADD CONSTRAINT "teams_canonical_id_unique" UNIQUE("canonical_id");--> statement-breakpoint
CREATE TABLE "competition_provider_aliases" (
	"canonical_competition_id" text NOT NULL,
	"provider" text NOT NULL,
	"provider_id" text NOT NULL,
	"first_seen_at" timestamp with time zone DEFAULT now() NOT NULL,
	"last_seen_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "competition_provider_aliases_provider_provider_id_pk" PRIMARY KEY("provider","provider_id")
);
--> statement-breakpoint
CREATE TABLE "fixture_provider_aliases" (
	"canonical_fixture_id" text NOT NULL,
	"provider" text NOT NULL,
	"provider_id" text NOT NULL,
	"first_seen_at" timestamp with time zone DEFAULT now() NOT NULL,
	"last_seen_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "fixture_provider_aliases_provider_provider_id_pk" PRIMARY KEY("provider","provider_id")
);
--> statement-breakpoint
CREATE TABLE "team_provider_aliases" (
	"canonical_team_id" text NOT NULL,
	"provider" text NOT NULL,
	"provider_id" text NOT NULL,
	"first_seen_at" timestamp with time zone DEFAULT now() NOT NULL,
	"last_seen_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "team_provider_aliases_provider_provider_id_pk" PRIMARY KEY("provider","provider_id")
);
--> statement-breakpoint
ALTER TABLE "competition_provider_aliases" ADD CONSTRAINT "competition_provider_aliases_canonical_competition_id_competitions_canonical_id_fk" FOREIGN KEY ("canonical_competition_id") REFERENCES "public"."competitions"("canonical_id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "fixture_provider_aliases" ADD CONSTRAINT "fixture_provider_aliases_canonical_fixture_id_fixtures_canonical_id_fk" FOREIGN KEY ("canonical_fixture_id") REFERENCES "public"."fixtures"("canonical_id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "team_provider_aliases" ADD CONSTRAINT "team_provider_aliases_canonical_team_id_teams_canonical_id_fk" FOREIGN KEY ("canonical_team_id") REFERENCES "public"."teams"("canonical_id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
CREATE INDEX "comp_provider_aliases_canonical_idx" ON "competition_provider_aliases" USING btree ("canonical_competition_id");--> statement-breakpoint
CREATE INDEX "fix_provider_aliases_canonical_idx" ON "fixture_provider_aliases" USING btree ("canonical_fixture_id");--> statement-breakpoint
CREATE INDEX "team_provider_aliases_canonical_idx" ON "team_provider_aliases" USING btree ("canonical_team_id");