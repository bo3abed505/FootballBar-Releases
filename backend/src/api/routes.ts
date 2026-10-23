import { FastifyInstance, FastifyPluginAsync } from 'fastify';
import { createHash } from 'crypto';
import { eq, or } from 'drizzle-orm';
import { FixturesRepository } from '../repositories/fixtures.repository.js';
import { checkDatabaseConnection, db, schema } from '../db/index.js';
import { createResponseEnvelope, createErrorEnvelope } from './envelope.js';
import { RequestGateway } from '../gateway/request-gateway.js';
import { formatV2TrayMatch, formatV2MatchDetail, formatV2CompetitionHub } from './v2-dto.js';
import { CatalogRepository } from '../repositories/catalog.repository.js';
import { createProvider } from '../acquisition/provider-factory.js';
import { SyncService } from '../acquisition/sync.service.js';
import { FotmobProvider } from '../providers/fotmob/fotmob.provider.js';

export const apiRoutes: FastifyPluginAsync = async (fastify: FastifyInstance) => {
  const fixturesRepo = new FixturesRepository();
  const catalogRepo = new CatalogRepository();
  const inFlightScheduleSyncs = new Map<string, Promise<any>>();
  const failedScheduleSyncCooldowns = new Map<string, number>();

  // ----------------------------------------------------
  // Health check endpoint (backward-compatible & enhanced)
  // ----------------------------------------------------
  fastify.get('/health', async (request, reply) => {
    const dbStatus = await checkDatabaseConnection();
    const scheduler: any = (fastify as any).scheduler;
    const schedulerDiag = scheduler ? scheduler.getDiagnostics() : null;
    const gateway = new RequestGateway();
    const effectiveProvider = process.env.ACTIVE_PROVIDER || process.env.FOOTBALL_PROVIDER || 'mock';
    const quotaDiag = await gateway.getDiagnostics(effectiveProvider);

    return reply.status(200).send({
      status: 'ok',
      database: dbStatus.connected ? 'connected' : 'disconnected',
      latencyMs: dbStatus.latencyMs,
      timestamp: new Date().toISOString(),
      version: '1.0.0',
      provider: 'live-telemetry',
      quotaMode: quotaDiag.mode,
      quotaRemaining: quotaDiag.remainingDaily,
      scheduler: schedulerDiag
        ? {
            running: schedulerDiag.running,
            lastCycleAction: schedulerDiag.lastCycleAction,
            lastCycleOutcome: schedulerDiag.lastCycleOutcome,
          }
        : {
            running: false,
            lastCycleAction: null,
            lastCycleOutcome: null,
          },
      uptimeSeconds: Math.round(process.uptime()),
    });
  });

  // ----------------------------------------------------
  // Diagnostics Endpoint (safe, sanitized, internal observability)
  // ----------------------------------------------------
  fastify.get('/api/diagnostics', async (request, reply) => {
    const dbStatus = await checkDatabaseConnection();
    const gateway = new RequestGateway();
    const effectiveProvider = process.env.ACTIVE_PROVIDER || process.env.FOOTBALL_PROVIDER || 'mock';
    const quotaDiag = await gateway.getDiagnostics(effectiveProvider);
    const scheduler: any = (fastify as any).scheduler;
    const schedulerDiag = scheduler ? scheduler.getDiagnostics() : null;

    return reply.status(200).send({
      service: {
        name: 'footballtray-backend',
        version: '1.0.0',
        nodeVersion: process.version,
        uptimeSeconds: Math.round(process.uptime()),
        memoryUsage: process.memoryUsage(),
      },
      provider: {
        mode: 'live-telemetry',
        allowLiveRequests: process.env.ALLOW_LIVE_PROVIDER_REQUESTS === 'true',
      },
      database: {
        connected: dbStatus.connected,
        latencyMs: dbStatus.latencyMs,
      },
      quota: quotaDiag,
      budget: quotaDiag, // backward compatibility alias
      scheduler: schedulerDiag ?? {
        running: false,
        executingCycle: false,
        currentState: 'STOPPED',
        lastCycleAt: null,
        lastCycleAction: null,
        lastCycleDurationMs: null,
        lastCycleOutcome: null,
        consecutiveErrors: 0,
        nextRecommendedWakeAt: null,
        nextPlannedAction: null,
        lastCycle: null,
        milestones: {
          lastSuccessfulDiscovery: { cycleId: null, timestamp: null, date: null },
          lastScheduleInvalidation: null,
          lastLivePoll: null,
          lastStaleRecovery: null,
        },
      },
      timestamp: new Date().toISOString(),
    });
  });

  // ----------------------------------------------------
  // Tray Matches Endpoint (supports ETag / 304)
  // ----------------------------------------------------
  fastify.get<{ Querystring: { date?: string; teams?: string; competitions?: string } }>('/api/tray', async (request, reply) => {
    try {
      const dateParam = request.query.date;
      const teamsParam = request.query.teams;
      const competitionsParam = request.query.competitions;
      const teams = teamsParam
        ? teamsParam.split(',').map((t) => t.trim()).filter((t) => t.length > 0)
        : undefined;
      const competitions = competitionsParam
        ? competitionsParam.split(',').map((c) => c.trim()).filter((c) => c.length > 0)
        : undefined;

      const matches = await fixturesRepo.getTrayMatches({
        date: dateParam,
        teams,
        competitions,
      });

      const formattedMatches = matches.map((m) => ({
        id: m.id,
        competition: {
          id: m.competition?.id,
          name: m.competition?.name,
          code: m.competition?.code !== 'COMP' ? (m.competition?.code ?? m.competition?.country) : (m.competition?.country !== 'COMP' ? m.competition?.country : undefined),
          country: m.competition?.country !== 'COMP' ? m.competition?.country : undefined,
          logoUrl: m.competition?.logoUrl,
        },
        homeTeam: {
          id: m.homeTeam?.id,
          name: m.homeTeam?.name,
          shortName: m.homeTeam?.shortName,
          logoUrl: m.homeTeam?.logoUrl,
        },
        awayTeam: {
          id: m.awayTeam?.id,
          name: m.awayTeam?.name,
          shortName: m.awayTeam?.shortName,
          logoUrl: m.awayTeam?.logoUrl,
        },
        state: m.state,
        elapsedMinute: m.elapsedMinute,
        extraMinute: m.extraMinute,
        score: {
          home: m.homeScore,
          away: m.awayScore,
          display: `${m.homeScore} - ${m.awayScore}`,
        },
        kickoffTime: m.kickoffTime,
        recentEvents: m.events.map((e) => ({
          id: e.id,
          type: e.type,
          minute: e.minute,
          extraMinute: e.extraMinute,
          teamId: e.teamId,
          playerName: e.playerName,
          description: e.description,
        })),
      }));

      // Compute deterministic ETag based on match data
      const dataHash = createHash('md5').update(JSON.stringify(formattedMatches)).digest('hex');
      const etag = `"${dataHash}"`;

      const ifNoneMatch = request.headers['if-none-match'];
      if (ifNoneMatch && (ifNoneMatch === etag || ifNoneMatch === dataHash || ifNoneMatch === `W/${etag}`)) {
        reply.header('ETag', etag);
        return reply.status(304).send();
      }

      const envelope = createResponseEnvelope({
        matches: formattedMatches,
      });

      reply.header('ETag', etag);
      return reply.send(envelope);
    } catch (err: any) {
      request.log.error(err, 'Failed to fetch tray matches');
      return reply.status(500).send(createErrorEnvelope('INTERNAL_ERROR', 'Failed to retrieve tray matches', { details: err?.message }));
    }
  });

  // ----------------------------------------------------
  // Match Details Endpoint
  // ----------------------------------------------------
  fastify.get<{ Params: { id: string } }>('/api/match/:id', async (request, reply) => {
    const { id } = request.params;

    if (!id || typeof id !== 'string' || id.trim().length === 0) {
      return reply.status(400).send(createErrorEnvelope('INVALID_REQUEST', 'Match ID is required and must not be empty.'));
    }

    try {
      const fixture = await fixturesRepo.getById(id.trim());

      if (!fixture) {
        return reply.status(404).send(createErrorEnvelope('MATCH_NOT_FOUND', `No match found with ID '${id}'.`));
      }

      const envelope = createResponseEnvelope({
        id: fixture.id,
        competition: fixture.competition,
        season: fixture.season,
        homeTeam: fixture.homeTeam,
        awayTeam: fixture.awayTeam,
        state: fixture.state,
        elapsedMinute: fixture.elapsedMinute,
        extraMinute: fixture.extraMinute,
        score: {
          home: fixture.homeScore,
          away: fixture.awayScore,
          halfTime: {
            home: fixture.homeScoreHt,
            away: fixture.awayScoreHt,
          },
          display: `${fixture.homeScore} - ${fixture.awayScore}`,
        },
        venue: fixture.venue,
        referee: fixture.referee,
        events: fixture.events,
        lineups: (fixture.lineups || []).map((l: any) => {
          const players = l.players || [];
          const starters = l.startingXI || l.startXi || l.starters || players.filter((p: any) => p.isStarter);
          const substitutes = l.substitutes || l.subs || l.bench || players.filter((p: any) => !p.isStarter);
          const unavailable = l.unavailable || l.naPlayers || l.injured || [];
          return {
            ...l,
            startingXI: starters,
            startXi: starters,
            starters,
            substitutes,
            subs: substitutes,
            unavailable,
            players: players.length > 0 ? players : [
              ...starters.map((p: any) => ({ ...p, isStarter: true })),
              ...substitutes.map((p: any) => ({ ...p, isStarter: false })),
            ],
          };
        }),
        statistics: fixture.statistics,
      });

      return reply.send(envelope);
    } catch (err: any) {
      request.log.error(err, `Failed to fetch match '${id}'`);
      return reply.status(500).send(createErrorEnvelope('INTERNAL_ERROR', 'Failed to retrieve match details', { details: err?.message }));
    }
  });

  // ----------------------------------------------------
  // Delivery API v2: Tray Matches (Canonical & Per-Fixture Freshness)
  // ----------------------------------------------------
  fastify.get<{ Querystring: { date?: string; teams?: string; competitions?: string } }>('/api/v2/tray', async (request, reply) => {
    try {
      const dateParam = request.query.date;
      const teamsParam = request.query.teams;
      const competitionsParam = request.query.competitions;
      const teams = teamsParam
        ? teamsParam.split(',').map((t) => t.trim()).filter((t) => t.length > 0)
        : undefined;
      const competitions = competitionsParam
        ? competitionsParam.split(',').map((c) => c.trim()).filter((c) => c.length > 0)
        : undefined;

      let isDateSyncing = false;
      // If a specific date within [-2..+6] relative to UTC is requested that has 0 fixtures in SQLite:
      // Trigger deduplicated in-flight schedule sync and await with bounded 3500ms race timeout
      if (dateParam && /^\d{4}-\d{2}-\d{2}$/.test(dateParam.trim())) {
        const normalizedDate = dateParam.trim();
        const [y, m, d] = normalizedDate.split('-').map(Number);
        const reqUtc = Date.UTC(y, m - 1, d);
        const now = new Date();
        const todayUtc = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
        const diffDays = Math.round((reqUtc - todayUtc) / (24 * 60 * 60 * 1000));

        if (diffDays >= -2 && diffDays <= 6) {
          const hasFixtures = await fixturesRepo.hasFixturesForDate(normalizedDate);
          if (!hasFixtures) {
            const failedAt = failedScheduleSyncCooldowns.get(normalizedDate);
            const isCoolingDown = failedAt ? Date.now() - failedAt < 15000 : false;

            if (!isCoolingDown) {
              let syncPromise = inFlightScheduleSyncs.get(normalizedDate);
              if (!syncPromise) {
                let syncService: SyncService | undefined =
                  (request.server as any).syncService || (fastify as any).syncService;
                if (!syncService) {
                  const effectiveProvider =
                    process.env.ACTIVE_PROVIDER || process.env.FOOTBALL_PROVIDER || 'fotmob';
                  const provider = createProvider({ provider: effectiveProvider as any });
                  syncService = new SyncService(provider);
                  (fastify as any).syncService = syncService;
                }

                if (syncService && typeof syncService.syncSchedule === 'function') {
                  syncPromise = syncService
                    .syncSchedule(normalizedDate)
                    .then((summary) => {
                      if (
                        !summary ||
                        ((summary.fixturesDiscovered ?? 0) === 0 &&
                          (summary.fixturesInserted ?? 0) === 0 &&
                          (summary.fixturesUpdated ?? 0) === 0)
                      ) {
                        failedScheduleSyncCooldowns.set(normalizedDate, Date.now());
                      } else {
                        failedScheduleSyncCooldowns.delete(normalizedDate);
                      }
                      return summary;
                    })
                    .catch((err: any) => {
                      request.log.warn(err, `On-demand schedule sync failed for ${normalizedDate}`);
                      failedScheduleSyncCooldowns.set(normalizedDate, Date.now());
                    })
                    .finally(() => {
                      inFlightScheduleSyncs.delete(normalizedDate);
                    });
                  inFlightScheduleSyncs.set(normalizedDate, syncPromise);
                }
              }

              if (syncPromise) {
                const timeoutPromise = new Promise<{ timedOut: true }>((resolve) =>
                  setTimeout(() => resolve({ timedOut: true }), 3500)
                );
                await Promise.race([syncPromise, timeoutPromise]);
                if (inFlightScheduleSyncs.has(normalizedDate)) {
                  isDateSyncing = true;
                }
              }
            }
          }
        }
      }

      // Query SQLite after bounded wait so newly discovered fixtures are immediately returned
      const matches = await fixturesRepo.getTrayMatches({
        date: dateParam,
        teams,
        competitions,
      });

      // If matches are 0 while an in-flight sync is active for this date, mark isDateSyncing
      if (dateParam && matches.length === 0 && inFlightScheduleSyncs.has(dateParam.trim())) {
        isDateSyncing = true;
      }

      // Compute followed match counts across the [-2..+6] UTC calendar window
      const now = new Date();
      const windowStart = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() - 2, 0, 0, 0, 0));
      const windowEnd = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + 6, 23, 59, 59, 999));

      const followedCountsByDate = await fixturesRepo.getFollowedCountsByDate({
        teams,
        competitions,
        fromDate: windowStart,
        toDate: windowEnd,
      });

      const followedTeamCompetitions = await fixturesRepo.getFollowedTeamCompetitions(teams);

      // Ensure pinned match is always present in response regardless of calendar date
      const prefService = (fastify as any).preferencesService;
      const pinnedMatchIds: string[] = [];
      const queryPinnedId = (request.query as any).pinnedMatchId;
      if (queryPinnedId && typeof queryPinnedId === 'string' && queryPinnedId.trim().length > 0) {
        const pid = queryPinnedId.trim();
        pinnedMatchIds.push(pid);
        if (prefService && typeof prefService.updatePreferences === 'function') {
          try { prefService.updatePreferences({ pinnedMatchId: pid }); } catch {}
        }
        const scheduler: any = (fastify as any).scheduler;
        if (scheduler && typeof scheduler.setPinnedMatchIds === 'function') {
          try { scheduler.setPinnedMatchIds([pid]); } catch {}
        }
      }
      if (prefService) {
        const prefPinned = prefService.getPreferences()?.pinnedMatchIds;
        if (Array.isArray(prefPinned)) {
          pinnedMatchIds.push(...prefPinned.map(String).filter((id: string) => id.trim().length > 0));
        }
      }

      const LIVE_STATES = ['LIVE', '1H', '2H', 'HT', 'ET', 'P', 'BT', 'FIRST_HALF', 'SECOND_HALF', 'HALFTIME', 'EXTRA_TIME', 'PENALTIES', 'IN_PLAY'];
      for (const pinnedId of pinnedMatchIds) {
        let pinnedRow: any = matches.find(
          (m: any) => m.id === pinnedId || m.canonicalId === pinnedId
        );
        if (!pinnedRow) {
          pinnedRow = await fixturesRepo.getByCanonicalOrLegacyId(pinnedId);
          if (pinnedRow) {
            matches.push(pinnedRow);
          }
        }
        if (pinnedRow) {
          const isLive = LIVE_STATES.includes(String(pinnedRow.state || '').toUpperCase());
          const lastUpdatedMs = pinnedRow.updatedAt ? new Date(pinnedRow.updatedAt).getTime() : 0;
          if (isLive && (Date.now() - lastUpdatedMs > 45000)) {
            const syncService: SyncService | undefined = (request.server as any).syncService || (fastify as any).syncService;
            if (syncService) {
              syncService.syncFixtureDetails(pinnedRow.id).catch(() => {});
            }
          }
        }
      }

      const formattedMatches = matches.map((m) => formatV2TrayMatch(m));

      let quotaExhausted = false;
      try {
        const scheduler: any = (fastify as any).scheduler;
        const schedulerDiag = scheduler ? scheduler.getDiagnostics() : null;
        const gateway = new RequestGateway();
        const effectiveProvider = process.env.ACTIVE_PROVIDER || process.env.FOOTBALL_PROVIDER || 'mock';
        const quotaDiag = await gateway.getDiagnostics(effectiveProvider);

        if (
          quotaDiag.mode === 'RESERVE_MODE' ||
          quotaDiag.mode === 'HARD_STOP' ||
          quotaDiag.remainingDaily <= 10 ||
          quotaDiag.isExhausted ||
          schedulerDiag?.currentState === 'IDLE_LIMIT' ||
          schedulerDiag?.lastCycleOutcome === 'LIMIT_REACHED' ||
          schedulerDiag?.lastCycleOutcome === 'EXHAUSTED' ||
          schedulerDiag?.lastCycleAction?.includes('LIMIT')
        ) {
          quotaExhausted = true;
        }
      } catch {
        // Non-critical fallback
      }

      // Append custom quota exhaustion header regardless of 200 or 304
      if (quotaExhausted) {
        reply.header('X-Quota-Exhausted', 'true');
      }

      // If the 3500ms timeout elapsed while fetch is still in flight (matches empty),
      // do NOT set a cacheable ETag header on interim empty response
      const isInterimSyncingEmpty = isDateSyncing && formattedMatches.length === 0;

      if (!isInterimSyncingEmpty) {
        // Compute deterministic ETag based on v2 match data, query parameters, quotaExhausted state, and followed counts
        // Exclude volatile runtime clock / polling timestamps (trust.sourceObservedAt, trust.dataAgeSeconds) so ETag remains stable between polls
        const stableMatches = formattedMatches.map(({ trust, ...rest }) => ({
          ...rest,
          trust: trust ? { freshnessState: trust.freshnessState, provider: trust.provider } : undefined,
        }));
        const cacheKey = `${dateParam || ''}_${teamsParam || ''}_${competitionsParam || ''}_${JSON.stringify(stableMatches)}_${quotaExhausted}_${JSON.stringify(followedCountsByDate)}`;
        const dataHash = createHash('md5').update(cacheKey).digest('hex');
        const etag = `"${dataHash}"`;

        const rawIfNoneMatch = request.headers['if-none-match'];
        const cleanIfNoneMatch = typeof rawIfNoneMatch === 'string'
          ? rawIfNoneMatch.replace(/^W\//i, '').replace(/^"+|"+$/g, '').trim()
          : null;
        if (cleanIfNoneMatch && (cleanIfNoneMatch === dataHash || cleanIfNoneMatch === etag.replace(/^"+|"+$/g, ''))) {
          reply.header('ETag', etag);
          if (quotaExhausted) {
            reply.header('X-Quota-Exhausted', 'true');
          }
          return reply.status(304).send();
        }

        reply.header('ETag', etag);
      } else {
        reply.header('Cache-Control', 'no-store, no-cache');
      }

      const envelope = createResponseEnvelope(
        {
          matches: formattedMatches,
          quotaExhausted,
          syncState: isDateSyncing ? 'SYNCING' : 'IDLE',
          followedCountsByDate,
          followedTeamCompetitions,
        },
        { schemaVersion: '2.0', version: 2, quotaExhausted }
      );

      return reply.send(envelope);
    } catch (err: any) {
      request.log.error(err, 'Failed to fetch v2 tray matches');
      return reply.status(500).send(
        createErrorEnvelope('INTERNAL_ERROR', 'Failed to retrieve tray matches', { details: err?.message }, { schemaVersion: '2.0' })
      );
    }
  });

  // ----------------------------------------------------
  // Delivery API v2: Live Tray Matches (Lightweight Taskbar Popout Summary)
  // ----------------------------------------------------
  fastify.get<{ Querystring: { teams?: string; competitions?: string } }>(
    '/api/v2/tray/live',
    async (request, reply) => {
      try {
        const teamsParam = request.query.teams;
        const competitionsParam = request.query.competitions;
        const teams = teamsParam
          ? teamsParam.split(',').map((t) => t.trim()).filter((t) => t.length > 0)
          : undefined;
        const competitions = competitionsParam
          ? competitionsParam.split(',').map((c) => c.trim()).filter((c) => c.length > 0)
          : undefined;

        // 1. Strictly query SQLite canonical tables (zero provider calls)
        const matches = await fixturesRepo.getLiveTrayMatches({ teams, competitions });
        const formattedMatches = matches.map((m) => formatV2TrayMatch(m));

        // 2. Assess daemon fallback / quota diagnostics
        let cachedFallback = false;
        let quotaExhausted = false;
        try {
          const scheduler: any = (fastify as any).scheduler;
          const schedulerDiag = scheduler ? scheduler.getDiagnostics() : null;
          const gateway = new RequestGateway();
          const effectiveProvider = process.env.ACTIVE_PROVIDER || process.env.FOOTBALL_PROVIDER || 'mock';
          const quotaDiag = await gateway.getDiagnostics(effectiveProvider);

          if (
            quotaDiag.isExhausted ||
            quotaDiag.mode === 'HARD_STOP' ||
            quotaDiag.mode === 'RESERVE_MODE' ||
            quotaDiag.remainingDaily <= 10 ||
            schedulerDiag?.lastCycleOutcome === 'LIMIT_REACHED' ||
            schedulerDiag?.lastCycleOutcome === 'EXHAUSTED' ||
            schedulerDiag?.currentState === 'IDLE_LIMIT'
          ) {
            quotaExhausted = true;
          }
          const syncService = (fastify as any).syncService;
          if (
            schedulerDiag?.lastCycleOutcome === 'RECOVERABLE_FALLBACK' ||
            schedulerDiag?.lastCycleOutcome === 'CIRCUIT_OPEN' ||
            syncService?.lastSyncSummary?.cachedFallback === true ||
            (syncService?.provider as any)?.circuitBreaker?.getState?.() === 1 || // CircuitState.OPEN
            (fastify as any).isCachedFallback === true
          ) {
            cachedFallback = true;
          }
        } catch {
          // Non-critical diagnostic check
        }

        // 3. Compute deterministic timestamp- and composition-based ETag
        const maxUpdatedAtMs = matches.reduce((max, m) => {
          const t = m.updatedAt ? new Date(m.updatedAt).getTime() : 0;
          return t > max ? t : max;
        }, 0);

        // Crucial Caveat Fix: Hash the sorted match IDs to prevent collisions on match removal
        const idsHash = createHash('md5')
          .update(matches.map((m) => m.id).sort().join(','))
          .digest('hex')
          .slice(0, 8);

        const filterKey = `${teamsParam || ''}_${competitionsParam || ''}_${cachedFallback}`;
        const filterHash = createHash('md5').update(filterKey).digest('hex').slice(0, 8);
        const etag = `W/"live-${maxUpdatedAtMs}-${idsHash}-${filterHash}"`;

        // 4. ETag 304 Not Modified validation: immediate return on cache hit
        const ifNoneMatch = request.headers['if-none-match'];
        if (ifNoneMatch && (ifNoneMatch === etag || ifNoneMatch === etag.replace(/^W\//, '') || `W/${ifNoneMatch}` === etag)) {
          reply.header('ETag', etag);
          if (quotaExhausted) reply.header('X-Quota-Exhausted', 'true');
          if (cachedFallback) reply.header('X-Cached-Fallback', 'true');
          return reply.status(304).send();
        }

        // 5. Cache miss: return 200 OK with payload
        if (quotaExhausted) reply.header('X-Quota-Exhausted', 'true');
        if (cachedFallback) reply.header('X-Cached-Fallback', 'true');
        reply.header('ETag', etag);

        return reply.send(
          createResponseEnvelope(
            { matches: formattedMatches, quotaExhausted, cachedFallback },
            { schemaVersion: '2.0', version: 2, cachedFallback, quotaExhausted }
          )
        );
      } catch (err: any) {
        request.log.error(err, 'Failed to fetch live tray matches');
        return reply.status(500).send(
          createErrorEnvelope('INTERNAL_ERROR', 'Failed to retrieve live tray matches', { details: err?.message }, { schemaVersion: '2.0' })
        );
      }
    }
  );

  // ----------------------------------------------------
  // Delivery API v2: Match Details (Canonical & Alias Resolution with ETag 304)
  // ----------------------------------------------------
  const handleMatchDetails = async (request: any, reply: any) => {
    const { id } = request.params;
    if (!id || typeof id !== 'string' || id.trim().length === 0) {
      return reply.status(400).send(
        createErrorEnvelope('INVALID_REQUEST', 'Match ID is required.', undefined, { schemaVersion: '2.0' })
      );
    }

    try {
      // 1. Strictly query SQLite canonical tables directly
      let fixture: any = await fixturesRepo.getByCanonicalOrLegacyId(id.trim());
      if (!fixture) {
        return reply.status(404).send(
          createErrorEnvelope('MATCH_NOT_FOUND', `No match found with ID '${id}'.`, undefined, { schemaVersion: '2.0' })
        );
      }

      // 2. Hydrate missing or stale live match details from active provider (15s TTL / CircuitBreaker protected)
      let syncService = (request.server as any).syncService || (fastify as any).syncService;
      if (!syncService) {
        try {
          const provider = createProvider({ provider: 'fotmob' });
          syncService = new SyncService(provider);
          (fastify as any).syncService = syncService;
        } catch {
          // Non-critical fallback if provider cannot be instantiated
        }
      }
      if (syncService) {
        const hasDetails = (fixture.statistics && fixture.statistics.length > 0) || (fixture.lineups && fixture.lineups.length > 0);
        const LIVE_STATES = ['LIVE', '1H', '2H', 'HT', 'ET', 'P', 'BT', 'FIRST_HALF', 'SECOND_HALF', 'HALFTIME', 'EXTRA_TIME', 'PENALTIES', 'IN_PLAY'];
        const isLive = LIVE_STATES.includes(String(fixture.state || '').toUpperCase());
        const isFinished = ['FINISHED', 'FT', 'FINISHED_AET', 'FINISHED_PEN'].includes(String(fixture.state || '').toUpperCase());
        const isStarted = isLive || isFinished || String(fixture.state || '').toUpperCase() === 'HALFTIME';
        const lastUpdatedMs = fixture.updatedAt ? new Date(fixture.updatedAt).getTime() : 0;
        const isStale = (Date.now() - lastUpdatedMs) > 15000;

        const homeScore = fixture.homeScore ?? 0;
        const awayScore = fixture.awayScore ?? 0;
        const totalScore = homeScore + awayScore;
        const existingEvents = fixture.events ?? [];
        const goalEventCount = existingEvents.filter((e: any) => e.type === 'Goal').length;
        const missingGoals = isStale && totalScore > 0 && goalEventCount < totalScore;
        const missingEventsOrStats = (!hasDetails || isStale) && isStarted && ((!fixture.events || fixture.events.length === 0) || (!fixture.statistics || fixture.statistics.length === 0));

        let cached = typeof syncService.getCachedMatchDetails === 'function'
          ? (syncService.getCachedMatchDetails(fixture.id) || syncService.getCachedMatchDetails(id.trim()))
          : null;

        const cachedAtMs = (cached as any)?.cachedAt || 0;
        const isLiveCacheStale = isLive && (Date.now() - cachedAtMs > 30000);
        const hasSparseCommentary = isStarted && (!cached?.commentary || !Array.isArray(cached.commentary) || cached.commentary.length <= 5);

        const hasRichCommentary = cached?.commentary &&
          Array.isArray(cached.commentary) &&
          cached.commentary.length > 5 &&
          cached.commentary.some((c: any) => c.type === 'whistle' || c.type === 'shot' || c.type === 'save' || c.type === 'block' || c.type === 'woodwork' || c.type === 'stat');

        if (missingGoals || missingEventsOrStats || !hasDetails || (isLive && (isStale || isLiveCacheStale)) || (isStarted && isStale && (!hasRichCommentary || hasSparseCommentary))) {
          try {
            const timeoutPromise = new Promise<false>((resolve) =>
              setTimeout(() => resolve(false), 3500)
            );
            const syncPromise = syncService.syncFixtureDetails(fixture.id);
            const synced = await Promise.race([syncPromise, timeoutPromise]);
            if (synced) {
              const updated = await fixturesRepo.getByCanonicalOrLegacyId(id.trim());
              if (updated) fixture = updated;
              cached = syncService.getCachedMatchDetails(fixture.id) || syncService.getCachedMatchDetails(id.trim());
            }
          } catch (syncErr: any) {
            request.log.warn(`On-demand match hydration failed for '${id}': ${syncErr?.message}`);
          }
        }

        if ((!fixture.events || fixture.events.length === 0) && cached?.events && Array.isArray(cached.events) && cached.events.length > 0) {
          fixture.events = cached.events.map((e: any) => ({
            ...e,
            fixtureId: fixture.id,
            teamId: (e.teamId === cached.homeTeam?.providerId ? fixture.homeTeamId : (e.teamId === cached.awayTeam?.providerId ? fixture.awayTeamId : (e.teamId || fixture.homeTeamId))),
          }));
        }
        if ((!fixture.statistics || fixture.statistics.length === 0) && cached?.stats && Array.isArray(cached.stats) && cached.stats.length > 0) {
          fixture.statistics = cached.stats.map((s: any) => ({
            ...s,
            fixtureId: fixture.id,
            teamId: (s.teamId === cached.homeTeam?.providerId ? fixture.homeTeamId : (s.teamId === cached.awayTeam?.providerId ? fixture.awayTeamId : (s.teamId || fixture.homeTeamId))),
          }));
        }

        if (cached?.commentary && Array.isArray(cached.commentary)) {
          fixture.commentary = cached.commentary;
        }
        if (cached?.h2hAndPredictions) {
          fixture.h2hAndPredictions = cached.h2hAndPredictions;
        }
        if (cached?.heatmaps) {
          fixture.heatmaps = cached.heatmaps;
        }
        if ((!fixture.lineups || fixture.lineups.length === 0) && cached?.lineups) {
          fixture.lineups = cached.lineups.map((l: any) => {
            const starters = l.startXi || l.starters || l.startingXI || [];
            const substitutes = l.substitutes || l.subs || l.bench || [];
            const unavailable = l.unavailable || l.naPlayers || l.injured || [];
            const mappedTeamId = (l.teamId === cached.homeTeam?.providerId ? fixture.homeTeamId : (l.teamId === cached.awayTeam?.providerId ? fixture.awayTeamId : (l.teamId || fixture.homeTeamId)));
            return {
              ...l,
              teamId: mappedTeamId,
              startingXI: starters,
              startXi: starters,
              starters,
              substitutes,
              subs: substitutes,
              unavailable,
              players: l.players || [
                ...starters.map((p: any) => ({ ...p, isStarter: true })),
                ...substitutes.map((p: any) => ({ ...p, isStarter: false })),
              ],
            };
          });
        }
        if (cached?.venue && !fixture.venue) {
          fixture.venue = cached.venue;
        }
        if (cached?.referee && !fixture.referee) {
          fixture.referee = cached.referee;
        }
        if (cached?.standings) {
          fixture.standings = cached.standings;
        }
        if (cached?.topScorers) {
          fixture.topScorers = cached.topScorers;
        }
        if (cached?.topAssists) {
          fixture.topAssists = cached.topAssists;
        }

        if (!fixture.standings && syncService?.provider && typeof (syncService.provider as any).getCompetitionHub === 'function') {
          const compId = fixture.competition?.canonicalId || fixture.competitionId || fixture.competition?.id;
          if (compId) {
            try {
              const hub = await (syncService.provider as any).getCompetitionHub(compId);
              if (hub && hub.standings) {
                const homeId = fixture.homeTeam?.canonicalId || fixture.homeTeam?.id || fixture.homeTeamId;
                const awayId = fixture.awayTeam?.canonicalId || fixture.awayTeam?.id || fixture.awayTeamId;
                fixture.standings = (hub.standings || []).map((s: any) => ({
                  ...s,
                  isHomeTeam: String(s.teamId) === String(homeId) || String(s.teamName).toLowerCase() === String(fixture.homeTeam?.name || '').toLowerCase(),
                  isAwayTeam: String(s.teamId) === String(awayId) || String(s.teamName).toLowerCase() === String(fixture.awayTeam?.name || '').toLowerCase(),
                }));
                fixture.topScorers = hub.topScorers || [];
                fixture.topAssists = hub.topAssists || [];
              }
            } catch {
              // Non-blocking fallback
            }
          }
        }
      }

      // 3. Compute fixture ETag based on entity updatedAt timestamp, events count, commentary count, and stats/lineups hash
      const fixtureUpdatedMs = fixture.updatedAt ? new Date(fixture.updatedAt).getTime() : 0;
      const eventCount = fixture.events?.length ?? 0;
      const commentaryCount = fixture.commentary?.length ?? 0;
      const detailsDigest = createHash('md5')
        .update(JSON.stringify(fixture.statistics || []) + JSON.stringify(fixture.lineups || []))
        .digest('hex')
        .slice(0, 8);
      const etag = `W/"match-${fixture.id}-${fixtureUpdatedMs}-${eventCount}-${commentaryCount}-${detailsDigest}"`;

      // 4. ETag 304 Validation
      const ifNoneMatch = request.headers['if-none-match'];
      if (ifNoneMatch && (ifNoneMatch === etag || ifNoneMatch === etag.replace(/^W\//, '') || `W/${ifNoneMatch}` === etag)) {
        reply.header('ETag', etag);
        return reply.status(304).send();
      }

      const formatted = formatV2MatchDetail(fixture);
      reply.header('ETag', etag);
      return reply.send(createResponseEnvelope(formatted, { schemaVersion: '2.0', version: 2 }));
    } catch (err: any) {
      request.log.error(err, `Failed to fetch match details '${id}'`);
      return reply.status(500).send(
        createErrorEnvelope('INTERNAL_ERROR', 'Failed to retrieve match details', { details: err?.message }, { schemaVersion: '2.0' })
      );
    }
  };

  // Register primary route and backward-compatible singular alias
  fastify.get<{ Params: { id: string } }>('/api/v2/matches/:id', handleMatchDetails);
  fastify.get<{ Params: { id: string } }>('/api/v2/match/:id', handleMatchDetails);

  // ----------------------------------------------------
  // Delivery API v2: Competition Hub (Standings & Leaders)
  // ----------------------------------------------------
  const competitionHubCache = new Map<string, { timestamp: number; payload: any }>();

  const handleCompetitionHub = async (request: any, reply: any) => {
    const id = request.params.id;
    if (!id || typeof id !== 'string' || id.trim().length === 0) {
      return reply.status(400).send(createErrorEnvelope('INVALID_PARAMS', 'Missing competition id'));
    }

    const trimmedId = id.trim();
    const queryName = typeof (request.query as any)?.name === 'string' ? (request.query as any).name.trim() : null;
    const queryLogoUrl = typeof (request.query as any)?.logoUrl === 'string' ? (request.query as any).logoUrl.trim() : null;

    const resolvedFotmobId = FotmobProvider.resolveFotmobLeagueId(trimmedId, queryName, queryLogoUrl);
    const cacheKey = resolvedFotmobId ? `hub_${resolvedFotmobId}` : `${trimmedId.toLowerCase()}_${(queryName || '').toLowerCase()}`;
    const cachedEntry = competitionHubCache.get(cacheKey);
    if (cachedEntry && Date.now() - cachedEntry.timestamp < 15 * 60 * 1000) {
      reply.header('Cache-Control', 'public, max-age=900, stale-while-revalidate=60');
      return reply.send(createResponseEnvelope(cachedEntry.payload, { schemaVersion: '2.0', version: 2 }));
    }

    try {
      let syncService = (request.server as any).syncService || (fastify as any).syncService;
      if (!syncService) {
        const provider = createProvider();
        syncService = new SyncService(provider);
        (fastify as any).syncService = syncService;
      }

      let hub: any = null;
      if (syncService?.provider && typeof (syncService.provider as any).getCompetitionHub === 'function') {
        hub = await (syncService.provider as any).getCompetitionHub(trimmedId, queryName, queryLogoUrl);
      }

      // Check DB for competition metadata if missing
      let compRecord: any = null;
      try {
        compRecord = await db.query.competitions.findFirst({
          where: or(
            eq(schema.competitions.canonicalId, trimmedId),
            eq(schema.competitions.id, trimmedId),
            queryName ? eq(schema.competitions.name, queryName) : undefined
          ),
        });
      } catch {
        // Non-blocking
      }

      if (!hub && compRecord) {
        const altId = compRecord.canonicalId || compRecord.id || compRecord.name;
        if (altId && altId !== trimmedId && syncService?.provider && typeof (syncService.provider as any).getCompetitionHub === 'function') {
          hub = await (syncService.provider as any).getCompetitionHub(altId, compRecord.name || queryName, compRecord.logoUrl || queryLogoUrl);
        }
      }

      // Match-Center Parity Fallback: query SQLite fixtures for this competition to extract numeric ID or use match details
      if (!hub || !hub.standings || hub.standings.length === 0) {
        try {
          const sampleFixture = await db
            .select({
              id: schema.fixtures.id,
              competitionId: schema.fixtures.competitionId,
              compName: schema.competitions.name,
              compLogoUrl: schema.competitions.logoUrl,
            })
            .from(schema.fixtures)
            .innerJoin(schema.competitions, eq(schema.fixtures.competitionId, schema.competitions.id))
            .where(
              or(
                eq(schema.fixtures.competitionId, trimmedId),
                eq(schema.competitions.canonicalId, trimmedId),
                compRecord?.id ? eq(schema.fixtures.competitionId, compRecord.id) : undefined,
                queryName ? eq(schema.competitions.name, queryName) : undefined
              )
            )
            .limit(1);

          if (sampleFixture && sampleFixture.length > 0) {
            const fix = sampleFixture[0];
            const fixLogoId = FotmobProvider.resolveFotmobLeagueId(fix.competitionId, fix.compName, fix.compLogoUrl);
            if (fixLogoId && (!resolvedFotmobId || resolvedFotmobId !== fixLogoId) && syncService?.provider && typeof (syncService.provider as any).getCompetitionHub === 'function') {
              hub = await (syncService.provider as any).getCompetitionHub(fixLogoId, fix.compName, fix.compLogoUrl);
            }

            // If still no hub or standings, use Match Details parity fallback
            if ((!hub || !hub.standings || hub.standings.length === 0) && syncService?.provider && typeof (syncService.provider as any).getMatchDetails === 'function') {
              const details = await (syncService.provider as any).getMatchDetails(fix.id);
              if (details && details.standings && details.standings.length > 0) {
                hub = {
                  competitionId: trimmedId,
                  competitionName: fix.compName || queryName || compRecord?.name || 'Competition',
                  competitionLogoUrl: fix.compLogoUrl || queryLogoUrl || compRecord?.logoUrl || (fixLogoId ? `https://images.fotmob.com/image_resources/logo/leaguelogo/dark/${fixLogoId}.png` : null),
                  countryCode: compRecord?.country || null,
                  season: null,
                  standings: details.standings,
                  topScorers: details.topScorers || [],
                  topAssists: details.topAssists || [],
                };
              }
            }
          }
        } catch {
          // Non-blocking
        }
      }

      // Offline fallback: ONLY aggregate from local matches strictly for targetCompId if competition was found in DB
      if (!hub || !hub.standings || hub.standings.length === 0) {
        const targetCompId = compRecord?.id;
        if (targetCompId) {
          try {
            const localMatches = await fixturesRepo.getTrayMatches({ competitions: [targetCompId], limit: 100 });
            if (localMatches && localMatches.length > 0) {
              const teamMap = new Map<string, { id: string; name: string; logoUrl?: string; played: number; wins: number; draws: number; losses: number; gf: number; ga: number; pts: number; form: string[] }>();

              for (const m of localMatches) {
                if (!m.homeTeam || !m.awayTeam) continue;
                const hId = m.homeTeam.id;
                const aId = m.awayTeam.id;
                if (!teamMap.has(hId)) {
                  teamMap.set(hId, { id: hId, name: m.homeTeam.name || 'Home Team', logoUrl: m.homeTeam.logoUrl ?? undefined, played: 0, wins: 0, draws: 0, losses: 0, gf: 0, ga: 0, pts: 0, form: [] });
                }
                if (!teamMap.has(aId)) {
                  teamMap.set(aId, { id: aId, name: m.awayTeam.name || 'Away Team', logoUrl: m.awayTeam.logoUrl ?? undefined, played: 0, wins: 0, draws: 0, losses: 0, gf: 0, ga: 0, pts: 0, form: [] });
                }
                const h = teamMap.get(hId)!;
                const a = teamMap.get(aId)!;
                const st = String(m.state || '').toUpperCase();
                if (st.includes('FINISH') || st === 'FT' || st === 'AET' || st === 'PEN') {
                  h.played++;
                  a.played++;
                  const hScore = m.homeScore ?? (m as any).score?.home ?? 0;
                  const aScore = m.awayScore ?? (m as any).score?.away ?? 0;
                  h.gf += hScore;
                  h.ga += aScore;
                  a.gf += aScore;
                  a.ga += hScore;
                  if (hScore > aScore) {
                    h.wins++;
                    h.pts += 3;
                    h.form.push('W');
                    a.losses++;
                    a.form.push('L');
                  } else if (hScore < aScore) {
                    a.wins++;
                    a.pts += 3;
                    a.form.push('W');
                    h.losses++;
                    h.form.push('L');
                  } else {
                    h.draws++;
                    h.pts += 1;
                    h.form.push('D');
                    a.draws++;
                    a.pts += 1;
                    a.form.push('D');
                  }
                }
              }

              const sortedTeams = Array.from(teamMap.values()).sort((a, b) => b.pts - a.pts || (b.gf - b.ga) - (a.gf - a.ga));
              const syntheticStandings = sortedTeams.map((t, idx) => ({
                rank: idx + 1,
                teamId: t.id,
                teamName: t.name,
                teamLogoUrl: t.logoUrl,
                played: t.played,
                wins: t.wins,
                draws: t.draws,
                losses: t.losses,
                goalsFor: t.gf,
                goalsAgainst: t.ga,
                goalDiff: t.gf - t.ga,
                points: t.pts,
                form: t.form.slice(-5).join(''),
              }));

              hub = {
                competitionId: trimmedId,
                competitionName: compRecord?.name || queryName || 'Competition',
                competitionLogoUrl: compRecord?.logoUrl || queryLogoUrl || (resolvedFotmobId ? `https://images.fotmob.com/image_resources/logo/leaguelogo/dark/${resolvedFotmobId}.png` : null),
                countryCode: compRecord?.country || null,
                standings: syntheticStandings,
                topScorers: hub?.topScorers || [],
                topAssists: hub?.topAssists || [],
              };
            }
          } catch {
            // Non-blocking
          }
        }
      }

      if (!hub) {
        hub = {
          competitionId: trimmedId,
          competitionName: queryName || compRecord?.name || 'Competition',
          competitionLogoUrl: queryLogoUrl || compRecord?.logoUrl || (resolvedFotmobId ? `https://images.fotmob.com/image_resources/logo/leaguelogo/dark/${resolvedFotmobId}.png` : null),
          countryCode: compRecord?.country || null,
          standings: [],
          topScorers: [],
          topAssists: [],
        };
      } else {
        // Preserve clean queryName and queryLogoUrl if hub has generic/id defaults
        if (queryName && (!hub.competitionName || hub.competitionName === 'Competition' || hub.competitionName.startsWith('fotmob_') || hub.competitionName.startsWith('comp_') || hub.competitionName.startsWith('ft_comp_'))) {
          hub.competitionName = queryName;
        }
        if (!hub.competitionLogoUrl && queryLogoUrl) {
          hub.competitionLogoUrl = queryLogoUrl;
        }
      }

      const formatted = formatV2CompetitionHub(hub);
      competitionHubCache.set(cacheKey, { timestamp: Date.now(), payload: formatted });

      reply.header('Cache-Control', 'public, max-age=900, stale-while-revalidate=60');
      return reply.send(createResponseEnvelope(formatted, { schemaVersion: '2.0', version: 2 }));
    } catch (err: any) {
      request.log.error(err, `Failed to retrieve competition hub '${id}'`);
      return reply.status(500).send(
        createErrorEnvelope('INTERNAL_ERROR', 'Failed to retrieve competition hub', { details: err?.message }, { schemaVersion: '2.0' })
      );
    }
  };

  fastify.get<{ Params: { id: string } }>('/api/v2/competitions/:id/hub', handleCompetitionHub);
  fastify.get<{ Params: { id: string } }>('/api/v2/competition/:id/hub', handleCompetitionHub);

  // ----------------------------------------------------
  // Delivery API v2: Catalog Search (Teams)
  // ----------------------------------------------------
  fastify.get<{ Querystring: { q?: string; limit?: string } }>('/api/v2/catalog/teams', async (request, reply) => {
    try {
      const q = request.query.q ?? '';
      const limit = parseInt(request.query.limit || '20', 10) || 20;
      const results = await catalogRepo.searchTeams(q, limit);

      const envelope = createResponseEnvelope(results, { schemaVersion: '2.0', version: 2 });
      return reply.send(envelope);
    } catch (err: any) {
      request.log.error(err, 'Failed to search teams catalog');
      return reply.status(500).send(
        createErrorEnvelope('INTERNAL_ERROR', 'Failed to search teams catalog', { details: err?.message }, { schemaVersion: '2.0' })
      );
    }
  });

  // ----------------------------------------------------
  // Delivery API v2: Catalog Search (Competitions)
  // ----------------------------------------------------
  fastify.get<{ Querystring: { q?: string; limit?: string } }>('/api/v2/catalog/competitions', async (request, reply) => {
    try {
      const q = request.query.q ?? '';
      const limit = parseInt(request.query.limit || '20', 10) || 20;
      const results = await catalogRepo.searchCompetitions(q, limit);

      const envelope = createResponseEnvelope(results, { schemaVersion: '2.0', version: 2 });
      return reply.send(envelope);
    } catch (err: any) {
      request.log.error(err, 'Failed to search competitions catalog');
      return reply.status(500).send(
        createErrorEnvelope('INTERNAL_ERROR', 'Failed to search competitions catalog', { details: err?.message }, { schemaVersion: '2.0' })
      );
    }
  });

  // ----------------------------------------------------
  // Delivery API v2: User Preferences Sync
  // ----------------------------------------------------
  fastify.get('/api/v2/preferences', async (request, reply) => {
    const prefService = (fastify as any).preferencesService;
    if (!prefService) {
      return reply.status(503).send(createErrorEnvelope('SERVICE_UNAVAILABLE', 'Preferences service is unavailable.'));
    }
    const data = prefService.getPreferences();
    return reply.send(createResponseEnvelope(data, { schemaVersion: '2.0', version: 2 }));
  });

  fastify.post<{
    Body: {
      competitionIds?: Array<string | number>;
      followedCompetitions?: Array<{ id: string; name?: string; code?: string }>;
      teamIds?: Array<string | number>;
      followedTeams?: Array<{ id: string; name?: string; code?: string }>;
    };
  }>('/api/v2/preferences', async (request, reply) => {
    const prefService = (fastify as any).preferencesService;
    if (!prefService) {
      return reply.status(503).send(createErrorEnvelope('SERVICE_UNAVAILABLE', 'Preferences service is unavailable.'));
    }

    try {
      const updated = await prefService.updatePreferences(request.body || {});
      return reply.status(200).send(createResponseEnvelope(updated, { schemaVersion: '2.0', version: 2 }));
    } catch (err: any) {
      request.log.error(err, 'Failed to update daemon preferences');
      return reply.status(500).send(
        createErrorEnvelope('INTERNAL_ERROR', 'Failed to update preferences', { details: err?.message }, { schemaVersion: '2.0' })
      );
    }
  });
};

