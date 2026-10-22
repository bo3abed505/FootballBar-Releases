import { z } from 'zod';

const nullishBool = z.boolean().nullish().transform(v => v ?? false);

export const FotmobMatchStatusSchema = z.object({
  utcTime: z.string().nullish(),
  started: nullishBool,
  finished: nullishBool,
  cancelled: nullishBool,
  ongoing: z.boolean().nullish(),
  scoreStr: z.string().nullish(),
  reason: z.object({
    short: z.string().nullish(),
    long: z.string().nullish(),
  }).passthrough().nullish(),
  liveTime: z.object({
    short: z.string().nullish(),
    long: z.string().nullish(),
    addedTime: z.number().nullish(),
    maxTime: z.number().nullish(),
  }).passthrough().nullish(),
}).passthrough();

export const FotmobTeamItemSchema = z.object({
  id: z.union([z.number(), z.string()]).transform(val => String(val)),
  name: z.string().nullish().transform(v => v ?? 'Unknown Team'),
  longName: z.string().nullish(),
  shortName: z.string().nullish(),
  score: z.number().nullish(),
}).passthrough();

export const FotmobMatchItemSchema = z.object({
  id: z.union([z.number(), z.string()]).transform(val => String(val)),
  home: FotmobTeamItemSchema.nullish(),
  away: FotmobTeamItemSchema.nullish(),
  status: FotmobMatchStatusSchema.default({}),
  time: z.string().nullish(),
}).passthrough();

export const FotmobLeagueSchema = z.object({
  id: z.union([z.number(), z.string()]).transform(val => String(val)),
  name: z.string().nullish().transform(v => v ?? 'Unknown League'),
  /** FotMob country code, e.g. "ENG", "FRA", "INT", "KSA". */
  ccode: z.string().nullish(),
  /** Parent league ID when `id` is a stage/group ID (e.g. 42 for UCL stage 943230). */
  primaryId: z.number().nullish(),
  matches: z.array(z.any()).nullish().transform(rawMatches => {
    if (!Array.isArray(rawMatches)) return [];
    const validMatches: Array<z.infer<typeof FotmobMatchItemSchema>> = [];
    for (const m of rawMatches) {
      const res = FotmobMatchItemSchema.safeParse(m);
      if (res.success && res.data && res.data.home && res.data.away) {
        validMatches.push(res.data);
      }
    }
    return validMatches;
  }),
}).passthrough();

/** @deprecated Alias kept for backward compatibility. Use FotmobLeagueSchema. */
export const FotmobLeagueGroupSchema = FotmobLeagueSchema;

export const FotmobMatchesResponseSchema = z.object({
  leagues: z.array(z.any()).nullish().transform(rawLeagues => {
    if (!Array.isArray(rawLeagues)) return [];
    const validLeagues: Array<z.infer<typeof FotmobLeagueSchema>> = [];
    for (const l of rawLeagues) {
      const res = FotmobLeagueSchema.safeParse(l);
      if (res.success && res.data) {
        validLeagues.push(res.data);
      }
    }
    return validLeagues;
  }),
}).passthrough();

export type FotmobMatchesResponse = z.infer<typeof FotmobMatchesResponseSchema>;
export type FotmobLeague = z.infer<typeof FotmobLeagueSchema>;
