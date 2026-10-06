/**
 * provider-factory.ts
 *
 * Constructs the active FootballProvider based on the ACTIVE_PROVIDER
 * environment variable.
 *
 * Supported values for ACTIVE_PROVIDER:
 *   mock        — MockFootballProvider (default when variable is absent)
 *   api_football — ApiFootballProvider routed through RequestGateway
 *
 * Safety rules for api_football:
 *   - API_FOOTBALL_KEY must be set to a non-empty string.
 *   - ALLOW_LIVE_PROVIDER_REQUESTS must equal "true".
 *   - These checks occur BEFORE the provider object is constructed so no
 *     network request is possible if the guard fails.
 *   - The API key is never logged or included in error messages.
 */

import { FootballProvider } from '../providers/football-provider.js';
import { MockFootballProvider } from '../providers/mock.provider.js';
import { ApiFootballProvider } from '../providers/api-football/api-football.provider.js';
import { EmptyFootballProvider } from '../providers/empty.provider.js';

export type SupportedProvider = 'mock' | 'api_football';

/** Thrown when configuration prevents a provider from being constructed safely. */
export class ProviderConfigError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ProviderConfigError';
  }
}

export interface ProviderFactoryOptions {
  /**
   * Override ACTIVE_PROVIDER. When omitted the factory reads process.env.
   * Useful in tests to avoid polluting the real environment.
   */
  activeProvider?: string;
  /**
   * Override ALLOW_LIVE_PROVIDER_REQUESTS. When omitted reads process.env.
   */
  allowLiveRequests?: string;
  /**
   * Override API_FOOTBALL_KEY presence check. When omitted reads process.env.
   * IMPORTANT: never pass the actual key value here; the factory only checks
   * whether a value exists, not what it is.
   */
  apiKeyPresent?: boolean;
}

/**
 * Reads ACTIVE_PROVIDER and creates the matching FootballProvider.
 *
 * Fails loudly for unknown providers or unsafe api_football configuration.
 * Never silently falls back from api_football to mock.
 */
export function createProvider(options?: ProviderFactoryOptions): FootballProvider {
  const isProduction = process.env.NODE_ENV === 'production';
  const hasActiveProviderOption = options && 'activeProvider' in options;
  const rawProvider = (
    hasActiveProviderOption
      ? options.activeProvider ?? (isProduction ? 'api_football' : 'mock')
      : process.env.ACTIVE_PROVIDER ?? (isProduction ? 'api_football' : 'mock')
  ).trim().toLowerCase();

  // Sever the mock fallback in production
  if (isProduction && (rawProvider === 'mock' || !hasActiveProviderOption)) {
    const keyIsPresent = typeof process.env.API_FOOTBALL_KEY === 'string' &&
      process.env.API_FOOTBALL_KEY.trim().length > 0;

    if (!keyIsPresent) {
      console.warn(
        '[PROVIDER:WARN] API_FOOTBALL_KEY is not configured in production. ' +
        'External match acquisition is disabled. Operating with an empty active calendar; ' +
        'mock provider fallback is completely disabled.'
      );
      return new EmptyFootballProvider();
    }
    return new ApiFootballProvider();
  }

  switch (rawProvider) {
    case 'mock':
      if (isProduction || (process.env.ALLOW_DEV_MOCKS !== 'true' && process.env.NODE_ENV !== 'test' && !process.env.VITEST)) {
        console.warn(
          '[PROVIDER:WARN] Mock provider requested without ALLOW_DEV_MOCKS=true. ' +
          'Mock provider is strictly disabled. Operating with an empty active calendar.'
        );
        return new EmptyFootballProvider();
      }
      return new MockFootballProvider();

    case 'api_football': {
      // --- Live-request safety guard ---
      // Must check BEFORE constructing ApiFootballProvider so zero HTTP I/O
      // can occur if the guard fails.
      const hasAllowLiveOption = options && 'allowLiveRequests' in options;
      const allowLive = hasAllowLiveOption
        ? options.allowLiveRequests ?? 'false'
        : process.env.ALLOW_LIVE_PROVIDER_REQUESTS ?? 'false';

      if (allowLive.trim().toLowerCase() !== 'true') {
        throw new ProviderConfigError(
          'ACTIVE_PROVIDER=api_football requires ALLOW_LIVE_PROVIDER_REQUESTS=true. ' +
          'Set this variable explicitly when you intend to make real API-Football requests. ' +
          'For development and testing use ACTIVE_PROVIDER=mock.'
        );
      }

      // Check API key presence (never log or expose the key value).
      const hasApiKeyPresentOption = options && 'apiKeyPresent' in options;
      const keyIsPresent = hasApiKeyPresentOption
        ? options.apiKeyPresent ?? false
        : typeof process.env.API_FOOTBALL_KEY === 'string' &&
          process.env.API_FOOTBALL_KEY.trim().length > 0;

      if (!keyIsPresent) {
        if (isProduction) {
          console.warn(
            '[PROVIDER:WARN] API_FOOTBALL_KEY is missing. ' +
            'Operating with an empty active calendar; mock provider fallback is completely disabled.'
          );
          return new EmptyFootballProvider();
        }
        throw new ProviderConfigError(
          'ACTIVE_PROVIDER=api_football requires API_FOOTBALL_KEY to be set in the environment. ' +
          'Obtain a key from https://www.api-football.com/ and add it to your .env file.'
        );
      }

      return new ApiFootballProvider();
    }

    default:
      throw new ProviderConfigError(
        `Unknown ACTIVE_PROVIDER value: "${rawProvider}". ` +
        `Supported values are: "mock", "api_football".`
      );
  }
}
