import { FastifyInstance, FastifyPluginAsync } from 'fastify';
import { createHash } from 'crypto';
import { FixturesRepository } from '../repositories/fixtures.repository.js';
import { checkDatabaseConnection } from '../db/index.js';
import { createResponseEnvelope, createErrorEnvelope } from './envelope.js';
import { RequestGateway } from '../gateway/request-gateway.js';
import { formatV2TrayMatch, formatV2MatchDetail } from './v2-dto.js';
import { CatalogRepository } from '../repositories/catalog.repository.js';

export const apiRoutes: FastifyPluginAsync = async (fastify: FastifyInstance) => {
  const fixturesRepo = new FixturesRepository();
  const catalogRepo = new CatalogRepository();

  // ----------------------------------------------------
  // Health check endpoint (backward-compatible & enhanced)
  // ----------------------------------------------------
  fastify.get('/health', async (request, reply) => {
    const dbStatus = await checkDatabaseConnection();
    const scheduler: any = (fastify as any).scheduler;
    const schedulerDiag = scheduler ? scheduler.getDiagnostics() : null;
    const gateway = new RequestGateway();
    const quotaDiag = await gateway.getDiagnostics(process.env.FOOTBALL_PROVIDER || 'mock');

    return reply.status(200).send({
      status: 'ok',
      database: dbStatus.connected ? 'connected' : 'disconnected',
      latencyMs: dbStatus.latencyMs,
      timestamp: new Date().toISOString(),
      version: '1.0.0',
      provider: process.env.FOOTBALL_PROVIDER || 'mock',
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
    const quotaDiag = await gateway.getDiagnostics(process.env.FOOTBALL_PROVIDER || 'mock');
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
        mode: process.env.FOOTBALL_PROVIDER || 'mock',
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
  fastify.get<{ Querystring: { date?: string; teams?: string } }>('/api/tray', async (request, reply) => {
    try {
      const dateParam = request.query.date;
      const teamsParam = request.query.teams;
      const teams = teamsParam
        ? teamsParam.split(',').map((t) => t.trim()).filter((t) => t.length > 0)
        : undefined;

      const matches = await fixturesRepo.getTrayMatches({
        date: dateParam,
        teams,
      });

      const formattedMatches = matches.map((m) => ({
        id: m.id,
        competition: {
          id: m.competition?.id,
          name: m.competition?.name,
          code: m.competition?.code,
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
        kickoffTime: fixture.kickoffTime,
        events: fixture.events,
        lineups: fixture.lineups,
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
  fastify.get<{ Querystring: { date?: string; teams?: string } }>('/api/v2/tray', async (request, reply) => {
    try {
      const dateParam = request.query.date;
      const teamsParam = request.query.teams;
      const teams = teamsParam
        ? teamsParam.split(',').map((t) => t.trim()).filter((t) => t.length > 0)
        : undefined;

      const matches = await fixturesRepo.getTrayMatches({
        date: dateParam,
        teams,
      });
      const formattedMatches = matches.map((m) => formatV2TrayMatch(m));

      // Compute deterministic ETag based on v2 match data and query parameters
      const cacheKey = `${dateParam || ''}_${teamsParam || ''}_${JSON.stringify(formattedMatches)}`;
      const dataHash = createHash('md5').update(cacheKey).digest('hex');
      const etag = `"${dataHash}"`;

      const ifNoneMatch = request.headers['if-none-match'];
      if (ifNoneMatch && (ifNoneMatch === etag || ifNoneMatch === dataHash || ifNoneMatch === `W/${etag}`)) {
        reply.header('ETag', etag);
        return reply.status(304).send();
      }

      const envelope = createResponseEnvelope(
        { matches: formattedMatches },
        { schemaVersion: '2.0', version: 2 }
      );

      reply.header('ETag', etag);
      return reply.send(envelope);
    } catch (err: any) {
      request.log.error(err, 'Failed to fetch v2 tray matches');
      return reply.status(500).send(
        createErrorEnvelope('INTERNAL_ERROR', 'Failed to retrieve tray matches', { details: err?.message }, { schemaVersion: '2.0' })
      );
    }
  });

  // ----------------------------------------------------
  // Delivery API v2: Match Details (Canonical & Alias Resolution)
  // ----------------------------------------------------
  fastify.get<{ Params: { id: string } }>('/api/v2/match/:id', async (request, reply) => {
    const { id } = request.params;

    if (!id || typeof id !== 'string' || id.trim().length === 0) {
      return reply.status(400).send(
        createErrorEnvelope('INVALID_REQUEST', 'Match ID is required and must not be empty.', undefined, { schemaVersion: '2.0' })
      );
    }

    try {
      let fixture = await fixturesRepo.getByCanonicalOrLegacyId(id.trim());

      if (!fixture) {
        return reply.status(404).send(
          createErrorEnvelope('MATCH_NOT_FOUND', `No match found with ID '${id}'.`, undefined, { schemaVersion: '2.0' })
        );
      }

      // --- Tier 3: Cache-Aside On-Demand Resolver ---
      // Check if lineups or statistics are absent and match is approaching (<45m), live, or finished
      const syncService = (fastify as any).syncService;
      if (syncService) {
        const now = new Date();
        const kickoff = new Date(fixture.kickoffTime);
        const minutesToKickoff = (kickoff.getTime() - now.getTime()) / (60 * 1000);
        const stateUpper = (fixture.state || '').toUpperCase().trim();
        const isLive = ['LIVE', 'HALFTIME', '1H', '2H', 'ET', 'P', 'BT', 'IN_PLAY'].includes(stateUpper);
        const isFinished = ['FINISHED', 'FT', 'AET', 'PEN', 'POSTMATCH'].includes(stateUpper);
        const isApproaching = minutesToKickoff <= 45 && !isFinished;

        const hasLineups = fixture.lineups && fixture.lineups.length > 0;
        const hasStatistics = fixture.statistics && fixture.statistics.length > 0;
        const lastPolled = fixture.lastPolledAt ? new Date(fixture.lastPolledAt).getTime() : 0;
        const secondsSinceLastPoll = (now.getTime() - lastPolled) / 1000;

        const needsLineups = (isApproaching || isLive || isFinished) && !hasLineups;
        const needsStatistics = (isLive || isFinished) && !hasStatistics;
        const isLiveRefreshDue = isLive && secondsSinceLastPoll >= 180;

        if ((needsLineups || needsStatistics || isLiveRefreshDue) && secondsSinceLastPoll >= 30) {
          try {
            await syncService.syncFixtureDetails(fixture.id);
            // Reload populated fixture from PostgreSQL
            const reloaded = await fixturesRepo.getByCanonicalOrLegacyId(fixture.id);
            if (reloaded) {
              fixture = reloaded;
            }
          } catch (err: any) {
            request.log.warn(`On-demand fixture sync failed for ${fixture.id}: ${err?.message}`);
          }
        }
      }

      const formatted = formatV2MatchDetail(fixture);
      const envelope = createResponseEnvelope(formatted, { schemaVersion: '2.0', version: 2 });
      return reply.send(envelope);
    } catch (err: any) {
      request.log.error(err, `Failed to fetch v2 match '${id}'`);
      return reply.status(500).send(
        createErrorEnvelope('INTERNAL_ERROR', 'Failed to retrieve match details', { details: err?.message }, { schemaVersion: '2.0' })
      );
    }
  });

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

