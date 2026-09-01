ALTER TABLE "api_request_ledger" ADD COLUMN "operation" text DEFAULT 'unknown' NOT NULL;--> statement-breakpoint
ALTER TABLE "api_request_ledger" ADD COLUMN "priority" text DEFAULT 'NORMAL' NOT NULL;--> statement-breakpoint
ALTER TABLE "api_request_ledger" ADD COLUMN "success" boolean DEFAULT true NOT NULL;--> statement-breakpoint
ALTER TABLE "api_request_ledger" ADD COLUMN "provider_daily_remaining" integer;--> statement-breakpoint
ALTER TABLE "api_request_ledger" ADD COLUMN "provider_minute_remaining" integer;--> statement-breakpoint
ALTER TABLE "provider_coverage" ADD COLUMN "has_fixtures" boolean DEFAULT true NOT NULL;--> statement-breakpoint
ALTER TABLE "provider_coverage" ADD COLUMN "has_commentary" boolean DEFAULT false NOT NULL;--> statement-breakpoint
ALTER TABLE "provider_coverage" ADD COLUMN "has_ratings" boolean DEFAULT false NOT NULL;--> statement-breakpoint
ALTER TABLE "provider_coverage" ADD COLUMN "has_xg" boolean DEFAULT false NOT NULL;--> statement-breakpoint
CREATE INDEX "ledger_provider_requested_idx" ON "api_request_ledger" USING btree ("provider","requested_at");