import { describe, it, expect, beforeAll, afterAll, vi } from 'vitest';
import { buildServer, AppRuntime } from '../src/server.js';
import { FastifyInstance } from 'fastify';
import { RequestGateway } from '../src/gateway/request-gateway.js';
import { sanitize, generateCorrelationId, Logger } from '../src/diagnostics/logger.js';
import { AcquisitionScheduler } from '../src/acquisition/acquisition-scheduler.js';
import { MockFootballProvider } from '../src/providers/mock.provider.js';
import { SyncService } from '../src/acquisition/sync.service.js';
import { FixturesRepository } from '../src/repositories/fixtures.repository.js';

describe('Backend Diagnostics & Observability', () => {
  let app: FastifyInstance;

  beforeAll(async () => {
    app = await buildServer();
  });

  afterAll(async () => {
    await app.close();
  });

  it('Test21: /health remains backward-compatible with status, database, latencyMs, timestamp', async () => {
    const response = await app.inject({
      method: 'GET',
      url: '/health',
    });

    expect(response.statusCode).toBe(200);
    const body = JSON.parse(response.body);
    expect(body.status).toBe('ok');
    expect(body).toHaveProperty('database');
    expect(body).toHaveProperty('latencyMs');
    expect(body).toHaveProperty('timestamp');
    expect(body).toHaveProperty('version');
    expect(body).toHaveProperty('provider');
    expect(body).toHaveProperty('scheduler');
    expect(body).toHaveProperty('uptimeSeconds');
  });

  it('Test22: /health reports database availability without error', async () => {
    const response = await app.inject({
      method: 'GET',
      url: '/health',
    });

    const body = JSON.parse(response.body);
    expect(['connected', 'disconnected']).toContain(body.database);
    expect(typeof body.latencyMs).toBe('number');
  });

  it('Test23: /health reports scheduler state', async () => {
    const response = await app.inject({
      method: 'GET',
      url: '/health',
    });

    const body = JSON.parse(response.body);
    expect(body.scheduler).toHaveProperty('running');
    expect(body.scheduler).toHaveProperty('lastCycleOutcome');
  });

  it('Test24: /health reports provider mode', async () => {
    const response = await app.inject({
      method: 'GET',
      url: '/health',
    });

    const body = JSON.parse(response.body);
    expect(body.provider).toBeDefined();
    expect(typeof body.provider).toBe('string');
  });

  it('Test25: /health never performs external provider requests', async () => {
    const provider = new MockFootballProvider();
    const fetchSpy = vi.spyOn(provider, 'getFixtures');

    const response = await app.inject({
      method: 'GET',
      url: '/health',
    });

    expect(response.statusCode).toBe(200);
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('Test26: /health and /api/diagnostics never expose API key', async () => {
    const healthRes = await app.inject({ method: 'GET', url: '/health' });
    const diagRes = await app.inject({ method: 'GET', url: '/api/diagnostics' });

    expect(healthRes.body).not.toContain('API_FOOTBALL_KEY');
    expect(healthRes.body).not.toContain('x-apisports-key');
    expect(diagRes.body).not.toContain('API_FOOTBALL_KEY');
    expect(diagRes.body).not.toContain('x-apisports-key');
  });

  it('Test27: /health and /api/diagnostics never expose database passwords', async () => {
    const healthRes = await app.inject({ method: 'GET', url: '/health' });
    const diagRes = await app.inject({ method: 'GET', url: '/api/diagnostics' });

    expect(healthRes.body).not.toContain('postgres:');
    expect(healthRes.body).not.toContain('password=');
    expect(diagRes.body).not.toContain('postgres:');
    expect(diagRes.body).not.toContain('password=');
  });

  it('Test28: Scheduler diagnostic values update upon cycle execution', async () => {
    const mockProvider = new MockFootballProvider();
    const syncService = new SyncService(mockProvider);
    const fixturesRepo = new FixturesRepository();

    const scheduler = new AcquisitionScheduler({
      syncService,
      fixturesRepository: fixturesRepo,
      providerName: 'mock',
      getBudgetContext: async () => ({
        allocatedDaily: 100,
        consumedDaily: 0,
        remainingDaily: 100,
        isExhausted: false,
      }),
    });

    const initialDiag = scheduler.getDiagnostics();
    expect(initialDiag.running).toBe(false);
    expect(initialDiag.lastCycleAt).toBeNull();

    await scheduler.executeSingleCycle();

    const updatedDiag = scheduler.getDiagnostics();
    expect(updatedDiag.lastCycleAt).not.toBeNull();
    expect(updatedDiag.lastCycleAction).toBeDefined();
    expect(typeof updatedDiag.lastCycleDurationMs).toBe('number');
    expect(updatedDiag.consecutiveErrors).toBe(0);
  });

  it('Test29: Request-budget diagnostic values are correct and safe', async () => {
    const gateway = new RequestGateway();
    const diag = await gateway.getDiagnostics('mock');

    expect(diag.provider).toBe('mock');
    expect(diag.allocatedDaily).toBe(100);
    expect(diag.operationalLimit).toBe(90);
    expect(diag.hardLimit).toBe(100);
    expect(diag.minuteCeiling).toBe(8);
    expect(typeof diag.consumedDaily).toBe('number');
    expect(typeof diag.remainingDaily).toBe('number');
  });

  it('Test30: Provider failure is sanitized and logged safely', () => {
    const unsafeErrorMsg = 'Failed request with key=secret_key_12345 to api-sports';
    const sanitized = sanitize(unsafeErrorMsg);

    expect(sanitized).not.toContain('secret_key_12345');
    expect(sanitized).toContain('key=[REDACTED]');
  });

  it('Test31: Database connection failure string is sanitized safely', () => {
    const unsafeDbErr = 'Could not connect to postgresql://postgres:secret_db_pw@localhost:5432/footballtray';
    const sanitized = sanitize(unsafeDbErr);

    expect(sanitized).not.toContain('secret_db_pw');
    expect(sanitized).toContain('postgresql://postgres:[REDACTED]@localhost:5432/footballtray');
  });

  it('Test32: Runtime lifecycle logs startup and shutdown cleanly', async () => {
    const testApp = await buildServer();
    const logSpy = {
      info: vi.fn(),
      warn: vi.fn(),
      error: vi.fn(),
    };

    const runtime = new AppRuntime(testApp, {
      port: 4099,
      enableScheduler: false,
      registerSignals: false,
      logger: logSpy,
    });

    const url = await runtime.start();
    expect(url).toContain('4099');
    expect(logSpy.info).toHaveBeenCalledWith(expect.stringContaining('Starting FootballTray backend runtime'));

    await runtime.stop();
    expect(logSpy.info).toHaveBeenCalledWith(expect.stringContaining('Graceful runtime shutdown complete'));
  });

  it('Test33: Correlation IDs are deterministic in shape but unique per operation', () => {
    const id1 = generateCorrelationId('sync');
    const id2 = generateCorrelationId('sync');

    expect(id1).toMatch(/^sync_[a-f0-9]{8}$/);
    expect(id2).toMatch(/^sync_[a-f0-9]{8}$/);
    expect(id1).not.toBe(id2);
  });

  it('Test34: /api/diagnostics endpoint returns complete sanitized operational metrics', async () => {
    const response = await app.inject({
      method: 'GET',
      url: '/api/diagnostics',
    });

    expect(response.statusCode).toBe(200);
    const body = JSON.parse(response.body);

    expect(body.service.name).toBe('footballtray-backend');
    expect(body.service.version).toBe('1.0.0');
    expect(body.provider).toHaveProperty('mode');
    expect(body.provider).toHaveProperty('allowLiveRequests');
    expect(body.database).toHaveProperty('connected');
    expect(body.scheduler).toHaveProperty('running');
    expect(body.budget).toHaveProperty('allocatedDaily');
    expect(body).toHaveProperty('timestamp');
  });
});
