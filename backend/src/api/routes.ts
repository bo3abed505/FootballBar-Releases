import { FastifyInstance, FastifyPluginAsync } from 'fastify';
import { createHash } from 'crypto';
import { FixturesRepository } from '../repositories/fixtures.repository.js';
import { checkDatabaseConnection } from '../db/index.js';
import { createResponseEnvelope, createErrorEnvelope } from './envelope.js';
import { RequestGateway } from '../gateway/request-gateway.js';

export const apiRoutes: FastifyPluginAsync = async (fastify: FastifyInstance) => {
  const fixturesRepo = new FixturesRepository();

  // ----------------------------------------------------
  // Health check endpoint (backward-compatible & enhanced)
  // ----------------------------------------------------
  fastify.get('/health', async (request, reply) => {
    const dbStatus = await checkDatabaseConnection();
    const scheduler: any = (fastify as any).scheduler;
    const schedulerDiag = scheduler ? scheduler.getDiagnostics() : null;

    return reply.status(200).send({
      status: 'ok',
      database: dbStatus.connected ? 'connected' : 'disconnected',
      latencyMs: dbStatus.latencyMs,
      timestamp: new Date().toISOString(),
      version: '1.0.0',
      provider: process.env.FOOTBALL_PROVIDER || 'mock',
      scheduler: schedulerDiag
        ? {
            running: schedulerDiag.running,
            lastCycleOutcome: schedulerDiag.lastCycleOutcome,
          }
        : {
            running: false,
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
    const budgetDiag = await gateway.getDiagnostics(process.env.FOOTBALL_PROVIDER || 'mock');
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
      scheduler: schedulerDiag ?? {
        running: false,
        executingCycle: false,
        lastCycleAt: null,
        lastCycleAction: null,
        lastCycleDurationMs: null,
        lastCycleOutcome: null,
        consecutiveErrors: 0,
        nextRecommendedWakeAt: null,
      },
      budget: budgetDiag,
      timestamp: new Date().toISOString(),
    });
  });

  // ----------------------------------------------------
  // Tray Matches Endpoint (supports ETag / 304)
  // ----------------------------------------------------
  fastify.get('/api/tray', async (request, reply) => {
    try {
      const matches = await fixturesRepo.getTrayMatches(10);

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
};
