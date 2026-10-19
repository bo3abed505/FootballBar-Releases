import { z } from 'zod';
import { FotmobMatchStatusSchema } from './matches.schema.js';

/**
 * NOTE: FotMob routinely sends `null` (not just omitted keys) for branches that
 * are not yet available — e.g. `content.stats: null` before kickoff or
 * `content.stats.Periods.All: null` early in live play. Every optional field /
 * nested object here is therefore `.nullish()` so a null branch never discards
 * an otherwise valid payload (lineups, events, live status).
 */

export const FotmobStatItemSchema = z.object({
  key: z.string().nullish(),
  title: z.string().nullish(),
  stats: z.array(z.union([z.string(), z.number(), z.null()])).nullish(),
}).passthrough();

export const FotmobMatchDetailsResponseSchema = z.object({
  general: z.object({
    matchId: z.union([z.number(), z.string()]).transform(val => String(val)),
    matchTimeUTCDate: z.string().nullish(),
    started: z.boolean().nullish().transform(v => v ?? false),
    finished: z.boolean().nullish().transform(v => v ?? false),
    leagueId: z.union([z.number(), z.string()]).nullish(),
    parentLeagueId: z.union([z.number(), z.string()]).nullish(),
    homeTeam: z.any().nullish(),
    awayTeam: z.any().nullish(),
  }).passthrough(),
  header: z.object({
    status: FotmobMatchStatusSchema.nullish(),
    teams: z.array(
      z.object({
        id: z.union([z.number(), z.string()]).transform(val => String(val)),
        name: z.string(),
        score: z.number().nullish(),
      }).passthrough()
    ).nullish(),
    events: z.any().nullish(),
  }).passthrough().nullish(),
  content: z.object({
    lineup: z.any().nullish(),
    matchFacts: z.any().nullish(),
    stats: z.object({
      Periods: z.object({
        All: z.object({
          stats: z.array(
            z.object({
              title: z.string().nullish(),
              key: z.string().nullish(),
              stats: z.array(FotmobStatItemSchema).nullish(),
            }).passthrough()
          ).nullish(),
        }).passthrough().nullish(),
      }).passthrough().nullish(),
    }).passthrough().nullish(),
  }).passthrough().nullish(),
}).passthrough();

export type FotmobMatchDetailsResponse = z.infer<typeof FotmobMatchDetailsResponseSchema>;
