ALTER TABLE "fixtures" ADD COLUMN "live_source_provider" text;--> statement-breakpoint
ALTER TABLE "fixtures" ADD COLUMN "live_observed_at" timestamp with time zone;--> statement-breakpoint
ALTER TABLE "fixtures" ADD COLUMN "schedule_source_provider" text;--> statement-breakpoint
ALTER TABLE "fixtures" ADD COLUMN "schedule_observed_at" timestamp with time zone;