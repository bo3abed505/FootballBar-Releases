import { IDataProvider } from '../providers/types.js';
import { FootballProvider } from '../providers/football-provider.js';
import { MockFootballProvider } from '../providers/mock.provider.js';
import { ApiFootballProvider } from '../providers/api-football/api-football.provider.js';
import { ProviderFactory } from '../providers/provider.factory.js';

export type SupportedProvider = 'mock' | 'api_football' | 'fotmob';

export class ProviderConfigError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ProviderConfigError';
  }
}

export interface ProviderFactoryOptions {
  activeProvider?: string;
  provider?: string;
  allowLiveRequests?: string | boolean;
  apiKeyPresent?: boolean;
  apiFootballApiKey?: string;
  apiFootballTier?: string;
}

export function createProvider(options?: ProviderFactoryOptions): IDataProvider & FootballProvider | any {
  const hasActiveProviderOption = !!options && 'activeProvider' in options;
  const rawProvider = (
    hasActiveProviderOption
      ? options.activeProvider ?? 'fotmob'
      : options?.provider ?? process.env.ACTIVE_PROVIDER ?? process.env.FOOTBALL_PROVIDER ?? 'fotmob'
  ).trim().toLowerCase();

  switch (rawProvider) {
    case 'mock':
      return new MockFootballProvider();

    case 'api_football':
    case 'api-football': {
      const hasAllowLiveOption = options && 'allowLiveRequests' in options;
      const allowLive = hasAllowLiveOption
        ? String(options.allowLiveRequests ?? 'false')
        : process.env.ALLOW_LIVE_PROVIDER_REQUESTS ?? 'true';

      const hasApiKeyPresentOption = options && 'apiKeyPresent' in options;
      const keyIsPresent = hasApiKeyPresentOption
        ? (options.apiKeyPresent ?? false)
        : (typeof (options?.apiFootballApiKey ?? process.env.API_FOOTBALL_KEY) === 'string' &&
          (options?.apiFootballApiKey ?? process.env.API_FOOTBALL_KEY)!.trim().length > 0);

      if (keyIsPresent && allowLive.trim().toLowerCase() === 'true') {
        const apiKey = options?.apiFootballApiKey || process.env.API_FOOTBALL_KEY || 'test_key';
        const tier = options?.apiFootballTier || process.env.API_FOOTBALL_TIER;
        return new ApiFootballProvider(apiKey, tier);
      }

      // If no valid key or not allowed, fallback gracefully to consolidated FotMob
      return ProviderFactory.createProvider({
        provider: 'fotmob',
        apiFootballApiKey: options?.apiFootballApiKey,
        apiFootballTier: options?.apiFootballTier,
      });
    }

    case 'fotmob':
    default:
      return ProviderFactory.createProvider({
        provider: 'fotmob',
        apiFootballApiKey: options?.apiFootballApiKey,
        apiFootballTier: options?.apiFootballTier,
      });
  }
}
