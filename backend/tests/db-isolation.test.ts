import { describe, it, expect } from 'vitest';
import { pool, isTestEnvironment } from '../src/db/index.js';

describe('Database Isolation & Safety Checks', () => {
  it('identifies environment as test runtime', () => {
    expect(isTestEnvironment).toBe(true);
  });

  it('connects exclusively to the dedicated footballtray_test database', async () => {
    const client = await pool.connect();
    try {
      const res = await client.query('SELECT current_database()');
      const dbName = res.rows[0].current_database;
      expect(dbName).toBe('footballtray_test');
      expect(dbName).not.toBe('footballtray');
    } finally {
      client.release();
    }
  });
});
