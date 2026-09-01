import { describe, it, expect } from 'vitest';
import { createProvider, ProviderConfigError } from '../src/acquisition/provider-factory.js';
import { parseCompetitionIds, CompetitionConfigError } from '../src/acquisition/competition-config.js';
import { MockFootballProvider } from '../src/providers/mock.provider.js';
import { ApiFootballProvider } from '../src/providers/api-football/api-football.provider.js';

// ============================================================
// ProviderFactory tests
// All tests use the options injection API — no real environment
// variables are read or mutated and no network requests are made.
// ============================================================
describe('ProviderFactory.createProvider()', () => {
  it('defaults to MockFootballProvider when ACTIVE_PROVIDER is absent', () => {
    const provider = createProvider({ activeProvider: undefined });
    expect(provider).toBeInstanceOf(MockFootballProvider);
    expect(provider.providerName).toBe('mock');
  });

  it('returns MockFootballProvider for ACTIVE_PROVIDER=mock', () => {
    const provider = createProvider({ activeProvider: 'mock' });
    expect(provider).toBeInstanceOf(MockFootballProvider);
  });

  it('returns MockFootballProvider for ACTIVE_PROVIDER=MOCK (case-insensitive)', () => {
    const provider = createProvider({ activeProvider: 'MOCK' });
    expect(provider).toBeInstanceOf(MockFootballProvider);
  });

  it('trims whitespace around ACTIVE_PROVIDER value', () => {
    const provider = createProvider({ activeProvider: '  mock  ' });
    expect(provider).toBeInstanceOf(MockFootballProvider);
  });

  it('throws ProviderConfigError for an unsupported provider value', () => {
    expect(() => createProvider({ activeProvider: 'excel' })).toThrow(ProviderConfigError);
    expect(() => createProvider({ activeProvider: 'excel' })).toThrow(/excel/);
    expect(() => createProvider({ activeProvider: 'excel' })).toThrow(/Supported values/);
  });

  it('throws ProviderConfigError for empty string provider', () => {
    // empty string normalizes to an empty string that matches no case
    expect(() => createProvider({ activeProvider: '' })).toThrow(ProviderConfigError);
  });

  // ------------------------------------------------------------------
  // api_football provider — safety guard enforcement
  // ------------------------------------------------------------------
  it('throws ProviderConfigError for api_football when ALLOW_LIVE_PROVIDER_REQUESTS is missing/false', () => {
    expect(() =>
      createProvider({
        activeProvider: 'api_football',
        allowLiveRequests: 'false',
        apiKeyPresent: true,
      })
    ).toThrow(ProviderConfigError);

    expect(() =>
      createProvider({
        activeProvider: 'api_football',
        allowLiveRequests: undefined,
        apiKeyPresent: true,
      })
    ).toThrow(ProviderConfigError);
  });

  it('throws ProviderConfigError for api_football when API key is absent', () => {
    expect(() =>
      createProvider({
        activeProvider: 'api_football',
        allowLiveRequests: 'true',
        apiKeyPresent: false,
      })
    ).toThrow(ProviderConfigError);

    const err = (() => {
      try {
        createProvider({
          activeProvider: 'api_football',
          allowLiveRequests: 'true',
          apiKeyPresent: false,
        });
      } catch (e) {
        return e as Error;
      }
    })();
    expect(err?.message).toMatch(/API_FOOTBALL_KEY/);
    // API key VALUE must not appear in the error message
    expect(err?.message).not.toMatch(/[a-f0-9]{32}/i); // no hex key
  });

  it('returns ApiFootballProvider when ALLOW_LIVE_PROVIDER_REQUESTS=true and key is present', () => {
    const provider = createProvider({
      activeProvider: 'api_football',
      allowLiveRequests: 'true',
      apiKeyPresent: true,
    });
    expect(provider).toBeInstanceOf(ApiFootballProvider);
    expect(provider.providerName).toBe('api_football');
  });

  it('error message for api_football without live flag does not contain the API key', () => {
    // Even when env happens to have a key, the error must not expose it.
    let errorMsg = '';
    try {
      createProvider({
        activeProvider: 'api_football',
        allowLiveRequests: 'false',
        apiKeyPresent: true,
      });
    } catch (e: any) {
      errorMsg = e.message;
    }
    // Must mention how to fix it
    expect(errorMsg).toMatch(/ALLOW_LIVE_PROVIDER_REQUESTS/);
    // Must NOT contain what looks like a real key
    expect(errorMsg).not.toMatch(/[a-f0-9]{20,}/i);
  });

  it('never silently falls back from api_football to mock', () => {
    // This is the core "fail closed" assertion.
    // Both bad configurations must throw, not silently return a mock.
    const badConfigs = [
      { activeProvider: 'api_football', allowLiveRequests: 'false', apiKeyPresent: false },
      { activeProvider: 'api_football', allowLiveRequests: 'true', apiKeyPresent: false },
      { activeProvider: 'api_football', allowLiveRequests: 'false', apiKeyPresent: true },
    ];

    for (const cfg of badConfigs) {
      let thrown = false;
      let result: unknown = null;
      try {
        result = createProvider(cfg);
      } catch {
        thrown = true;
      }
      expect(thrown, `Expected throw for config: ${JSON.stringify(cfg)}`).toBe(true);
      expect(result).toBeNull();
    }
  });
});

