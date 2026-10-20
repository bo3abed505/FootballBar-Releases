import { IDataProvider } from './types.js';
import { FotmobProvider } from './fotmob/fotmob.provider.js';
import { ApiFootballProvider } from './api-football/api-football.provider.js';
import { MockFootballProvider } from './mock.provider.js';
import { ResilientFootballProvider } from './resilient.provider.js';

export type ProviderType = 'fotmob' | 'api-football' | 'api_football' | 'mock';

export interface AppConfig {
  provider?: ProviderType | string;
  apiFootballApiKey?: string;
  apiFootballTier?: string;
  primary?: FotmobProvider;
  fallback?: ApiFootballProvider;
}

export class ProviderFactory {
  private static instances = new Map<string, any>();
  private static lastConfig: AppConfig | null = null;

  public static createProvider(config?: AppConfig): IDataProvider & any {
    const rawProvider = (config?.provider ?? 'fotmob').trim().toLowerCase();
    this.lastConfig = { ...config, provider: rawProvider as ProviderType };

    if (rawProvider === 'mock') {
      const mockKey = 'mock';
      if (!this.instances.has(mockKey)) {
        this.instances.set(mockKey, new MockFootballProvider() as any);
      }
      return this.instances.get(mockKey)!;
    }

    if (rawProvider === 'api-football' || rawProvider === 'api_football') {
      const apiKey = config?.apiFootballApiKey || process.env.API_FOOTBALL_KEY;
      if (apiKey) {
        const key = `api-football_${apiKey}_${config?.apiFootballTier || process.env.API_FOOTBALL_TIER || 'default'}`;
        if (!this.instances.has(key)) {
          this.instances.set(key, new ApiFootballProvider(apiKey, config?.apiFootballTier || process.env.API_FOOTBALL_TIER));
        }
        return this.instances.get(key)!;
      }
    }

    // Default authoritative primary: ResilientFootballProvider (FotMob primary + automated fallback)
    const resilientKey = 'fotmob_resilient';
    if (!this.instances.has(resilientKey)) {
      this.instances.set(
        resilientKey,
        new ResilientFootballProvider({
          primary: config?.primary,
          fallback: config?.fallback,
          apiFootballApiKey: config?.apiFootballApiKey || process.env.API_FOOTBALL_KEY,
          apiFootballTier: config?.apiFootballTier || process.env.API_FOOTBALL_TIER,
        })
      );
    }
    return this.instances.get(resilientKey)!;
  }

  public static clearInstances(): void {
    this.instances.clear();
    this.lastConfig = null;
  }
}
