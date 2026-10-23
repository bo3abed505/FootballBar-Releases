import { eq, desc, or, and, gte, lte, inArray, lt, like, asc, sql, notLike, notInArray, isNull } from 'drizzle-orm';
import { db, schema } from '../db/index.js';
import { AcquisitionPolicyEngine, FixturesSummary } from '../acquisition/acquisition-policy.js';
import { generateCanonicalId } from '../db/canonical-id.js';
import { resolveFixtureCanonicalIdByAlias, extractProviderId } from '../db/provider-alias.js';

export interface GetTrayMatchesOptions {
  date?: string | Date | null;
  teams?: string[] | null;
  competitions?: string[] | null;
  limit?: number | null;
}

export interface ActiveSummaryOptions {
  followedTeamIds?: Array<string | number>;
  followedCompetitionIds?: Array<string | number>;
  pinnedMatchIds?: Array<string | number>;
}

export class FixturesRepository {
  async countTotalFixtures(): Promise<number> {
    const result = await db.select({ count: sql<number>`count(*)` }).from(schema.fixtures);
    return Number(result[0]?.count ?? 0);
  }

  async countNonMockFixtures(): Promise<number> {
    const result = await db
      .select({ count: sql<number>`count(*)` })
      .from(schema.fixtures)
      .where(
        and(
          notLike(schema.fixtures.id, '%mock%'),
          notInArray(schema.fixtures.id, ['fix_elclasico_2026', 'fix_spain_egypt_2026'])
        )
      );
    return Number(result[0]?.count ?? 0);
  }

  async hasFixturesForDate(dateStr: string): Promise<boolean> {
    const startOfDate = new Date(`${dateStr}T00:00:00.000Z`);
    const endOfDate = new Date(startOfDate.getTime() + 24 * 60 * 60 * 1000);
    const rows = await db
      .select({ id: schema.fixtures.id })
      .from(schema.fixtures)
      .where(
        and(
          gte(schema.fixtures.kickoffTime, startOfDate),
          lt(schema.fixtures.kickoffTime, endOfDate)
        )
      )
      .limit(1);
    return rows.length > 0;
  }

  async getFollowedCountsByDate(options: {
    teams?: string[] | null;
    competitions?: string[] | null;
    fromDate: Date;
    toDate: Date;
  }): Promise<Record<string, number>> {
    const counts: Record<string, number> = {};
    const { teams: targetTeams, competitions: targetCompetitions, fromDate, toDate } = options;

    if ((!targetTeams || targetTeams.length === 0) && (!targetCompetitions || targetCompetitions.length === 0)) {
      return counts;
    }

    const expandedTeamIds = new Set<string>();
    const expandedCompIds = new Set<string>();

    if (targetTeams && targetTeams.length > 0) {
      for (const t of targetTeams) {
        if (!t) continue;
        const trimmed = t.trim();
        if (!trimmed) continue;
        if (trimmed.startsWith('comp_') || trimmed.startsWith('ft_comp_')) {
          expandedCompIds.add(trimmed);
          continue;
        }
        expandedTeamIds.add(trimmed);
        if (trimmed.startsWith('team_')) {
          expandedTeamIds.add(trimmed.substring(5));
        } else if (!trimmed.startsWith('ft_team_')) {
          expandedTeamIds.add(`team_${trimmed}`);
        }
      }

      const canonicalCandidates = Array.from(expandedTeamIds).filter((id) => id.startsWith('ft_team_'));
      if (canonicalCandidates.length > 0) {
        try {
          const matchedTeams = await db.query.teams.findMany({
            where: inArray(schema.teams.canonicalId, canonicalCandidates),
            columns: { id: true, canonicalId: true },
          });
          for (const mt of matchedTeams) {
            if (mt.id) expandedTeamIds.add(mt.id);
          }
        } catch {
          // Non-critical fallback
        }
      }
    }

    if (targetCompetitions && targetCompetitions.length > 0) {
      for (const c of targetCompetitions) {
        if (!c) continue;
        const trimmed = c.trim();
        if (!trimmed) continue;
        expandedCompIds.add(trimmed);
        if (trimmed.startsWith('comp_')) {
          expandedCompIds.add(trimmed.substring(5));
        } else if (!trimmed.startsWith('ft_comp_')) {
          expandedCompIds.add(`comp_${trimmed}`);
        }
      }

      const canonicalCompCandidates = Array.from(expandedCompIds).filter((id) => id.startsWith('ft_comp_'));
      if (canonicalCompCandidates.length > 0) {
        try {
          const matchedComps = await db.query.competitions.findMany({
            where: inArray(schema.competitions.canonicalId, canonicalCompCandidates),
            columns: { id: true, canonicalId: true },
          });
          for (const mc of matchedComps) {
            if (mc.id) expandedCompIds.add(mc.id);
          }
        } catch {
          // Non-critical fallback
        }
      }
    }

    const conditions: any[] = [];
    if (expandedTeamIds.size > 0) {
      const arr = Array.from(expandedTeamIds);
      conditions.push(inArray(schema.fixtures.homeTeamId, arr));
      conditions.push(inArray(schema.fixtures.awayTeamId, arr));
    }
    if (expandedCompIds.size > 0) {
      const arr = Array.from(expandedCompIds);
      conditions.push(inArray(schema.fixtures.competitionId, arr));
    }

    if (conditions.length === 0) {
      return counts;
    }

    const rows = await db
      .select({
        id: schema.fixtures.id,
        kickoffTime: schema.fixtures.kickoffTime,
      })
      .from(schema.fixtures)
      .where(
        and(
          gte(schema.fixtures.kickoffTime, fromDate),
          lte(schema.fixtures.kickoffTime, toDate),
          or(...conditions)
        )
      );

    for (const r of rows) {
      const dateKey = new Date(r.kickoffTime).toISOString().slice(0, 10);
      counts[dateKey] = (counts[dateKey] ?? 0) + 1;
    }

    return counts;
  }

