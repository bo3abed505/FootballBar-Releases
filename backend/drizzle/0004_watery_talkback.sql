UPDATE "competitions" SET "canonical_id" = 'ft_comp_' || upper(substring(md5(random()::text || clock_timestamp()::text) from 1 for 26)) WHERE "canonical_id" IS NULL;--> statement-breakpoint
UPDATE "teams" SET "canonical_id" = 'ft_team_' || upper(substring(md5(random()::text || clock_timestamp()::text) from 1 for 26)) WHERE "canonical_id" IS NULL;--> statement-breakpoint
UPDATE "fixtures" SET "canonical_id" = 'ft_fix_' || upper(substring(md5(random()::text || clock_timestamp()::text) from 1 for 26)) WHERE "canonical_id" IS NULL;--> statement-breakpoint
ALTER TABLE "competitions" ALTER COLUMN "canonical_id" SET NOT NULL;--> statement-breakpoint
ALTER TABLE "fixtures" ALTER COLUMN "canonical_id" SET NOT NULL;--> statement-breakpoint
ALTER TABLE "teams" ALTER COLUMN "canonical_id" SET NOT NULL;