// ============================================================
// Competition allowlist parser tests
// Pure function — no environment, no I/O, no DB.
// ============================================================
describe('parseCompetitionIds()', () => {
  it('returns empty array for undefined input', () => {
    expect(parseCompetitionIds(undefined)).toEqual([]);
  });

  it('returns empty array for empty string', () => {
    expect(parseCompetitionIds('')).toEqual([]);
  });

  it('returns empty array for whitespace-only string', () => {
    expect(parseCompetitionIds('   ')).toEqual([]);
  });

  it('parses a single valid ID', () => {
    expect(parseCompetitionIds('39')).toEqual([39]);
  });

  it('parses multiple valid IDs', () => {
    expect(parseCompetitionIds('39,140,233')).toEqual([39, 140, 233]);
  });

  it('trims whitespace around each entry', () => {
    expect(parseCompetitionIds(' 39 , 140 , 233 ')).toEqual([39, 140, 233]);
  });

  it('ignores empty entries from trailing commas', () => {
    expect(parseCompetitionIds('39,140,')).toEqual([39, 140]);
  });

  it('ignores empty entries from leading commas', () => {
    expect(parseCompetitionIds(',39,140')).toEqual([39, 140]);
  });

  it('deduplicates repeated IDs (preserves first occurrence order)', () => {
    expect(parseCompetitionIds('39,140,39,233,140')).toEqual([39, 140, 233]);
  });

  it('throws CompetitionConfigError for a non-numeric entry', () => {
    expect(() => parseCompetitionIds('39,abc,140')).toThrow(CompetitionConfigError);
    expect(() => parseCompetitionIds('39,abc,140')).toThrow(/abc/);
    expect(() => parseCompetitionIds('39,abc,140')).toThrow(/invalid/i);
  });

  it('throws CompetitionConfigError for a negative integer', () => {
    expect(() => parseCompetitionIds('-1')).toThrow(CompetitionConfigError);
  });

  it('throws CompetitionConfigError for zero', () => {
    expect(() => parseCompetitionIds('0')).toThrow(CompetitionConfigError);
  });

  it('throws CompetitionConfigError for a decimal number', () => {
    expect(() => parseCompetitionIds('39.5')).toThrow(CompetitionConfigError);
  });

  it('throws CompetitionConfigError for a floating-point zero-decimal (e.g. "39.0")', () => {
    // "39.0" is not equal to String(39), so it must be rejected.
    expect(() => parseCompetitionIds('39.0')).toThrow(CompetitionConfigError);
  });

  it('throws CompetitionConfigError for a hex string', () => {
    expect(() => parseCompetitionIds('0x27')).toThrow(CompetitionConfigError);
  });

  it('produces the six target competition IDs from the documented example', () => {
    const result = parseCompetitionIds('39,140,233,2,6,1');
    expect(result).toEqual([39, 140, 233, 2, 6, 1]);
    expect(result).toHaveLength(6);
  });
});

// ============================================================
// Pipeline Filter Propagation Tests
// Verifies that AcquisitionService and SyncService pass filters
// down to the provider without dropping them.
// ============================================================
describe('Acquisition & Sync Pipeline Filter Forwarding', () => {
  it('AcquisitionService.acquireAll() passes FixtureFilter to provider.getFixtures()', async () => {
    let receivedFilter: any = null;

    const mockProvider: any = {
      providerName: 'mock_test',
      getFixtures: async (filter?: any) => {
        receivedFilter = filter;
        return [];
      },
    };

    const { AcquisitionService } = await import('../src/acquisition/acquisition.service.js');
    const acqService = new AcquisitionService(mockProvider);

    const testFilter = { competitionIds: [39, 140] };
    await acqService.acquireAll(testFilter);

    expect(receivedFilter).toEqual(testFilter);
  });

  it('AcquisitionService.acquireAll() forwards undefined when no filter is provided', async () => {
    let receivedFilter: any = 'not_called';

    const mockProvider: any = {
      providerName: 'mock_test',
      getFixtures: async (filter?: any) => {
        receivedFilter = filter;
        return [];
      },
    };

    const { AcquisitionService } = await import('../src/acquisition/acquisition.service.js');
    const acqService = new AcquisitionService(mockProvider);

    await acqService.acquireAll();

    expect(receivedFilter).toBeUndefined();
  });

  it('SyncService.sync() forwards FixtureFilter to AcquisitionService', async () => {
    let receivedFilter: any = null;

    const mockProvider: any = {
      providerName: 'mock_test',
      getFixtures: async (filter?: any) => {
        receivedFilter = filter;
        return [];
      },
    };

    const { SyncService } = await import('../src/acquisition/sync.service.js');
    const syncService = new SyncService(mockProvider);

    const testFilter = { competitionIds: [39, 140] };
    const summary = await syncService.sync(testFilter);

    expect(receivedFilter).toEqual(testFilter);
    expect(summary.provider).toBe('mock_test');
    expect(summary.fixturesDiscovered).toBe(0);
  });
});

