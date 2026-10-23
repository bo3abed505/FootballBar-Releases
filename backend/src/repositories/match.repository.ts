import { eq, inArray, notInArray, sql, and, lt } from 'drizzle-orm';
import { db, schema } from '../db/index.js';
import {
  NormalizedMatch,
  MatchStatus,
} from '../providers/types.js';
import {
  CanonicalCompetition,
  CanonicalSeason,
  CanonicalTeam,
  CanonicalFixture,
} from '../normalization/fixture.js';
import { CanonicalEvent } from '../normalization/event.js';
import { NormalizedAcquisitionBatch } from '../acquisition/acquisition.service.js';
import { generateCanonicalId } from '../db/canonical-id.js';
import { DbExecutor } from '../db/provider-alias.js';
import { AcquisitionPolicyEngine } from '../acquisition/acquisition-policy.js';

export interface BatchPersistResult {
  provider: string;
  fixturesDiscovered: number;
  fixturesInserted: number;
  fixturesUpdated: number;
  eventsInserted: number;
  eventsSkippedDuplicates: number;
  hasChanges: boolean;
  discardedCount: number;
}

export interface PersistBatchOptions {
  liveOnly?: boolean;
  providerName?: string;
  executor?: DbExecutor;
  discardedCount?: number;
}

export interface MatchValidationResult {
  valid: boolean;
  error?: string;
}

export interface BatchValidationResult {
  valid: NormalizedMatch[];
  discarded: Array<{ match: unknown; reason: string }>;
}

export interface CanonicalBatchValidationResult {
  validBatch: NormalizedAcquisitionBatch;
  discardedCount: number;
}

export class MatchRepository {
  private readonly logger: {
    info: (msg: string, meta?: any) => void;
    warn: (msg: string, meta?: any) => void;
    error: (msg: string, meta?: any) => void;
  };

  constructor(logger?: {
    info: (msg: string, meta?: any) => void;
    warn: (msg: string, meta?: any) => void;
    error: (msg: string, meta?: any) => void;
  }) {
    this.logger = logger ?? {
      info: (msg, meta) => console.log(`[MATCH_REPO] ${msg}`, meta ? JSON.stringify(meta) : ''),
      warn: (msg, meta) => console.warn(`[MATCH_REPO:WARN] ${msg}`, meta ? JSON.stringify(meta) : ''),
      error: (msg, meta) => console.error(`[MATCH_REPO:ERROR] ${msg}`, meta ? JSON.stringify(meta) : ''),
    };
  }

  /**
   * Validates a single NormalizedMatch candidate before database ingestion.
   * Isolates partial failures: invalid/malformed matches are caught here before transactions open.
   */
  public validateNormalizedMatch(match: unknown): MatchValidationResult {
    if (!match || typeof match !== 'object') {
      return { valid: false, error: 'Match must be a non-null object' };
    }

    const m = match as Partial<NormalizedMatch>;

    // 1. Provider Match ID validation
    if (!m.providerMatchId || typeof m.providerMatchId !== 'string' || m.providerMatchId.trim().length === 0) {
      return { valid: false, error: 'Missing or empty providerMatchId' };
    }

    // 2. Home Team validation
    if (!m.homeTeam || typeof m.homeTeam !== 'object') {
      return { valid: false, error: 'Missing homeTeam object' };
    }
    if (!m.homeTeam.providerId || typeof m.homeTeam.providerId !== 'string' || m.homeTeam.providerId.trim().length === 0) {
      return { valid: false, error: 'Missing or empty homeTeam.providerId' };
    }
    if (!m.homeTeam.name || typeof m.homeTeam.name !== 'string' || m.homeTeam.name.trim().length === 0) {
      return { valid: false, error: 'Missing or empty homeTeam.name' };
    }

    // 3. Away Team validation
    if (!m.awayTeam || typeof m.awayTeam !== 'object') {
      return { valid: false, error: 'Missing awayTeam object' };
    }
    if (!m.awayTeam.providerId || typeof m.awayTeam.providerId !== 'string' || m.awayTeam.providerId.trim().length === 0) {
      return { valid: false, error: 'Missing or empty awayTeam.providerId' };
    }
    if (!m.awayTeam.name || typeof m.awayTeam.name !== 'string' || m.awayTeam.name.trim().length === 0) {
      return { valid: false, error: 'Missing or empty awayTeam.name' };
    }

    // 4. Time and Kickoff validation
    if (!m.time || typeof m.time !== 'object') {
      return { valid: false, error: 'Missing time object' };
    }
    if (!m.time.scheduledKickoff || typeof m.time.scheduledKickoff !== 'string') {
      return { valid: false, error: 'Missing or non-string time.scheduledKickoff' };
    }
    const kickoffTimestamp = Date.parse(m.time.scheduledKickoff);
    if (isNaN(kickoffTimestamp)) {
      return { valid: false, error: `Invalid date format for scheduledKickoff: ${m.time.scheduledKickoff}` };
    }

    // 5. Match Status validation
    if (!m.time.status || typeof m.time.status !== 'string') {
      return { valid: false, error: 'Missing or non-string time.status' };
    }

    // 6. Numeric bounds validation (minute, addedTime, scores)
    if (m.time.minute !== null && m.time.minute !== undefined) {
      if (typeof m.time.minute !== 'number' || isNaN(m.time.minute) || m.time.minute < 0) {
        return { valid: false, error: 'time.minute must be a non-negative number or null' };
      }
    }
    if (m.time.addedTime !== null && m.time.addedTime !== undefined) {
      if (typeof m.time.addedTime !== 'number' || isNaN(m.time.addedTime) || m.time.addedTime < 0) {
        return { valid: false, error: 'time.addedTime must be a non-negative number or null' };
      }
    }
    if (m.homeTeam.score !== null && m.homeTeam.score !== undefined) {
      if (typeof m.homeTeam.score !== 'number' || isNaN(m.homeTeam.score) || m.homeTeam.score < 0) {
        return { valid: false, error: 'homeTeam.score must be a non-negative number or null' };
      }
    }
    if (m.awayTeam.score !== null && m.awayTeam.score !== undefined) {
      if (typeof m.awayTeam.score !== 'number' || isNaN(m.awayTeam.score) || m.awayTeam.score < 0) {
        return { valid: false, error: 'awayTeam.score must be a non-negative number or null' };
      }
    }

    return { valid: true };
  }