  async getFollowedTeamCompetitions(targetTeams?: string[] | null): Promise<Array<{
    id: string;
    canonicalId?: string;
    name: string;
    country?: string | null;
    logoUrl?: string | null;
  }>> {
    if (!targetTeams || targetTeams.length === 0) {
      return [];
    }

    const expandedTeamIds = new Set<string>();
    for (const t of targetTeams) {
      if (!t) continue;
      const trimmed = t.trim();
      if (!trimmed) continue;
      expandedTeamIds.add(trimmed);
      if (trimmed.startsWith('team_')) {
        expandedTeamIds.add(trimmed.substring(5));
      } else if (!trimmed.startsWith('ft_team_')) {
        expandedTeamIds.add(`team_${trimmed}`);
      }
    }

    const canonicalCandidates = Array.from(expandedTeamIds).filter((id) => id.startsWith('ft_team_'));
    if (canonicalCandidates.length > 0) {
      try {
        const matchedTeams = await db.query.teams.findMany({
          where: inArray(schema.teams.canonicalId, canonicalCandidates),
          columns: { id: true, canonicalId: true },
        });
        for (const mt of matchedTeams) {
          if (mt.id) expandedTeamIds.add(mt.id);
        }
      } catch {
        // Non-critical fallback
      }
    }

    if (expandedTeamIds.size === 0) {
      return [];
    }

    const teamArr = Array.from(expandedTeamIds);

    const rows = await db
      .select({
        id: schema.competitions.id,
        canonicalId: schema.competitions.canonicalId,
        name: schema.competitions.name,
        country: schema.competitions.country,
        logoUrl: schema.competitions.logoUrl,
      })
      .from(schema.fixtures)
      .innerJoin(schema.competitions, eq(schema.fixtures.competitionId, schema.competitions.id))
      .where(
        or(
          inArray(schema.fixtures.homeTeamId, teamArr),
          inArray(schema.fixtures.awayTeamId, teamArr)
        )
      );

    const distinctMap = new Map<string, { id: string; canonicalId?: string; name: string; country?: string | null; logoUrl?: string | null }>();
    for (const row of rows) {
      if (!row.id || distinctMap.has(row.id)) continue;

      let logo = row.logoUrl;
      if (!logo || logo.includes('placeholder') || logo.endsWith('/comp.png')) {
        const digits = row.id.match(/\d+/)?.[0];
        if (digits) {
          logo = `https://images.fotmob.com/image_resources/logo/leaguelogo/dark/${digits}.png`;
        }
      }

      distinctMap.set(row.id, {
        id: row.id,
        canonicalId: row.canonicalId,
        name: row.name,
        country: row.country,
        logoUrl: logo,
      });
    }

    return Array.from(distinctMap.values());
  }

  async getAll(limit = 50) {
    return await db.query.fixtures.findMany({
      columns: {
        canonicalId: false,
      },
      with: {
        competition: {
          columns: {
            canonicalId: false,
          },
        },
        homeTeam: {
          columns: {
            canonicalId: false,
          },
        },
        awayTeam: {
          columns: {
            canonicalId: false,
          },
        },
        events: true,
      },
      orderBy: [desc(schema.fixtures.kickoffTime)],
      limit,
    });
  }

  async getById(id: string) {
    return await db.query.fixtures.findFirst({
      where: eq(schema.fixtures.id, id),
      columns: {
        canonicalId: false,
      },
      with: {
        competition: {
          columns: {
            canonicalId: false,
          },
        },
        season: true,
        homeTeam: {
          columns: {
            canonicalId: false,
          },
        },
        awayTeam: {
          columns: {
            canonicalId: false,
          },
        },
        events: true,
        lineups: {
          with: {
            players: true,
          },
        },
        statistics: true,
      },
    });
  }

  async getByCanonicalOrLegacyId(identifier: string) {
    const trimmed = identifier.trim();
    if (!trimmed) return null;

    const findFixture = (whereClause: any) =>
      db.query.fixtures.findFirst({
        where: whereClause,
        with: {
          competition: true,
          season: true,
          homeTeam: true,
          awayTeam: true,
          events: {
            orderBy: [asc(schema.events.minute), asc(schema.events.extraMinute)],
          },
          lineups: {
            with: {
              players: true,
            },
          },
          statistics: true,
        },
      });

    // 1. Check directly by canonicalId
    let fixture = await findFixture(eq(schema.fixtures.canonicalId, trimmed));
    if (fixture) return fixture;

    // 2. Check directly by primary ID
    fixture = await findFixture(eq(schema.fixtures.id, trimmed));
    if (fixture) return fixture;

    // 3. Resolve via fixture_provider_aliases (e.g. provider_id = '123' or 'fix_123')
    const canonicalFromAlias = await resolveFixtureCanonicalIdByAlias(db, trimmed);
    if (canonicalFromAlias) {
      return await findFixture(eq(schema.fixtures.canonicalId, canonicalFromAlias));
    }

    return null;
  }

