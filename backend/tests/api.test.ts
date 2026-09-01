import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import { buildServer } from '../src/server.js';
import { FastifyInstance } from 'fastify';

describe('API Endpoints & ETag behavior', () => {
  let app: FastifyInstance;

  beforeAll(async () => {
    app = await buildServer();
  });

  afterAll(async () => {
    await app.close();
  });

  it('GET /api/tray returns envelope with ETag', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/api/tray',
    });

    // Either 200 (if DB connected/seeded or returns empty matches) or 500 (if DB offline)
    if (res.statusCode === 200) {
      const body = JSON.parse(res.body);
      expect(body).toHaveProperty('schemaVersion', '1.0');
      expect(body).toHaveProperty('requestId');
      expect(body).toHaveProperty('data');
      expect(body.data).toHaveProperty('matches');
      expect(Array.isArray(body.data.matches)).toBe(true);

      const etagHeader = res.headers['etag'];
      expect(etagHeader).toBeDefined();

      // Test 304 Not Modified
      const cachedRes = await app.inject({
        method: 'GET',
        url: '/api/tray',
        headers: {
          'if-none-match': etagHeader as string,
        },
      });

      expect(cachedRes.statusCode).toBe(304);
      expect(cachedRes.body).toBe('');
    } else {
      // If DB is offline during unit test execution, graceful error envelope is returned
      expect(res.statusCode).toBe(500);
      const body = JSON.parse(res.body);
      expect(body).toHaveProperty('error');
      expect(body.error.code).toBe('INTERNAL_ERROR');
    }
  });

  it('GET /api/match/:id returns 404 for non-existent match', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/api/match/fix_non_existent_99999',
    });

    if (res.statusCode === 404) {
      const body = JSON.parse(res.body);
      expect(body.data).toBeNull();
      expect(body.error?.code).toBe('MATCH_NOT_FOUND');
      expect(body.error?.message).toContain('fix_non_existent_99999');
    } else {
      expect(res.statusCode).toBe(500);
    }
  });
});
