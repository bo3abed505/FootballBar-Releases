import { describe, it, expect, beforeAll, afterAll, vi } from 'vitest';
import { buildServer, AppRuntime } from '../src/server.js';
import { FastifyInstance } from 'fastify';
import { RequestGateway } from '../src/gateway/request-gateway.js';
import { sanitize, sanitizeMeta, generateCorrelationId, Logger } from '../src/diagnostics/logger.js';
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

  // ============================================================
  // Milestone 15 Security Hardening — Logger Metadata Sanitization
  // ============================================================
  describe('Milestone 15 Security Hardening — Logger Metadata Sanitization', () => {
    it('Test A: Sensitive top-level metadata keys are redacted', () => {
      const sensitiveKeys = [
        'password',
        'passwd',
        'secret',
        'token',
        'access_token',
        'refresh_token',
        'api_key',
        'apikey',
        'authorization',
        'cookie',
        'set-cookie',
        'client_secret',
        'Password',
        'API_KEY',
        'Authorization',
        'Set-Cookie',
        'refreshToken',
      ];

      const input: Record<string, unknown> = {};
      for (const key of sensitiveKeys) {
        input[key] = `sensitive_value_for_${key}`;
      }

      const result = sanitizeMeta(input) as Record<string, unknown>;

      for (const key of sensitiveKeys) {
        expect(result[key]).toBe('[REDACTED]');
      }
    });

    it('Test B: Sensitive nested metadata keys are redacted', () => {
      const nestedInput = {
        app: 'FootballTray',
        config: {
          auth: {
            password: 'super_secret_password',
            token: 'jwt.token.value',
            inner: {
              api_key: 'nested_api_key_val',
              client_secret: 'nested_client_secret',
            },
          },
        },
      };

      const result = sanitizeMeta(nestedInput) as any;

      expect(result.app).toBe('FootballTray');
      expect(result.config.auth.password).toBe('[REDACTED]');
      expect(result.config.auth.token).toBe('[REDACTED]');
      expect(result.config.auth.inner.api_key).toBe('[REDACTED]');
      expect(result.config.auth.inner.client_secret).toBe('[REDACTED]');
    });

    it('Test C: Sensitive values inside metadata strings are redacted', () => {
      const metaWithStringSecrets = {
        connectionString: 'postgresql://postgres:mySecretPassword123@localhost:5432/footballtray',
        authHeader: 'Bearer my_access_token_abc123',
        apiUrl: 'https://v3.football.api-sports.io/fixtures?key=secret_api_key_xyz',
        cookieHeader: 'session_id=12345; password=plaintext_pass',
      };

      const result = sanitizeMeta(metaWithStringSecrets) as any;

      expect(result.connectionString).not.toContain('mySecretPassword123');
      expect(result.connectionString).toBe('postgresql://postgres:[REDACTED]@localhost:5432/footballtray');

      expect(result.authHeader).not.toContain('my_access_token_abc123');
      expect(result.authHeader).toBe('Bearer [REDACTED]');

      expect(result.apiUrl).not.toContain('secret_api_key_xyz');
      expect(result.apiUrl).toContain('key=[REDACTED]');

      expect(result.cookieHeader).not.toContain('plaintext_pass');
      expect(result.cookieHeader).toContain('password=[REDACTED]');
    });

    it('Test D: Arrays and complex nested structures are handled recursively', () => {
      const complexMeta = {
        batches: [
          {
            batchId: 1,
            credentials: [{ token: 'tok_abc' }, { password: 'pwd_123' }],
          },
          [
            'normal_entry',
            'postgresql://user:pass456@db:5432/prod',
            { secret: 'deep_secret' },
          ],
        ],
      };

      const result = sanitizeMeta(complexMeta) as any;

      expect(result.batches[0].batchId).toBe(1);
      expect(result.batches[0].credentials[0].token).toBe('[REDACTED]');
      expect(result.batches[0].credentials[1].password).toBe('[REDACTED]');
      expect(result.batches[1][0]).toBe('normal_entry');
      expect(result.batches[1][1]).toBe('postgresql://user:[REDACTED]@db:5432/prod');
      expect(result.batches[1][2].secret).toBe('[REDACTED]');
    });

    it('Test E: Original metadata object is not mutated', () => {
      const originalMeta = {
        password: 'unmutated_password',
        nested: {
          token: 'unmutated_token',
          list: [{ api_key: 'unmutated_key' }],
        },
      };

      const originalSnapshot = JSON.parse(JSON.stringify(originalMeta));
      const sanitized = sanitizeMeta(originalMeta) as any;

      // Original must be completely intact and unchanged
      expect(originalMeta).toEqual(originalSnapshot);
      expect(originalMeta.password).toBe('unmutated_password');
      expect(originalMeta.nested.token).toBe('unmutated_token');
      expect(originalMeta.nested.list[0].api_key).toBe('unmutated_key');

      // Returned object is a distinct instance with redacted values
      expect(sanitized).not.toBe(originalMeta);
      expect(sanitized.nested).not.toBe(originalMeta.nested);
      expect(sanitized.nested.list).not.toBe(originalMeta.nested.list);
      expect(sanitized.password).toBe('[REDACTED]');
      expect(sanitized.nested.token).toBe('[REDACTED]');
      expect(sanitized.nested.list[0].api_key).toBe('[REDACTED]');
    });

    it('Test F: Circular metadata does not crash logging', () => {
      const circularObj: any = {
        name: 'circular_structure',
        token: 'secret_token_123',
      };
      circularObj.self = circularObj;
      circularObj.child = {
        parent: circularObj,
      };

      // Direct sanitization should not crash
      let sanitized: any;
      expect(() => {
        sanitized = sanitizeMeta(circularObj);
      }).not.toThrow();

      expect(sanitized.name).toBe('circular_structure');
      expect(sanitized.token).toBe('[REDACTED]');
      expect(sanitized.self).toBe('[Circular]');
      expect(sanitized.child.parent).toBe('[Circular]');

      // Must be safely JSON serializable
      expect(() => JSON.stringify(sanitized)).not.toThrow();

      // Logger method invocation must not throw with circular meta
      const consoleErrorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      expect(() => {
        Logger.error('TestComponent', 'TestCircular', 'Error occurred with circular meta', undefined, circularObj);
      }).not.toThrow();
      expect(consoleErrorSpy).toHaveBeenCalled();
      consoleErrorSpy.mockRestore();
    });

    it('Test G: Ordinary non-sensitive metadata remains intact', () => {
      const ordinaryMeta = {
        fixtureId: 99123,
        status: '1H',
        elapsed: 35,
        homeTeam: 'Arsenal',
        awayTeam: 'Chelsea',
        scores: [2, 1],
        isActive: true,
        extraInfo: {
          season: 2025,
          round: 'Round 26',
        },
      };

      const result = sanitizeMeta(ordinaryMeta);
      expect(result).toEqual(ordinaryMeta);
    });

    it('Test H: Shared and reused references in sibling properties are preserved while genuine cycles are redacted', () => {
      const shared = {
        value: 123,
        details: { team: 'Arsenal', count: 5 },
      };

      const metaWithShared = {
        first: shared,
        second: shared,
        items: [shared, shared],
        genuineCycle: {} as any,
      };
      metaWithShared.genuineCycle.self = metaWithShared.genuineCycle;

      const result = sanitizeMeta(metaWithShared) as any;

      // 1. Sibling occurrences are preserved and not labeled [Circular]
      expect(result.first).toEqual({ value: 123, details: { team: 'Arsenal', count: 5 } });
      expect(result.second).toEqual({ value: 123, details: { team: 'Arsenal', count: 5 } });
      expect(result.first).not.toBe('[Circular]');
      expect(result.second).not.toBe('[Circular]');

      // 2. Array occurrences are also preserved
      expect(result.items[0]).toEqual({ value: 123, details: { team: 'Arsenal', count: 5 } });
      expect(result.items[1]).toEqual({ value: 123, details: { team: 'Arsenal', count: 5 } });
      expect(result.items[0]).not.toBe('[Circular]');
      expect(result.items[1]).not.toBe('[Circular]');

      // 3. Genuine circular reference is still detected and labeled [Circular]
      expect(result.genuineCycle.self).toBe('[Circular]');

      // 4. Must be safely JSON serializable
      expect(() => JSON.stringify(result)).not.toThrow();
    });
  });
});