  async getTrayMatches(
    optionsOrLimit?: GetTrayMatchesOptions | number,
    now: Date = new Date()
  ) {
    const nowMs = now.getTime();

    let limit: number | undefined = undefined;
    let targetDate: string | Date | null = null;
    let targetTeams: string[] | null = null;
    let targetCompetitions: string[] | null = null;

    if (typeof optionsOrLimit === 'number') {
      limit = optionsOrLimit;
    } else if (optionsOrLimit && typeof optionsOrLimit === 'object') {
      limit = optionsOrLimit.limit ?? undefined;
      targetDate = optionsOrLimit.date ?? null;
      targetTeams = optionsOrLimit.teams ?? null;
      targetCompetitions = optionsOrLimit.competitions ?? null;
    }

    const LIVE_STATES = [
      'LIVE', 'HALFTIME', '1H', '2H', 'ET', 'P', 'BT', 'IN_PLAY',
      'FIRST_HALF', 'SECOND_HALF', 'EXTRA_TIME', 'EXTRA_TIME_HALFTIME', 'PENALTIES', 'HT'
    ];
    const PREMATCH_STATES = ['PREMATCH', 'NS', 'TBD', 'TIMED', 'SCHEDULED', 'NOT_STARTED'];
    const FINISHED_STATES = ['FINISHED', 'FT', 'AET', 'PEN', 'POSTMATCH', 'FINISHED_AET', 'FINISHED_PEN'];

    let activeDate = now;
    if (targetDate) {
      if (typeof targetDate === 'string') {
        const parts = targetDate.trim().split('-');
        if (parts.length === 3) {
          const year = parseInt(parts[0], 10);
          const month = parseInt(parts[1], 10) - 1;
          const day = parseInt(parts[2], 10);
          activeDate = new Date(Date.UTC(year, month, day, 0, 0, 0, 0));
        } else {
          const parsed = new Date(targetDate);
          if (!isNaN(parsed.getTime())) {
            activeDate = parsed;
          }
        }
      } else if (targetDate instanceof Date && !isNaN(targetDate.getTime())) {
        activeDate = targetDate;
      }
    }

    const startOfActiveDay = new Date(Date.UTC(activeDate.getUTCFullYear(), activeDate.getUTCMonth(), activeDate.getUTCDate(), 0, 0, 0, 0));
    const endOfActiveDay = new Date(Date.UTC(activeDate.getUTCFullYear(), activeDate.getUTCMonth(), activeDate.getUTCDate(), 23, 59, 59, 999));

    // Live match cutoffs:
    // If targetDate is specified, bound to the selected day.
    // If real-time tray (targetDate is null), include matches active today or kicking off within approaching window.
    const livePastCutoff = targetDate
      ? startOfActiveDay
      : new Date(Math.max(startOfActiveDay.getTime(), nowMs - AcquisitionPolicyEngine.MAX_LIVE_MATCH_AGE_MS));
    const liveFutureCutoff = targetDate
      ? endOfActiveDay
      : new Date(Math.max(endOfActiveDay.getTime(), nowMs + AcquisitionPolicyEngine.APPROACHING_WINDOW_MS));

    // Prematch schedule: include active day and approaching prematch window (+24h)
    const prematchPastCutoff = startOfActiveDay;
    const prematchFutureCutoff = targetDate
      ? endOfActiveDay
      : new Date(Math.max(nowMs + 24 * 60 * 60 * 1000, endOfActiveDay.getTime()));

    // Finished matches: bounded to active day
    const finishedPastCutoff = startOfActiveDay;
    const finishedFutureCutoff = endOfActiveDay;

    // Expand teams to include legacy IDs, stripped numeric prefixes, and resolve canonical IDs
    const expandedTeamIds = new Set<string>();
    const expandedCompIds = new Set<string>();

    if (targetTeams && targetTeams.length > 0) {
      for (const t of targetTeams) {
        if (!t) continue;
        const trimmed = t.trim();
        if (!trimmed) continue;
        if (trimmed.startsWith('comp_') || trimmed.startsWith('ft_comp_')) {
          expandedCompIds.add(trimmed);
          continue;
        }
        expandedTeamIds.add(trimmed);
        if (trimmed.startsWith('team_')) {
          expandedTeamIds.add(trimmed.substring(5));
        } else if (!trimmed.startsWith('ft_team_')) {
          expandedTeamIds.add(`team_${trimmed}`);
        }
      }

      // Check if any canonical team IDs (e.g. ft_team_...) were provided and resolve their legacy IDs
      const canonicalCandidates = Array.from(expandedTeamIds).filter((id) => id.startsWith('ft_team_'));
      if (canonicalCandidates.length > 0) {
        try {
          const matchedTeams = await db.query.teams.findMany({
            where: inArray(schema.teams.canonicalId, canonicalCandidates),
            columns: { id: true, canonicalId: true },
          });
          for (const mt of matchedTeams) {
            if (mt.id) expandedTeamIds.add(mt.id);
          }
        } catch {
          // Non-critical fallback
        }
      }
    }

    if (targetCompetitions && targetCompetitions.length > 0) {
      for (const c of targetCompetitions) {
        if (!c) continue;
        const trimmed = c.trim();
        if (!trimmed) continue;
        expandedCompIds.add(trimmed);
        if (trimmed.startsWith('comp_')) {
          expandedCompIds.add(trimmed.substring(5));
        } else if (!trimmed.startsWith('ft_comp_')) {
          expandedCompIds.add(`comp_${trimmed}`);
        }
      }
    }

    // Check if any canonical competition IDs (e.g. ft_comp_...) were provided and resolve their legacy IDs
    const canonicalCompCandidates = Array.from(expandedCompIds).filter((id) => id.startsWith('ft_comp_'));
    if (canonicalCompCandidates.length > 0) {
      try {
        const matchedComps = await db.query.competitions.findMany({
          where: inArray(schema.competitions.canonicalId, canonicalCompCandidates),
          columns: { id: true, canonicalId: true },
        });
        for (const mc of matchedComps) {
          if (mc.id) expandedCompIds.add(mc.id);
        }
      } catch {
        // Non-critical fallback
      }
    }

    // Strict OR Query:
    // (Matches exactly on requested ?date) OR (Matches belonging to ?teams) OR (Matches belonging to ?competitions)
    const dateCondition = targetDate
      ? and(
          gte(schema.fixtures.kickoffTime, startOfActiveDay),
          lte(schema.fixtures.kickoffTime, endOfActiveDay)
        )
      : or(
          and(
            inArray(schema.fixtures.state, LIVE_STATES),
            gte(schema.fixtures.kickoffTime, livePastCutoff),
            lte(schema.fixtures.kickoffTime, liveFutureCutoff)
          ),
          and(
            inArray(schema.fixtures.state, PREMATCH_STATES),
            gte(schema.fixtures.kickoffTime, prematchPastCutoff),
            lte(schema.fixtures.kickoffTime, prematchFutureCutoff)
          ),
          and(
            inArray(schema.fixtures.state, FINISHED_STATES),
            gte(schema.fixtures.kickoffTime, finishedPastCutoff),
            lte(schema.fixtures.kickoffTime, finishedFutureCutoff)
          )
        );

    const orConditions: any[] = [dateCondition];

    // Include matches belonging to ?teams
    if (expandedTeamIds.size > 0) {
      const targetTeamList = Array.from(expandedTeamIds);
      orConditions.push(
        or(
          inArray(schema.fixtures.homeTeamId, targetTeamList),
          inArray(schema.fixtures.awayTeamId, targetTeamList)
        )
      );
    }

    // Include matches belonging to ?competitions
    if (expandedCompIds.size > 0) {
      const targetCompList = Array.from(expandedCompIds);
      orConditions.push(
        inArray(schema.fixtures.competitionId, targetCompList)
      );
    }

    return await db.query.fixtures.findMany({
      where: or(...orConditions),
      with: {
        competition: true,
        homeTeam: true,
        awayTeam: true,
        events: {
          orderBy: [desc(schema.events.minute), desc(schema.events.extraMinute)],
          limit: 5,
        },
      },
      orderBy: [
        sql`CASE
          WHEN ${schema.fixtures.state} IN ('LIVE', 'HALFTIME', '1H', '2H', 'ET', 'P', 'BT', 'IN_PLAY') THEN 0
          WHEN ${schema.fixtures.state} IN ('PREMATCH', 'NS', 'TBD') THEN 1
          WHEN ${schema.fixtures.state} IN ('FINISHED', 'FT', 'AET', 'PEN', 'POSTMATCH') THEN 2
          ELSE 3
        END ASC`,
        sql`CASE
          WHEN ${schema.fixtures.state} IN ('LIVE', 'HALFTIME', '1H', '2H', 'ET', 'P', 'BT', 'IN_PLAY', 'PREMATCH', 'NS', 'TBD')
            THEN ${schema.fixtures.kickoffTime}
        END ASC NULLS LAST`,
        sql`CASE
          WHEN ${schema.fixtures.state} IN ('FINISHED', 'FT', 'AET', 'PEN', 'POSTMATCH')
            THEN ${schema.fixtures.kickoffTime}
        END DESC NULLS LAST`,
        asc(schema.fixtures.id),
      ],
      ...(typeof limit === 'number' && limit > 0 ? { limit } : {}),
    });
  }

