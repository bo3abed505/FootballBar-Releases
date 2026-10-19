import { FotmobProvider } from './fotmob.provider.js';
import { MatchStatus } from '../types.js';
import { ProviderChallengeError, ProviderSchemaError, ProviderNetworkError, ProviderResourceNotFoundError } from '../errors.js';
import fs from 'fs';
import path from 'path';
import { describe, it, expect, vi as jest, beforeEach, afterEach } from 'vitest';

describe('FotmobProvider', () => {
  let provider: FotmobProvider;
  
  const matchesJsonPath = path.resolve(__dirname, '../../../test/fixtures/fotmob/matches.json');
  const detailsJsonPath = path.resolve(__dirname, '../../../test/fixtures/fotmob/match-details.json');
  
  const matchesPayload = fs.readFileSync(matchesJsonPath, 'utf-8');
  const detailsPayload = fs.readFileSync(detailsJsonPath, 'utf-8');

  beforeEach(() => {
    provider = new FotmobProvider();
    global.fetch = jest.fn();
  });

  afterEach(() => {
    jest.resetAllMocks();
    jest.useRealTimers();
  });

  it('should fetch and normalize daily matches successfully', async () => {
    (global.fetch as any).mockResolvedValue({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'application/json' }),
      text: async () => matchesPayload
    });

    const matches = await provider.getDailyMatches('20261009');
    
    expect(matches).toHaveLength(1);
    expect(matches[0].providerMatchId).toBe('4193850');
    expect(matches[0].homeTeam.name).toBe('Arsenal');
    expect(matches[0].time.status).toBe(MatchStatus.SECOND_HALF);
    expect(matches[0].time.displayClock).toBe("67'");
  });

  it('should deduplicate live matches across dates during night fixture crossing', async () => {
    // Mock getLiveMatches to return two days (today and yesterday)
    // Both responses will return the same payload (containing match 4193850)
    (global.fetch as any).mockResolvedValue({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'application/json' }),
      text: async () => matchesPayload
    });

    jest.useFakeTimers();
    // 02:00 AM local time
    jest.setSystemTime(new Date(2026, 9, 10, 2, 0, 0));

    const matches = await provider.getLiveMatches();
    
    // Even though fetch was called twice returning the same match, we should only get 1
    expect(matches).toHaveLength(1);
    expect(matches[0].providerMatchId).toBe('4193850');
    expect(global.fetch).toHaveBeenCalledTimes(2);
  });

  it('should fetch and normalize match details successfully', async () => {
    (global.fetch as any).mockResolvedValue({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'application/json' }),
      text: async () => detailsPayload
    });

    const details = await provider.getMatchDetails('4193850');
    
    expect(details.providerMatchId).toBe('4193850');
    expect(details.homeTeam.name).toBe('Arsenal');
    expect(details.stats.Periods.All.stats[0].stats[0].key).toBe('expected_goals');
  });

  it('should throw ProviderNetworkError if fetch times out via AbortController', async () => {
    jest.useFakeTimers();
    (global.fetch as any).mockImplementation((_url: string, options: any) => {
      return new Promise((resolve, reject) => {
        // Listen to the abort signal
        if (options.signal) {
          options.signal.addEventListener('abort', () => {
            // Node native fetch throws a DOMException for AbortSignal
            const err = new DOMException('The operation was aborted.', 'AbortError');
            reject(err);
          });
        }
      });
    });

    const promise = provider.getDailyMatches('20261009');
    
    // Advance timers by 8 seconds to trigger the setTimeout in fetchJson
    jest.advanceTimersByTime(8000);
    
    await expect(promise).rejects.toThrow(ProviderNetworkError);
    await expect(promise).rejects.toThrow('Fotmob fetch timed out after 8000ms');
  });

  it('should throw ProviderSchemaError on malformed JSON', async () => {
    (global.fetch as any).mockResolvedValue({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'application/json' }),
      text: async () => JSON.stringify({ leagues: [{ id: '123', name: 'L', matches: 'not-an-array' }] })
    });

    await expect(provider.getDailyMatches('20261009')).rejects.toThrow(ProviderSchemaError);
  });

  it('should throw ProviderSchemaError when teams array is missing or incomplete', async () => {
    const badDetails = JSON.parse(detailsPayload);
    badDetails.header.teams = [{ id: '123', name: 'Only One Team' }];
    (global.fetch as any).mockResolvedValue({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'application/json' }),
      text: async () => JSON.stringify(badDetails)
    });

    await expect(provider.getMatchDetails('4193850')).rejects.toThrow(ProviderSchemaError);
  });

  it('should throw ProviderChallengeError when receiving HTML block', async () => {
    (global.fetch as any).mockResolvedValue({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'text/html' }),
      text: async () => '\n <!DOCTYPE html><html>Cloudflare Block</html>'
    });

    await expect(provider.getDailyMatches('20261009')).rejects.toThrow(ProviderChallengeError);
  });

  it('should throw ProviderChallengeError on 403 status', async () => {
    (global.fetch as any).mockResolvedValue({
      ok: false,
      status: 403,
      headers: new Headers(),
      text: async () => ''
    });

    await expect(provider.getDailyMatches('20261009')).rejects.toThrow(ProviderChallengeError);
  });
  
  it('should throw ProviderResourceNotFoundError on 404 status', async () => {
    (global.fetch as any).mockResolvedValue({
      ok: false,
      status: 404,
      headers: new Headers(),
      text: async () => ''
    });

    await expect(provider.getDailyMatches('20261009')).rejects.toThrow(ProviderResourceNotFoundError);
  });
  
  it('should throw ProviderNetworkError on other network errors', async () => {
    (global.fetch as any).mockRejectedValue(new Error('ECONNREFUSED'));

    await expect(provider.getDailyMatches('20261009')).rejects.toThrow(ProviderNetworkError);
  });
});
