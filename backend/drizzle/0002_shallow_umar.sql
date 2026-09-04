CREATE TABLE "scheduler_cycles" (
	"id" text PRIMARY KEY NOT NULL,
	"action" text NOT NULL,
	"priority" text DEFAULT 'NORMAL' NOT NULL,
	"reason" text NOT NULL,
	"target_competition_ids" jsonb,
	"started_at" timestamp with time zone NOT NULL,
	"completed_at" timestamp with time zone,
	"duration_ms" integer,
	"outcome" text,
	"provider_requests_count" integer DEFAULT 0 NOT NULL,
	"provider_requests_cost" integer DEFAULT 0 NOT NULL,
	"error_type" text,
	"error_message" text,
	"fixtures_discovered" integer DEFAULT 0 NOT NULL,
	"fixtures_updated" integer DEFAULT 0 NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
ALTER TABLE "api_request_ledger" ADD COLUMN "cycle_id" text;--> statement-breakpoint
ALTER TABLE "api_request_ledger" ADD COLUMN "competition_id" text;--> statement-breakpoint
CREATE INDEX "scheduler_cycles_started_idx" ON "scheduler_cycles" USING btree ("started_at");--> statement-breakpoint
CREATE INDEX "scheduler_cycles_action_idx" ON "scheduler_cycles" USING btree ("action","started_at");--> statement-breakpoint
CREATE INDEX "scheduler_cycles_pruning_idx" ON "scheduler_cycles" USING btree ("outcome","started_at");--> statement-breakpoint
CREATE INDEX "ledger_cycle_id_idx" ON "api_request_ledger" USING btree ("cycle_id");--> statement-breakpoint
CREATE INDEX "ledger_competition_id_idx" ON "api_request_ledger" USING btree ("competition_id");