  async getLiveTrayMatches(
    options?: { teams?: string[] | null; competitions?: string[] | null; limit?: number | null },
    now: Date = new Date()
  ) {
    const nowMs = now.getTime();
    const LIVE_STATES = ['LIVE', 'HALFTIME', 'HT', '1H', '2H', 'FIRST_HALF', 'SECOND_HALF', 'EXTRA_TIME', 'EXTRA_TIME_HALFTIME', 'PENALTIES', 'ET', 'P', 'BT', 'IN_PLAY'];
    const FINISHED_STATES = ['FINISHED', 'FINISHED_AET', 'FINISHED_PEN', 'FT', 'AET', 'PEN', 'POSTMATCH'];

    const livePastCutoff = new Date(nowMs - AcquisitionPolicyEngine.MAX_LIVE_MATCH_AGE_MS);
    const approachingFutureCutoff = new Date(nowMs + 45 * 60 * 1000); // Kicking off within 45 min
    const recentFinishCutoff = new Date(nowMs - 15 * 60 * 1000);     // Concluded in last 15 min

    // Expand teams to include legacy IDs, stripped numeric prefixes, and resolve canonical IDs
    const expandedTeamIds = new Set<string>();
    const expandedCompIds = new Set<string>();

    if (options?.teams && options.teams.length > 0) {
      for (const t of options.teams) {
        if (!t) continue;
        const trimmed = t.trim();
        if (!trimmed) continue;
        if (trimmed.startsWith('comp_') || trimmed.startsWith('ft_comp_')) {
          expandedCompIds.add(trimmed);
          continue;
        }
        expandedTeamIds.add(trimmed);
        if (trimmed.startsWith('team_')) {
          expandedTeamIds.add(trimmed.substring(5));
        } else if (!trimmed.startsWith('ft_team_')) {
          expandedTeamIds.add(`team_${trimmed}`);
        }
      }

      const canonicalCandidates = Array.from(expandedTeamIds).filter((id) => id.startsWith('ft_team_'));
      if (canonicalCandidates.length > 0) {
        try {
          const matchedTeams = await db.query.teams.findMany({
            where: inArray(schema.teams.canonicalId, canonicalCandidates),
            columns: { id: true, canonicalId: true },
          });
          for (const mt of matchedTeams) {
            if (mt.id) expandedTeamIds.add(mt.id);
          }
        } catch {
          // Non-critical fallback
        }
      }
    }

    if (options?.competitions && options.competitions.length > 0) {
      for (const c of options.competitions) {
        if (!c) continue;
        const trimmed = c.trim();
        if (!trimmed) continue;
        expandedCompIds.add(trimmed);
        if (trimmed.startsWith('comp_')) {
          expandedCompIds.add(trimmed.substring(5));
        } else if (!trimmed.startsWith('ft_comp_')) {
          expandedCompIds.add(`comp_${trimmed}`);
        }
      }

      const canonicalCompCandidates = Array.from(expandedCompIds).filter((id) => id.startsWith('ft_comp_'));
      if (canonicalCompCandidates.length > 0) {
        try {
          const matchedComps = await db.query.competitions.findMany({
            where: inArray(schema.competitions.canonicalId, canonicalCompCandidates),
            columns: { id: true, canonicalId: true },
          });
          for (const mc of matchedComps) {
            if (mc.id) expandedCompIds.add(mc.id);
          }
        } catch {
          // Non-critical fallback
        }
      }
    }

    const orConditions = [
      // 1. Actively in play
      and(
        inArray(schema.fixtures.state, LIVE_STATES),
        gte(schema.fixtures.kickoffTime, livePastCutoff)
      ),
      // 2. Imminent kickoff (within 45 minutes)
      and(
        inArray(schema.fixtures.state, ['PREMATCH', 'NS', 'TBD', 'SCHEDULED', 'TIMED', 'NOT_STARTED']),
        gte(schema.fixtures.kickoffTime, new Date(nowMs - 10 * 60 * 1000)),
        lte(schema.fixtures.kickoffTime, approachingFutureCutoff)
      ),
      // 3. Recently finished (within last 15 minutes)
      and(
        inArray(schema.fixtures.state, FINISHED_STATES),
        gte(schema.fixtures.updatedAt, recentFinishCutoff)
      ),
    ];

    let whereClause: any = or(...orConditions);

    if (expandedTeamIds.size > 0 || expandedCompIds.size > 0) {
      const filterConditions: any[] = [];
      if (expandedTeamIds.size > 0) {
        const targetTeamList = Array.from(expandedTeamIds);
        filterConditions.push(
          or(
            inArray(schema.fixtures.homeTeamId, targetTeamList),
            inArray(schema.fixtures.awayTeamId, targetTeamList)
          )
        );
      }
      if (expandedCompIds.size > 0) {
        const targetCompList = Array.from(expandedCompIds);
        filterConditions.push(
          inArray(schema.fixtures.competitionId, targetCompList)
        );
      }
      whereClause = and(or(...orConditions), or(...filterConditions));
    }

    return await db.query.fixtures.findMany({
      where: whereClause,
      with: {
        competition: true,
        homeTeam: true,
        awayTeam: true,
        events: {
          orderBy: [desc(schema.events.minute), desc(schema.events.extraMinute)],
          limit: 5,
        },
      },
      orderBy: [
        sql`CASE
          WHEN ${schema.fixtures.state} IN ('LIVE', 'HALFTIME', '1H', '2H', 'FIRST_HALF', 'SECOND_HALF', 'EXTRA_TIME', 'EXTRA_TIME_HALFTIME', 'PENALTIES', 'ET', 'P', 'BT', 'IN_PLAY') THEN 0
          WHEN ${schema.fixtures.state} IN ('PREMATCH', 'NS', 'TBD', 'SCHEDULED') THEN 1
          ELSE 2
        END ASC`,
        asc(schema.fixtures.kickoffTime),
        asc(schema.fixtures.id),
      ],
      ...(typeof options?.limit === 'number' && options.limit > 0 ? { limit: options.limit } : {}),
    });
  }

