import { IDataProvider } from './types.js';
import { FotmobProvider } from './fotmob/fotmob.provider.js';
import { ApiFootballProvider } from './api-football/api-football.provider.js';

export type ProviderType = 'fotmob' | 'api-football' | 'api_football';

export interface AppConfig {
  provider: ProviderType;
  apiFootballApiKey?: string;
  apiFootballTier?: string;
}

export class ProviderFactory {
  private static instances = new Map<string, IDataProvider>();
  private static lastConfig: AppConfig | null = null;

  public static createProvider(config: AppConfig): IDataProvider {
    this.lastConfig = { ...config };
    
    const isApiFootball = config.provider === 'api-football' || config.provider === 'api_football';
    if (isApiFootball && config.apiFootballApiKey) {
      const key = `api-football_${config.apiFootballApiKey}_${config.apiFootballTier || 'default'}`;
      if (!this.instances.has(key)) {
        this.instances.set(key, new ApiFootballProvider(config.apiFootballApiKey, config.apiFootballTier));
      }
      return this.instances.get(key)!;
    }

    const fotmobKey = 'fotmob';
    if (!this.instances.has(fotmobKey)) {
      this.instances.set(fotmobKey, new FotmobProvider());
    }
    return this.instances.get(fotmobKey)!;
  }
}
