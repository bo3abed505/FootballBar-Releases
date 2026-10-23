import { z } from 'zod';
import { FotmobMatchStatusSchema } from './matches.schema.js';

/**
 * NOTE: FotMob routinely sends `null` (not just omitted keys) for branches that
 * are not yet available — e.g. `content.stats: null` before kickoff or
 * `content.stats.Periods.All: null` early in live play. Every optional field /
 * nested object here is therefore `.nullish()` so a null branch never discards
 * an otherwise valid payload (lineups, events, live status).
 */

export const FotmobShotSchema = z.object({
  id: z.union([z.number(), z.string()]).nullish(),
  eventType: z.string().nullish(),
  teamId: z.union([z.number(), z.string()]).nullish(),
  playerId: z.union([z.number(), z.string()]).nullish(),
  playerName: z.string().nullish(),
  x: z.number().nullish(),
  y: z.number().nullish(),
  min: z.number().nullish(),
  minAdded: z.number().nullish(),
  isBlocked: z.boolean().nullish(),
  isOnTarget: z.boolean().nullish(),
  blockedX: z.number().nullish(),
  blockedY: z.number().nullish(),
  goalCrossedY: z.number().nullish(),
  goalCrossedZ: z.number().nullish(),
  expectedGoals: z.number().nullish(),
  expectedGoalsOnTarget: z.number().nullish(),
  shotType: z.string().nullish(),
  situation: z.string().nullish(),
  period: z.string().nullish(),
  isOwnGoal: z.boolean().nullish(),
  onGoalShot: z.any().nullish(),
  isSavedOffLine: z.boolean().nullish(),
  firstName: z.string().nullish(),
  lastName: z.string().nullish(),
  fullName: z.string().nullish(),
}).passthrough();

export const FotmobMatchEventRawSchema = z.object({
  eventId: z.union([z.number(), z.string()]).nullish(),
  type: z.string().nullish(),
  time: z.union([z.number(), z.string()]).nullish(),
  overloadTime: z.union([z.number(), z.string()]).nullish(),
  teamId: z.union([z.number(), z.string()]).nullish(),
  isHome: z.boolean().nullish(),
  card: z.string().nullish(),
  cardColor: z.string().nullish(),
  player: z.any().nullish(),
  playerName: z.string().nullish(),
  nameStr: z.string().nullish(),
  swap: z.any().nullish(),
  assist: z.any().nullish(),
  assistStr: z.string().nullish(),
  description: z.string().nullish(),
  VAR: z.any().nullish(),
  minutesAddedStr: z.string().nullish(),
  minutesAdded: z.union([z.number(), z.string()]).nullish(),
  half: z.union([z.number(), z.string()]).nullish(),
  injured: z.boolean().nullish(),
}).passthrough();

export const FotmobStatItemSchema = z.object({
  key: z.string().nullish(),
  title: z.string().nullish(),
  stats: z.array(z.union([z.string(), z.number(), z.null()])).nullish(),
}).passthrough();

export type FotmobShot = z.infer<typeof FotmobShotSchema>;

export interface FotmobMatchDetailsResponse {
  general: {
    matchId: string;
    matchTimeUTCDate?: string | null;
    started: boolean;
    finished: boolean;
    leagueId?: number | string | null;
    parentLeagueId?: number | string | null;
    homeTeam?: any;
    awayTeam?: any;
    [key: string]: any;
  };
  header?: {
    status?: any;
    teams?: Array<{ id: string; name: string; score?: number | null; [key: string]: any }> | null;
    events?: any;
    [key: string]: any;
  } | null;
  content?: {
    lineup?: any;
    matchFacts?: any;
    liveticker?: any;
    shotmap?: {
      shots?: FotmobShot[] | null;
      [key: string]: any;
    } | null;
    stats?: any;
    [key: string]: any;
  } | null;
  [key: string]: any;
}

export const FotmobMatchDetailsResponseSchema: z.ZodType<FotmobMatchDetailsResponse, z.ZodTypeDef, any> = z.object({
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
    liveticker: z.any().nullish(),
    shotmap: z.object({
      shots: z.array(FotmobShotSchema).nullish(),
    }).passthrough().nullish(),
    stats: z.any().nullish(),
    h2h: z.any().nullish(),
    overview: z.any().nullish(),
    insights: z.any().nullish(),
    poll: z.any().nullish(),
    superlive: z.any().nullish(),
    commentary: z.any().nullish(),
    table: z.any().nullish(),
  }).passthrough().nullish(),
}).passthrough();

     