  async insert(fixture: Omit<typeof schema.fixtures.$inferInsert, 'canonicalId'> & { canonicalId?: string }) {
    const canonicalId = fixture.canonicalId || generateCanonicalId('fix');
    const results = await db.insert(schema.fixtures).values({ ...fixture, canonicalId }).onConflictDoNothing().returning();
    return results[0] ?? null;
  }

  async updateState(id: string, state: string, elapsedMinute: number, homeScore: number, awayScore: number) {
    const results = await db.update(schema.fixtures)
      .set({
        state,
        elapsedMinute,
        homeScore,
        awayScore,
        updatedAt: new Date(),
      })
      .where(eq(schema.fixtures.id, id))
      .returning();
    return results[0] ?? null;
  }

  /**
   * Reconciles expired LIVE fixtures locally without external provider calls.
   * Any fixture remaining in an active live state beyond MAX_LIVE_MATCH_AGE_MS (3.5h)
   * has conclusively finished play and is transitioned to FINISHED.
   */
  async reconcileExpiredLiveFixtures(now: Date = new Date()): Promise<number> {
    const cutoff = new Date(now.getTime() - AcquisitionPolicyEngine.MAX_LIVE_MATCH_AGE_MS);
    const result = await db
      .update(schema.fixtures)
      .set({
        state: 'FINISHED',
        updatedAt: now,
      })
      .where(
        and(
          inArray(schema.fixtures.state, ['LIVE', 'HALFTIME', '1H', '2H', 'FIRST_HALF', 'SECOND_HALF', 'EXTRA_TIME', 'EXTRA_TIME_HALFTIME', 'PENALTIES', 'ET', 'P', 'BT', 'IN_PLAY']),
          lt(schema.fixtures.kickoffTime, cutoff)
        )
      );

    // Also sweep ancient PREMATCH fixtures past match day (> 24h past kickoff) locally to FINISHED
    const ancientPrematchCutoff = new Date(now.getTime() - 24 * 60 * 60 * 1000);
    await db
      .update(schema.fixtures)
      .set({
        state: 'FINISHED',
        updatedAt: now,
      })
      .where(
        and(
          inArray(schema.fixtures.state, ['PREMATCH', 'NS', 'TBD']),
          lt(schema.fixtures.kickoffTime, ancientPrematchCutoff)
        )
      );

    return (result as any).rowCount ?? (result as any).changes ?? 0;
  }

