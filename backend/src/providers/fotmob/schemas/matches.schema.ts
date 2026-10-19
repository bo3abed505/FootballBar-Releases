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
  name: z.string(),
  longName: z.string().nullish(),
  shortName: z.string().nullish(),
  score: z.number().nullish(),
}).passthrough();

export const FotmobMatchItemSchema = z.object({
  id: z.union([z.number(), z.string()]).transform(val => String(val)),
  home: FotmobTeamItemSchema,
  away: FotmobTeamItemSchema,
  status: FotmobMatchStatusSchema,
  time: z.string().nullish(),
}).passthrough();

export const FotmobLeagueSchema = z.object({
  id: z.union([z.number(), z.string()]).transform(val => String(val)),
  name: z.string(),
  /** FotMob country code, e.g. "ENG", "FRA", "INT", "KSA". */
  ccode: z.string().nullish(),
  /** Parent league ID when `id` is a stage/group ID (e.g. 42 for UCL stage 943230). */
  primaryId: z.number().nullish(),
  matches: z.array(FotmobMatchItemSchema).default([]),
}).passthrough();

/** @deprecated Alias kept for backward compatibility. Use FotmobLeagueSchema. */
export const FotmobLeagueGroupSchema = FotmobLeagueSchema;

export const FotmobMatchesResponseSchema = z.object({
  leagues: z.array(FotmobLeagueSchema).nullish().transform(v => v ?? []),
}).passthrough();

export type FotmobMatchesResponse = z.infer<typeof FotmobMatchesResponseSchema>;
export type FotmobLeague = z.infer<typeof FotmobLeagueSchema>;