  /**
   * Filters a list of matches, isolating malformed records from valid records.
   * Partial Failure Isolation: Bad matches are logged and discarded before opening DB transactions.
   */
  public validateMatchBatch(matches: unknown[]): BatchValidationResult {
    const valid: NormalizedMatch[] = [];
    const discarded: Array<{ match: unknown; reason: string }> = [];

    if (!Array.isArray(matches)) {
      this.logger.warn('validateMatchBatch received a non-array input');
      return { valid, discarded: [{ match: matches, reason: 'Input is not an array' }] };
    }

    for (const item of matches) {
      const res = this.validateNormalizedMatch(item);
      if (res.valid) {
        valid.push(item as NormalizedMatch);
      } else {
        const reason = res.error || 'Unknown validation failure';
        this.logger.warn(`[PARTIAL_FAILURE_ISOLATION] Discarding malformed match: ${reason}`, {
          matchId: (item as any)?.providerMatchId,
        });
        discarded.push({ match: item, reason });
      }
    }

    return { valid, discarded };
  }

  /**
   * Validates a CanonicalFixture candidate before database persistence.
   * Ensures that no malformed record aborts the atomic bulk upsert.
   */
  public validateCanonicalFixture(fixture: unknown): MatchValidationResult {
    if (!fixture || typeof fixture !== 'object') {
      return { valid: false, error: 'Fixture must be a non-null object' };
    }
    const f = fixture as Partial<CanonicalFixture>;

    if (!f.id || typeof f.id !== 'string' || f.id.trim().length === 0) {
      return { valid: false, error: 'Missing or empty fixture.id' };
    }
    if (!f.homeTeamId || typeof f.homeTeamId !== 'string' || f.homeTeamId.trim().length === 0) {
      return { valid: false, error: 'Missing or empty fixture.homeTeamId' };
    }
    if (!f.awayTeamId || typeof f.awayTeamId !== 'string' || f.awayTeamId.trim().length === 0) {
      return { valid: false, error: 'Missing or empty fixture.awayTeamId' };
    }
    if (!f.competitionId || typeof f.competitionId !== 'string' || f.competitionId.trim().length === 0) {
      return { valid: false, error: 'Missing or empty fixture.competitionId' };
    }
    if (!f.kickoffTime || !(f.kickoffTime instanceof Date) || isNaN(f.kickoffTime.getTime())) {
      return { valid: false, error: 'Invalid or missing fixture.kickoffTime Date' };
    }
    if (!f.state || typeof f.state !== 'string' || f.state.trim().length === 0) {
      return { valid: false, error: 'Missing or empty fixture.state' };
    }
    if (f.elapsedMinute !== null && f.elapsedMinute !== undefined) {
      if (typeof f.elapsedMinute !== 'number' || isNaN(f.elapsedMinute) || f.elapsedMinute < 0) {
        return { valid: false, error: 'fixture.elapsedMinute must be a non-negative number' };
      }
    }
    if (f.extraMinute !== null && f.extraMinute !== undefined) {
      if (typeof f.extraMinute !== 'number' || isNaN(f.extraMinute) || f.extraMinute < 0) {
        return { valid: false, error: 'fixture.extraMinute must be a non-negative number' };
      }
    }
    if (f.homeScore !== null && f.homeScore !== undefined) {
      if (typeof f.homeScore !== 'number' || isNaN(f.homeScore) || f.homeScore < 0) {
        return { valid: false, error: 'fixture.homeScore must be a non-negative number' };
      }
    }
    if (f.awayScore !== null && f.awayScore !== undefined) {
      if (typeof f.awayScore !== 'number' || isNaN(f.awayScore) || f.awayScore < 0) {
        return { valid: false, error: 'fixture.awayScore must be a non-negative number' };
      }
    }

    return { valid: true };
  }