  // Tracks dates for which schedule discovery has been executed
  private discoveredDates = new Set<string>();
  private invalidatedDates = new Set<string>();

  /**
   * Marks schedule discovery as completed for a given UTC date string (YYYY-MM-DD).
   * Persists a durable discovery record in api_request_ledger (0 cost units, provider='system').
   */
  async markScheduleDiscovered(dateStr: string, now: Date = new Date(), cycleId?: string): Promise<void> {
    this.invalidatedDates.delete(dateStr);
    this.discoveredDates.add(dateStr);

    try {
      await db.insert(schema.apiRequestLedger).values({
        id: `disc_${dateStr}_${Date.now()}_${Math.random().toString(36).substring(2, 7)}`,
        cycleId: cycleId ?? null,
        provider: 'system',
        endpoint: `/schedule?date=${dateStr}`,
        operation: 'DISCOVER_SCHEDULE',
        priority: 'NORMAL',
        success: true,
        httpStatus: 200,
        responseTimeMs: 0,
        costUnits: 0,
        requestedAt: now,
      }).onConflictDoNothing();
    } catch {
      // Fallback gracefully if ledger write fails
    }
  }

  /**
   * Explicitly invalidates schedule discovery state for a date (or all dates).
   * Persists a durable invalidation marker (0 cost units, provider='system', operation='INVALIDATE_SCHEDULE').
   * Forces next scheduler cycle to perform a discovery cycle even across restarts,
   * while completely preserving immutable historical provider audit logs.
   */
  async invalidateSchedule(dateStr?: string, now: Date = new Date(), cycleId?: string): Promise<void> {
    const targetDate = dateStr ?? now.toISOString().slice(0, 10);
    this.discoveredDates.delete(targetDate);
    this.invalidatedDates.add(targetDate);

    try {
      await db.insert(schema.apiRequestLedger).values({
        id: `inval_${targetDate}_${Date.now()}_${Math.random().toString(36).substring(2, 7)}`,
        cycleId: cycleId ?? null,
        provider: 'system',
        endpoint: `/schedule?date=${targetDate}`,
        operation: 'INVALIDATE_SCHEDULE',
        priority: 'NORMAL',
        success: true,
        httpStatus: 200,
        responseTimeMs: 0,
        costUnits: 0,
        requestedAt: now,
        errorMessage: 'SCHEDULE_INVALIDATED',
      }).onConflictDoNothing();
    } catch {
      // Fallback gracefully if database insert fails
    }
  }

  /**
   * Returns true if schedule discovery has been executed for the given date and not invalidated.
   */
  isScheduleDiscovered(dateStr: string): boolean {
    if (this.invalidatedDates.has(dateStr)) return false;
    return this.discoveredDates.has(dateStr);
  }

  /**
   * Resolves the authoritative discovery state for a date by checking in-memory cache
   * and persistent ledger markers (cleanly ordering DISCOVER_SCHEDULE vs INVALIDATE_SCHEDULE).
   */
  async resolveScheduleState(dateStr: string, now: Date = new Date()): Promise<'DISCOVERED' | 'INVALIDATED' | 'UNKNOWN'> {
    if (this.invalidatedDates.has(dateStr)) return 'INVALIDATED';
    if (this.discoveredDates.has(dateStr)) return 'DISCOVERED';

    try {
      const startOfDay = new Date(dateStr + 'T00:00:00.000Z');
      const nextDay = new Date(startOfDay.getTime() + 24 * 60 * 60 * 1000);

      // Check for durable invalidation marker for dateStr
      const latestInvalidation = await db.query.apiRequestLedger.findFirst({
        where: and(
          eq(schema.apiRequestLedger.provider, 'system'),
          eq(schema.apiRequestLedger.operation, 'INVALIDATE_SCHEDULE'),
          gte(schema.apiRequestLedger.requestedAt, startOfDay),
          lt(schema.apiRequestLedger.requestedAt, nextDay)
        ),
        orderBy: [desc(schema.apiRequestLedger.requestedAt)],
      });

      // Check for latest successful discovery evidence for dateStr
      const latestDiscovery = await db.query.apiRequestLedger.findFirst({
        where: and(
          eq(schema.apiRequestLedger.success, true),
          gte(schema.apiRequestLedger.requestedAt, startOfDay),
          lt(schema.apiRequestLedger.requestedAt, nextDay),
          or(
            and(
              eq(schema.apiRequestLedger.provider, 'system'),
              eq(schema.apiRequestLedger.operation, 'DISCOVER_SCHEDULE')
            ),
            and(
              eq(schema.apiRequestLedger.provider, 'api_football'),
              eq(schema.apiRequestLedger.operation, 'get_fixtures'),
              like(schema.apiRequestLedger.endpoint, `%date=${dateStr}%`)
            )
          )
        ),
        orderBy: [desc(schema.apiRequestLedger.requestedAt)],
      });

      const invalidationTime = latestInvalidation
        ? (latestInvalidation.requestedAt instanceof Date
            ? latestInvalidation.requestedAt.getTime()
            : new Date(latestInvalidation.requestedAt).getTime())
        : null;

      const discoveryTime = latestDiscovery
        ? (latestDiscovery.requestedAt instanceof Date
            ? latestDiscovery.requestedAt.getTime()
            : new Date(latestDiscovery.requestedAt).getTime())
        : null;

      if (invalidationTime !== null && (discoveryTime === null || invalidationTime >= discoveryTime)) {
        this.invalidatedDates.add(dateStr);
        return 'INVALIDATED';
      }

      if (discoveryTime !== null && (invalidationTime === null || discoveryTime > invalidationTime)) {
        this.discoveredDates.add(dateStr);
        if (latestDiscovery?.operation !== 'DISCOVER_SCHEDULE') {
          await this.markScheduleDiscovered(dateStr, now);
        }
        return 'DISCOVERED';
      }
    } catch {
      // Fallback gracefully if database query fails
    }

    return 'UNKNOWN';
  }

