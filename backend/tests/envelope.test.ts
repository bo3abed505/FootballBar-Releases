import { describe, it, expect } from 'vitest';
import { createResponseEnvelope, createErrorEnvelope } from '../src/api/envelope.js';

describe('Response Envelope Contract', () => {
  it('creates valid response envelope adhering to API_CONTRACT.md', () => {
    const payload = { test: 'value', count: 42 };
    const envelope = createResponseEnvelope(payload, { version: 2 });

    expect(envelope.schemaVersion).toBe('1.0');
    expect(envelope.requestId).toMatch(/^req_\d+_\d+$/);
    expect(envelope.version).toBe(2);
    expect(envelope.stale).toBe(false);
    expect(envelope.dataAgeSeconds).toBe(0);
    expect(new Date(envelope.generatedAt).getTime()).toBeGreaterThan(0);
    expect(new Date(envelope.serverTime).getTime()).toBeGreaterThan(0);
    expect(envelope.data).toEqual(payload);
  });

  it('creates valid error envelope adhering to API_CONTRACT.md', () => {
    const envelope = createErrorEnvelope('NOT_FOUND', 'Match not found', { id: '123' });

    expect(envelope.schemaVersion).toBe('1.0');
    expect(envelope.requestId).toMatch(/^req_\d+_\d+$/);
    expect(envelope.data).toBeNull();
    expect(envelope.error).toEqual({
      code: 'NOT_FOUND',
      message: 'Match not found',
      details: { id: '123' },
    });
  });
});