  /**
   * Filters a NormalizedAcquisitionBatch, isolating malformed fixtures prior to transaction entry.
   * Pre-transaction screening ensures one corrupted match does not abort valid batch persistence.
   */
  public validateCanonicalBatch(batch: NormalizedAcquisitionBatch): CanonicalBatchValidationResult {
    const validFixtures: CanonicalFixture[] = [];
    let discardedCount = 0;

    for (const f of batch.fixtures) {
      const check = this.validateCanonicalFixture(f);
      if (check.valid) {
        validFixtures.push(f);
      } else {
        discardedCount++;
        this.logger.warn(`[PARTIAL_FAILURE_ISOLATION] Discarding malformed fixture: ${check.error}`, {
          fixtureId: (f as any)?.id,
        });
      }
    }

    return {
      validBatch: {
        ...batch,
        fixtures: validFixtures,
      },
      discardedCount,
    };
  }

  /**
   * Converts validated NormalizedMatch[] into a canonical acquisition batch.
   */
  public convertMatchesToBatch(matches: NormalizedMatch[], providerName: string): NormalizedAcquisitionBatch {
    const compMap = new Map<string, CanonicalCompetition>();
    const seasonMap = new Map<string, CanonicalSeason>();
    const teamMap = new Map<string, CanonicalTeam>();
    const fixtureList: CanonicalFixture[] = [];
    const eventList: CanonicalEvent[] = [];

    for (const raw of matches) {
      const compId = raw.competitionId || 'unknown_comp';
      if (!compMap.has(compId)) {
        compMap.set(compId, {
          id: compId,
          name: raw.competitionName || 'Unknown Competition',
          code: raw.competitionCode ?? null,
          type: 'league',
          country: raw.competitionCountry ?? null,
          logoUrl: raw.competitionLogoUrl ?? null,
          provider: providerName,
          providerId: compId,
        });
      }

      const seasonId = `season_${compId}_unknown`;
      if (!seasonMap.has(seasonId)) {
        seasonMap.set(seasonId, {
          id: seasonId,
          competitionId: compId,
          year: new Date().getFullYear(),
          displayYear: String(new Date().getFullYear()),
          startDate: new Date(new Date().getFullYear(), 0, 1),
          endDate: new Date(new Date().getFullYear(), 11, 31),
          isCurrent: true,
        });
      }

      if (raw.homeTeam?.providerId && !teamMap.has(raw.homeTeam.providerId)) {
        teamMap.set(raw.homeTeam.providerId, {
          id: raw.homeTeam.providerId,
          name: raw.homeTeam.name,
          shortName: raw.homeTeam.shortName || raw.homeTeam.name,
          code: raw.homeTeam.shortName ?? null,
          country: null,
          logoUrl: raw.homeTeam.logoUrl ?? null,
          venueName: null,
          provider: providerName,
          providerId: raw.homeTeam.providerId,
        });
      }

      if (raw.awayTeam?.providerId && !teamMap.has(raw.awayTeam.providerId)) {
        teamMap.set(raw.awayTeam.providerId, {
          id: raw.awayTeam.providerId,
          name: raw.awayTeam.name,
          shortName: raw.awayTeam.shortName || raw.awayTeam.name,
          code: raw.awayTeam.shortName ?? null,
          country: null,
          logoUrl: raw.awayTeam.logoUrl ?? null,
          venueName: null,
          provider: providerName,
          providerId: raw.awayTeam.providerId,
        });
      }

      const isFinished = ['FINISHED', 'FT', 'AET', 'PEN', 'POSTMATCH'].includes(raw.time.status);
      fixtureList.push({
        id: raw.providerMatchId,
        competitionId: compId,
        seasonId: seasonId,
        homeTeamId: raw.homeTeam.providerId,
        awayTeamId: raw.awayTeam.providerId,
        round: null,
        kickoffTime: new Date(raw.time.scheduledKickoff),
        state: raw.time.status,
        elapsedMinute: raw.time.minute ?? 0,
        extraMinute: raw.time.addedTime ?? 0,
        homeScore: raw.homeTeam.score ?? 0,
        awayScore: raw.awayTeam.score ?? 0,
        homeScoreHt: null,
        awayScoreHt: null,
        homeScoreEt: null,
        awayScoreEt: null,
        homeScorePen: null,
        awayScorePen: null,
        venue: null,
        referee: null,
        lastPolledAt: new Date(),
        finalizedAt: isFinished ? new Date() : null,
        provider: providerName,
        providerId: raw.providerMatchId,
      });
    }

    return {
      competitions: Array.from(compMap.values()),
      seasons: Array.from(seasonMap.values()),
      teams: Array.from(teamMap.values()),
      fixtures: fixtureList,
      events: eventList,
    };
  }