  /**
   * Retrieves active summary counts (live, approaching, recently finished, stale prematch)
   * by evaluating candidate database fixtures through AcquisitionPolicyEngine.
   */
  async getActiveSummary(now: Date = new Date(), options?: ActiveSummaryOptions): Promise<FixturesSummary> {
    // Automatically sweep and clean up any ancient LIVE fixtures without external calls
    await this.reconcileExpiredLiveFixtures(now);

    const candidateFixtures = await db.query.fixtures.findMany({
      where: or(
        and(
          inArray(schema.fixtures.state, [
            'LIVE', 'HALFTIME', '1H', '2H', 'ET', 'P', 'BT', 'IN_PLAY',
            'FIRST_HALF', 'SECOND_HALF', 'EXTRA_TIME', 'EXTRA_TIME_HALFTIME', 'PENALTIES', 'HT'
          ]),
          gte(schema.fixtures.kickoffTime, new Date(now.getTime() - AcquisitionPolicyEngine.MAX_LIVE_MATCH_AGE_MS)),
          lte(schema.fixtures.kickoffTime, new Date(now.getTime() + AcquisitionPolicyEngine.APPROACHING_WINDOW_MS))
        ),
        and(
          inArray(schema.fixtures.state, ['PREMATCH', 'NS', 'TBD', 'TIMED', 'SCHEDULED', 'NOT_STARTED']),
          gte(schema.fixtures.kickoffTime, new Date(now.getTime() - 24 * 60 * 60 * 1000)),
          lte(schema.fixtures.kickoffTime, new Date(now.getTime() + 24 * 60 * 60 * 1000))
        ),
        and(
          inArray(schema.fixtures.state, ['FINISHED', 'FT', 'AET', 'PEN', 'POSTMATCH', 'FINISHED_AET', 'FINISHED_PEN']),
          isNull(schema.fixtures.finalizedAt),
          gte(schema.fixtures.kickoffTime, new Date(now.getTime() - 4 * 60 * 60 * 1000))
        )
      ),
      limit: 500,
    });

    const ACTIVE_PLAY_SET = new Set([
      'FIRST_HALF', 'SECOND_HALF', 'EXTRA_TIME', 'PENALTIES', 'LIVE', '1H', '2H', 'ET', 'P', 'IN_PLAY'
    ]);
    const HALFTIME_SET = new Set([
      'HALFTIME', 'HT', 'BT', 'EXTRA_TIME_HALFTIME'
    ]);
    const SCHEDULED_SET = new Set([
      'PREMATCH', 'NS', 'TBD', 'TIMED', 'SCHEDULED', 'NOT_STARTED'
    ]);

    const todayUtcPrefix = now.toISOString().slice(0, 10);
    let liveCount = 0;
    let activeInPlayCount = 0;
    let pinnedOrFollowedLiveCount = 0;
    let pinnedOrFollowedActiveInPlayCount = 0;
    let totalLiveHalftimeCount = 0;
    let pinnedOrFollowedHalftimeCount = 0;
    let minHalftimeRemainingMs: number | null = null;
    let minScheduledKickoffMs: number | null = null;
    let approachingCount = 0;
    let recentlyFinishedCount = 0;
    let stalePrematchCount = 0;
    const staleCompetitionIds: number[] = [];

    // Build followed/pinned lookups
    const followedTeams = new Set<string>();
    if (options?.followedTeamIds) {
      for (const id of options.followedTeamIds) {
        const s = String(id).trim();
        if (s) {
          followedTeams.add(s);
          if (/^\d+$/.test(s)) followedTeams.add(`team_${s}`);
          if (s.startsWith('team_')) followedTeams.add(s.slice(5));
        }
      }
    }

    const followedComps = new Set<string>();
    if (options?.followedCompetitionIds) {
      for (const id of options.followedCompetitionIds) {
        const s = String(id).trim();
        if (s) {
          followedComps.add(s);
          if (/^\d+$/.test(s)) followedComps.add(`comp_${s}`);
          if (s.startsWith('comp_')) followedComps.add(s.slice(5));
        }
      }
    }

    const pinnedMatches = new Set<string>();
    if (options?.pinnedMatchIds) {
      for (const id of options.pinnedMatchIds) {
        const s = String(id).trim();
        if (s) {
          pinnedMatches.add(s);
          if (/^\d+$/.test(s)) pinnedMatches.add(`fix_${s}`);
          if (s.startsWith('fix_')) pinnedMatches.add(s.slice(4));
        }
      }
    }

    // Authoritative schedule resolution (persists across restarts)
    const scheduleState = await this.resolveScheduleState(todayUtcPrefix, now);
    let scheduleKnownForToday = scheduleState === 'DISCOVERED';

    for (const f of candidateFixtures) {
      const stateUpper = (f.state || '').toUpperCase().trim();
      const kickoffDate = f.kickoffTime instanceof Date ? f.kickoffTime : new Date(f.kickoffTime);
      const kickoffStr = kickoffDate.toISOString();
      const kickoffMs = kickoffDate.getTime();

      // Check upcoming scheduled fixtures for kickoff wake-up guard
      if (SCHEDULED_SET.has(stateUpper) || kickoffMs >= now.getTime() - 60_000) {
        if (SCHEDULED_SET.has(stateUpper)) {
          if (minScheduledKickoffMs === null || kickoffMs < minScheduledKickoffMs) {
            minScheduledKickoffMs = kickoffMs;
          }
        }
      }

      // Only recognize schedule from existing DB fixtures if today's date was not explicitly invalidated
      if (kickoffStr.slice(0, 10) === todayUtcPrefix && scheduleState !== 'INVALIDATED') {
        scheduleKnownForToday = true;
      }

      const classification = AcquisitionPolicyEngine.classifyFixtureState({
        state: f.state,
        kickoffTime: f.kickoffTime,
        lastPolledAt: f.lastPolledAt,
        updatedAt: f.updatedAt,
        finalizedAt: f.finalizedAt,
      }, now);

      if (classification === 'LIVE_WINDOW') {
        liveCount++;

        const isPinned = pinnedMatches.size > 0 && (
          pinnedMatches.has(f.id) ||
          (f.canonicalId ? pinnedMatches.has(f.canonicalId) : false)
        );

        const isTeamFollowed = followedTeams.size > 0 && (
          (f.homeTeamId ? followedTeams.has(f.homeTeamId) : false) ||
          (f.awayTeamId ? followedTeams.has(f.awayTeamId) : false)
        );

        const isCompFollowed = followedComps.size > 0 && (
          f.competitionId ? followedComps.has(f.competitionId) : false
        );

        const isFollowedOrPinned = isPinned || isTeamFollowed || isCompFollowed;
        const isActivePlay = ACTIVE_PLAY_SET.has(stateUpper);
        const isHalftime = HALFTIME_SET.has(stateUpper);

        if (isActivePlay) {
          activeInPlayCount++;
        }

        if (isFollowedOrPinned) {
          pinnedOrFollowedLiveCount++;
          if (isActivePlay) {
            pinnedOrFollowedActiveInPlayCount++;
          }
          if (isHalftime) {
            pinnedOrFollowedHalftimeCount++;
          }
        }

        if (isHalftime) {
          totalLiveHalftimeCount++;
          const updateTime = f.updatedAt ? new Date(f.updatedAt).getTime() : (f.lastPolledAt ? new Date(f.lastPolledAt).getTime() : now.getTime());
          const elapsedSinceHt = Math.max(0, now.getTime() - updateTime);
          const remaining = Math.max(0, AcquisitionPolicyEngine.HALFTIME_SUSPEND_MS - elapsedSinceHt);
          if (minHalftimeRemainingMs === null || remaining > minHalftimeRemainingMs) {
            minHalftimeRemainingMs = remaining;
          }
        }
      } else if (classification === 'APPROACHING') {
        approachingCount++;
      } else if (classification === 'RECENTLY_FINISHED') {
        recentlyFinishedCount++;
      } else if (classification === 'STALE_PREMATCH') {
        stalePrematchCount++;
        if (f.competitionId) {
          const compNum = parseInt(extractProviderId(f.competitionId, 'comp'), 10);
          if (!isNaN(compNum)) {
            staleCompetitionIds.push(compNum);
          }
        }
      }
    }

    // If fixtures existed today but no ledger record existed yet, persist discovery marker
    if (scheduleKnownForToday && scheduleState === 'UNKNOWN') {
      await this.markScheduleDiscovered(todayUtcPrefix, now);
    }

    const hasPinnedOrFollowedLive = pinnedOrFollowedLiveCount > 0;
    // allLiveInHalftime / pinnedOrFollowedInHalftime is true ONLY when there is at least one halftime match AND ZERO active in-play matches
    const allLiveInHalftime = totalLiveHalftimeCount > 0 && activeInPlayCount === 0;
    const pinnedOrFollowedInHalftime = hasPinnedOrFollowedLive && pinnedOrFollowedHalftimeCount > 0 && pinnedOrFollowedActiveInPlayCount === 0;
    const halftimeRemainingMs = minHalftimeRemainingMs !== null && minHalftimeRemainingMs > 0
      ? minHalftimeRemainingMs
      : AcquisitionPolicyEngine.HALFTIME_SUSPEND_MS;

    return {
      liveCount,
      activeInPlayCount,
      pinnedOrFollowedActiveInPlayCount,
      hasPinnedOrFollowedLive: options ? hasPinnedOrFollowedLive : undefined,
      pinnedOrFollowedLiveCount,
      unpinnedLiveCount: liveCount - pinnedOrFollowedLiveCount,
      allLiveInHalftime,
      pinnedOrFollowedInHalftime,
      halftimeRemainingMs,
      approachingCount,
      recentlyFinishedCount,
      stalePrematchCount,
      staleCompetitionIds: Array.from(new Set(staleCompetitionIds)),
      scheduleKnownForToday,
      nextScheduledKickoffMs: minScheduledKickoffMs,
    };
  }
}