  /**
   * Native Bulk Upsert Batch Persistence.
   * Eliminates N+1 query loops by executing bulk insert().values().onConflictDoUpdate() per table.
   * Single SQLite transaction guarantees atomicity, snapshot isolation, and rollback safety.
   */
  public async persistBatch(
    batch: NormalizedAcquisitionBatch,
    options?: PersistBatchOptions
  ): Promise<BatchPersistResult> {
    const providerName = options?.providerName || 'unknown';
    const liveOnly = options?.liveOnly ?? false;

    // STEP 1: Pre-Transaction Validation (Partial Failure Isolation)
    // Filter malformed fixtures BEFORE touching the database or acquiring the write-lock.
    const { validBatch, discardedCount: preDiscarded } = this.validateCanonicalBatch(batch);
    const totalDiscarded = (options?.discardedCount ?? 0) + preDiscarded;

    // Deduplicate within the in-memory batch arrays to prevent SQLite "ON CONFLICT DO UPDATE command cannot affect row a second time"
    const compMap = new Map<string, CanonicalCompetition>();
    for (const c of validBatch.competitions) compMap.set(c.id, c);
    const dedupedComps = Array.from(compMap.values());

    const seasonMap = new Map<string, CanonicalSeason>();
    for (const s of validBatch.seasons) seasonMap.set(s.id, s);
    const dedupedSeasons = Array.from(seasonMap.values());

    const teamMap = new Map<string, CanonicalTeam>();
    for (const t of validBatch.teams) teamMap.set(t.id, t);
    const dedupedTeams = Array.from(teamMap.values());

    const fixtureMap = new Map<string, CanonicalFixture>();
    for (const f of validBatch.fixtures) fixtureMap.set(f.id, f);
    const dedupedFixtures = Array.from(fixtureMap.values());

    if (dedupedFixtures.length === 0 && dedupedComps.length === 0 && dedupedTeams.length === 0) {
      return {
        provider: providerName,
        fixturesDiscovered: batch.fixtures.length,
        fixturesInserted: 0,
        fixturesUpdated: 0,
        eventsInserted: 0,
        eventsSkippedDuplicates: 0,
        hasChanges: false,
        discardedCount: totalDiscarded,
      };
    }

    const executeInTransaction = async (tx: DbExecutor): Promise<BatchPersistResult> => {
      const now = new Date();

      // 1. Bulk Upsert Competitions (Native ON CONFLICT DO UPDATE)
      if (dedupedComps.length > 0) {
        const compIds = dedupedComps.map((c) => c.id);

        // Resolve aliases for incoming competitions
        const compAliases = await tx
          .select({
            providerId: schema.competitionProviderAliases.providerId,
            canonicalCompetitionId: schema.competitionProviderAliases.canonicalCompetitionId,
          })
          .from(schema.competitionProviderAliases)
          .where(
            and(
              eq(schema.competitionProviderAliases.provider, providerName),
              inArray(schema.competitionProviderAliases.providerId, compIds)
            )
          );
        const aliasCanonicalMap = new Map(compAliases.map((a) => [a.providerId, a.canonicalCompetitionId]));

        const canonList = Array.from(aliasCanonicalMap.values());
        const existingByCanon = canonList.length > 0
          ? await tx
              .select({ id: schema.competitions.id, canonicalId: schema.competitions.canonicalId })
              .from(schema.competitions)
              .where(inArray(schema.competitions.canonicalId, canonList))
          : [];
        const canonToCompMap = new Map(existingByCanon.map((r) => [r.canonicalId, r]));

        const compRemap = new Map<string, string>();
        for (const c of dedupedComps) {
          const provId = c.providerId || c.id;
          if (provId && aliasCanonicalMap.has(provId)) {
            const canon = aliasCanonicalMap.get(provId)!;
            const targetComp = canonToCompMap.get(canon);
            if (targetComp) {
              compRemap.set(c.id, targetComp.id);
              c.canonicalId = targetComp.canonicalId;
              c.id = targetComp.id;
            } else {
              c.canonicalId = canon;
            }
          }
        }

        if (compRemap.size > 0) {
          for (const s of dedupedSeasons) {
            if (compRemap.has(s.competitionId)) {
              s.competitionId = compRemap.get(s.competitionId)!;
            }
          }
          for (const f of dedupedFixtures) {
            if (compRemap.has(f.competitionId)) {
              f.competitionId = compRemap.get(f.competitionId)!;
            }
          }
        }

        const effectiveCompIds = dedupedComps.map((c) => c.id);
        const existingCompRows = await tx
          .select({ id: schema.competitions.id, canonicalId: schema.competitions.canonicalId })
          .from(schema.competitions)
          .where(inArray(schema.competitions.id, effectiveCompIds));
        const existingCompMap = new Map(existingCompRows.map((r) => [r.id, r.canonicalId]));

        const compRows = dedupedComps.map((c) => ({
          id: c.id,
          canonicalId: c.canonicalId || existingCompMap.get(c.id) || generateCanonicalId('comp'),
          name: c.name || 'Unknown Competition',
          code: c.code ?? null,
          type: c.type ?? 'league',
          country: c.country ?? null,
          logoUrl: c.logoUrl ?? null,
          createdAt: now,
          updatedAt: now,
        }));

        await tx
          .insert(schema.competitions)
          .values(compRows)
          .onConflictDoUpdate({
            target: schema.competitions.id,
            set: {
              name: sql`excluded.name`,
              code: sql`COALESCE(NULLIF(excluded.code, 'COMP'), ${schema.competitions.code})`,
              type: sql`excluded.type`,
              country: sql`COALESCE(NULLIF(excluded.country, 'COMP'), ${schema.competitions.country})`,
              logoUrl: sql`COALESCE(excluded.logo_url, ${schema.competitions.logoUrl})`,
              updatedAt: sql`excluded.updated_at`,
            },
          });

        // Bulk Competition Aliases (ON CONFLICT DO NOTHING)
        const compAliasRows = dedupedComps
          .filter((c) => c.providerId)
          .map((c) => ({
            canonicalCompetitionId: c.canonicalId || existingCompMap.get(c.id) || compRows.find((r) => r.id === c.id)!.canonicalId,
            provider: c.provider || providerName,
            providerId: c.providerId!,
            firstSeenAt: now,
            lastSeenAt: now,
          }));

        if (compAliasRows.length > 0) {
          await tx
            .insert(schema.competitionProviderAliases)
            .values(compAliasRows)
            .onConflictDoNothing();
        }
      }

      // 2. Bulk Upsert Seasons (Native ON CONFLICT DO UPDATE or reuse existing)
      const compIdsForSeasons = dedupedComps.map((c) => c.id);
      const existingSeasons = compIdsForSeasons.length > 0
        ? await tx
            .select({ id: schema.competitionSeasons.id, competitionId: schema.competitionSeasons.competitionId, year: schema.competitionSeasons.year })
            .from(schema.competitionSeasons)
            .where(inArray(schema.competitionSeasons.competitionId, compIdsForSeasons))
        : [];

      const existingSeasonCompYearMap = new Map(existingSeasons.map((s) => [`${s.competitionId}_${s.year}`, s.id]));
      const existingSeasonCompMap = new Map(existingSeasons.map((s) => [s.competitionId, s.id]));

      const seasonsToInsert = dedupedSeasons.filter((s) => !existingSeasonCompYearMap.has(`${s.competitionId}_${s.year}`));

      if (seasonsToInsert.length > 0) {
        const seasonRows = seasonsToInsert.map((s) => ({
          id: s.id,
          competitionId: s.competitionId,
          year: s.year,
          displayYear: s.displayYear,
          startDate: s.startDate,
          endDate: s.endDate,
          isCurrent: s.isCurrent ?? true,
          createdAt: now,
        }));

        await tx
          .insert(schema.competitionSeasons)
          .values(seasonRows)
          .onConflictDoUpdate({
            target: schema.competitionSeasons.id,
            set: {
              isCurrent: sql`excluded.is_current`,
            },
          });
      }

      // 3. Bulk Upsert Teams (Native ON CONFLICT DO UPDATE)
      if (dedupedTeams.length > 0) {
        const teamIds = dedupedTeams.map((t) => t.id);
        const existingTeamRows = await tx
          .select({ id: schema.teams.id, canonicalId: schema.teams.canonicalId })
          .from(schema.teams)
          .where(inArray(schema.teams.id, teamIds));
        const existingTeamMap = new Map(existingTeamRows.map((r) => [r.id, r.canonicalId]));

        const teamRows = dedupedTeams.map((t) => ({
          id: t.id,
          canonicalId: t.canonicalId || existingTeamMap.get(t.id) || generateCanonicalId('team'),
          name: t.name || 'Unknown Team',
          shortName: t.shortName || t.name || 'TEAM',
          code: t.code ?? null,
          country: t.country ?? null,
          logoUrl: t.logoUrl ?? null,
          venueName: t.venueName ?? null,
          createdAt: now,
          updatedAt: now,
        }));

        await tx
          .insert(schema.teams)
          .values(teamRows)
          .onConflictDoUpdate({
            target: schema.teams.id,
            set: {
              name: sql`excluded.name`,
              shortName: sql`excluded.short_name`,
              code: sql`excluded.code`,
              country: sql`excluded.country`,
              logoUrl: sql`excluded.logo_url`,
              venueName: sql`excluded.venue_name`,
              updatedAt: sql`excluded.updated_at`,
            },
          });

        // Bulk Team Aliases (ON CONFLICT DO NOTHING)
        const teamAliasRows = dedupedTeams
          .filter((t) => t.providerId)
          .map((t) => ({
            canonicalTeamId: t.canonicalId || existingTeamMap.get(t.id) || teamRows.find((r) => r.id === t.id)!.canonicalId,
            provider: t.provider || providerName,
            providerId: t.providerId!,
            firstSeenAt: now,
            lastSeenAt: now,
          }));

        if (teamAliasRows.length > 0) {
          await tx
            .insert(schema.teamProviderAliases)
            .values(teamAliasRows)
            .onConflictDoNothing();
        }
      }

      // 4. Determine inserted vs updated counts for fixtures via single batch check
      let fixturesInserted = 0;
      let fixturesUpdated = 0;

      if (dedupedFixtures.length > 0) {
        const fixtureIds = dedupedFixtures.map((f) => f.id);
        const existingFixtureRows = await tx
          .select({
            id: schema.fixtures.id,
            canonicalId: schema.fixtures.canonicalId,
            state: schema.fixtures.state,
            elapsedMinute: schema.fixtures.elapsedMinute,
            extraMinute: schema.fixtures.extraMinute,
            homeScore: schema.fixtures.homeScore,
            awayScore: schema.fixtures.awayScore,
          })
          .from(schema.fixtures)
          .where(inArray(schema.fixtures.id, fixtureIds));

        const existingFixtureMap = new Map(existingFixtureRows.map((r) => [r.id, r]));
        fixturesInserted = dedupedFixtures.filter((f) => !existingFixtureMap.has(f.id)).length;
        fixturesUpdated = dedupedFixtures.filter((f) => {
          const existing = existingFixtureMap.get(f.id);
          if (!existing) return false;
          return (
            existing.state !== f.state ||
            existing.elapsedMinute !== (f.elapsedMinute ?? 0) ||
            existing.extraMinute !== (f.extraMinute ?? 0) ||
            existing.homeScore !== (f.homeScore ?? 0) ||
            existing.awayScore !== (f.awayScore ?? 0)
          );
        }).length;

        // Bulk Upsert Fixtures (Native ON CONFLICT DO UPDATE, canonicalId omitted from set to preserve identity)
        const fixtureRows = dedupedFixtures.map((f) => {
          const resolvedSeasonId = f.seasonId && !f.seasonId.endsWith('_unknown')
            ? f.seasonId
            : (existingSeasonCompMap.get(f.competitionId) || f.seasonId);

          const isFinished = !!f.state && ['FINISHED', 'FT', 'AET', 'PEN', 'POSTMATCH'].includes(f.state);

          return {
            id: f.id,
            canonicalId: f.canonicalId || existingFixtureMap.get(f.id)?.canonicalId || generateCanonicalId('fix'),
            competitionId: f.competitionId,
            seasonId: resolvedSeasonId,
            homeTeamId: f.homeTeamId,
            awayTeamId: f.awayTeamId,
            round: f.round ?? null,
            kickoffTime: f.kickoffTime,
            state: f.state,
            elapsedMinute: f.elapsedMinute ?? 0,
            extraMinute: f.extraMinute ?? 0,
            homeScore: f.homeScore ?? 0,
            awayScore: f.awayScore ?? 0,
            homeScoreHt: f.homeScoreHt ?? null,
            awayScoreHt: f.awayScoreHt ?? null,
            homeScoreEt: f.homeScoreEt ?? null,
            awayScoreEt: f.awayScoreEt ?? null,
            homeScorePen: f.homeScorePen ?? null,
            awayScorePen: f.awayScorePen ?? null,
            venue: f.venue ?? null,
            referee: f.referee ?? null,
            lastPolledAt: now,
            finalizedAt: f.finalizedAt ?? (isFinished ? now : null),
            ...(liveOnly
              ? { liveSourceProvider: f.provider || providerName, liveObservedAt: now }
              : { scheduleSourceProvider: f.provider || providerName, scheduleObservedAt: now }),
            createdAt: now,
            updatedAt: now,
          };
        });

        const insertQuery = tx
          .insert(schema.fixtures)
          .values(fixtureRows)
          .onConflictDoUpdate({
            target: schema.fixtures.id,
            set: {
              state: sql`excluded.state`,
              elapsedMinute: sql`excluded.elapsed_minute`,
              extraMinute: sql`excluded.extra_minute`,
              homeScore: sql`excluded.home_score`,
              awayScore: sql`excluded.away_score`,
              homeScoreHt: sql`COALESCE(excluded.home_score_ht, ${schema.fixtures.homeScoreHt})`,
              awayScoreHt: sql`COALESCE(excluded.away_score_ht, ${schema.fixtures.awayScoreHt})`,
              homeScoreEt: sql`COALESCE(excluded.home_score_et, ${schema.fixtures.homeScoreEt})`,
              awayScoreEt: sql`COALESCE(excluded.away_score_et, ${schema.fixtures.awayScoreEt})`,
              homeScorePen: sql`COALESCE(excluded.home_score_pen, ${schema.fixtures.homeScorePen})`,
              awayScorePen: sql`COALESCE(excluded.away_score_pen, ${schema.fixtures.awayScorePen})`,
              venue: sql`COALESCE(excluded.venue, ${schema.fixtures.venue})`,
              referee: sql`COALESCE(excluded.referee, ${schema.fixtures.referee})`,
              round: sql`COALESCE(excluded.round, ${schema.fixtures.round})`,
              kickoffTime: sql`COALESCE(excluded.kickoff_time, ${schema.fixtures.kickoffTime})`,
              lastPolledAt: sql`excluded.last_polled_at`,
              finalizedAt: sql`COALESCE(excluded.finalized_at, ${schema.fixtures.finalizedAt})`,
              ...(liveOnly
                ? {
                    liveSourceProvider: sql`excluded.live_source_provider`,
                    liveObservedAt: sql`excluded.live_observed_at`,
                  }
                : {
                    scheduleSourceProvider: sql`excluded.schedule_source_provider`,
                    scheduleObservedAt: sql`excluded.schedule_observed_at`,
                  }),
              updatedAt: sql`excluded.updated_at`,
            },
          });
        await insertQuery;
 
         // Bulk Fixture Aliases (ON CONFLICT DO NOTHING)
         const fixAliasRows = dedupedFixtures
           .filter((f) => f.providerId)
           .map((f) => ({
             canonicalFixtureId: f.canonicalId || existingFixtureMap.get(f.id)?.canonicalId || fixtureRows.find((r) => r.id === f.id)!.canonicalId,
             provider: f.provider || providerName,
             providerId: f.providerId!,
             firstSeenAt: now,
             lastSeenAt: now,
           }));
 
         if (fixAliasRows.length > 0) {
           await tx
             .insert(schema.fixtureProviderAliases)
             .values(fixAliasRows)
             .onConflictDoNothing();
         }
       }

      // 5. Bulk Events Insert with Deduplication (Chunked to respect SQLite variable limits & memory ceiling)
      let eventsInserted = 0;
      let eventsSkipped = 0;

      if (validBatch.events && validBatch.events.length > 0) {
        const EVENT_CHUNK_SIZE = 500;
        const allEventUids = validBatch.events.map((e) => e.eventUid);

        // Chunk lookup queries to stay well within SQLITE_MAX_VARIABLE_NUMBER
        const existingEventUidSet = new Set<string>();
        for (let i = 0; i < allEventUids.length; i += EVENT_CHUNK_SIZE) {
          const chunkUids = allEventUids.slice(i, i + EVENT_CHUNK_SIZE);
          const existingEventRows = await tx
            .select({ eventUid: schema.events.eventUid })
            .from(schema.events)
            .where(inArray(schema.events.eventUid, chunkUids));

          for (const row of existingEventRows) {
            existingEventUidSet.add(row.eventUid);
          }
        }

        const seenEventUids = new Set<string>();
        const newEvents = validBatch.events.filter((e) => {
          if (existingEventUidSet.has(e.eventUid) || seenEventUids.has(e.eventUid)) {
            return false;
          }
          seenEventUids.add(e.eventUid);
          return true;
        });

        eventsSkipped = validBatch.events.length - newEvents.length;

        // Chunk insert operations to prevent massive single-payload allocations
        for (let i = 0; i < newEvents.length; i += EVENT_CHUNK_SIZE) {
          const chunk = newEvents.slice(i, i + EVENT_CHUNK_SIZE);
          await tx.insert(schema.events).values(chunk).onConflictDoNothing();
          eventsInserted += chunk.length;
        }
      }

      // 6. Vanished-Live Fixture Reconciliation (Live only)
      if (liveOnly) {
        const cutoff = new Date(now.getTime() - AcquisitionPolicyEngine.MAX_LIVE_MATCH_AGE_MS);
        const incomingIds = dedupedFixtures.map((f) => f.id);
        const conditions: any[] = [
          inArray(schema.fixtures.state, ['LIVE', 'HALFTIME', '1H', '2H', 'ET', 'P', 'BT', 'IN_PLAY']),
          lt(schema.fixtures.kickoffTime, cutoff),
        ];
        if (incomingIds.length > 0) {
          conditions.push(notInArray(schema.fixtures.id, incomingIds));
        }
        await tx
          .update(schema.fixtures)
          .set({
            state: 'FINISHED',
            updatedAt: now,
          })
          .where(and(...conditions));
      }

      return {
        provider: providerName,
        fixturesDiscovered: batch.fixtures.length,
        fixturesInserted,
        fixturesUpdated,
        eventsInserted,
        eventsSkippedDuplicates: eventsSkipped,
        hasChanges: fixturesInserted > 0 || fixturesUpdated > 0 || eventsInserted > 0,
        discardedCount: totalDiscarded,
      };
    };

    // If caller provided an active transaction executor, participate in it.
    // Otherwise, open a single atomic transaction boundary across all bulk operations.
    if (options?.executor) {
      return await executeInTransaction(options.executor);
    }

    return await db.transaction(async (tx) => {
      return await executeInTransaction(tx);
    });
  }

  /**
   * High-level entry point for persisting an array of NormalizedMatch objects directly.
   * Runs Partial Failure Isolation, canonical transformation, and single-transaction persistence.
   */
  public async ingestNormalizedMatches(
    rawMatches: unknown[],
    options: { providerName: string; liveOnly?: boolean }
  ): Promise<BatchPersistResult> {
    // 1. Partial Failure Isolation: screen incoming array
    const { valid, discarded } = this.validateMatchBatch(rawMatches);

    if (valid.length === 0) {
      this.logger.warn('No valid matches found to persist in batch');
      return {
        provider: options.providerName,
        fixturesDiscovered: 0,
        fixturesInserted: 0,
        fixturesUpdated: 0,
        eventsInserted: 0,
        eventsSkippedDuplicates: 0,
        hasChanges: false,
        discardedCount: discarded.length,
      };
    }

    // 2. Map valid matches to canonical acquisition batch
    const batch = this.convertMatchesToBatch(valid, options.providerName);

    // 3. Persist batch with native bulk upserts in a single SQLite transaction
    return await this.persistBatch(batch, {
      liveOnly: options.liveOnly,
      providerName: options.providerName,
      discardedCount: discarded.length,
    });
  }
}
