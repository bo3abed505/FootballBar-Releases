/**
 * resilient.provider.ts
 *
 * Resilient Football Data Provider (Directive 2 / Phase 4)
 *
 * Implements IDataProvider with FotmobProvider as the authoritative primary adapter
 * and automated circuit-breaker fallback to ApiFootballProvider (when API_FOOTBALL_KEY
 * is configured in the environment).
 *
 * If FotMob's circuit breaker trips to OPEN:
 *   - Automatically delegates to ApiFootballProvider if available.
 *   - Gracefully returns empty collections without crashing the daemon if no fallback key is configured.
 *   - Resumes FotMob automatically when the circuit breaker half-opens and probe succeeds.
 */

import {
  IDataProvider,
  NormalizedLiveMatch,
  NormalizedMatch,
  NormalizedMatchDetails,
} from './types.js';
import { CircuitBreaker, CircuitState } from '../resilience/circuit-breaker.js';
import { FotmobProvider } from './fotmob/fotmob.provider.js';
import { ApiFootballProvider } from './api-football/api-football.provider.js';

export interface ResilientFootballProviderOptions {
  primary?: FotmobProvider;
  fallback?: ApiFootballProvider;
  apiFootballApiKey?: string;
  apiFootballTier?: string;
  logger?: {
    warn(message: string, meta?: Record<string, unknown>): void;
    info(message: string, meta?: Record<string, unknown>): void;
  };
}

export class ResilientFootballProvider implements IDataProvider {
  public readonly name = 'fotmob';
  public readonly primary: FotmobProvider;
  public readonly fallback: ApiFootballProvider | null;
  private readonly logger: {
    warn(message: string, meta?: Record<string, unknown>): void;
    info(message: string, meta?: Record<string, unknown>): void;
  };

  constructor(options?: ResilientFootballProviderOptions) {
    this.primary = options?.primary ?? new FotmobProvider();

    const apiKey = (options?.apiFootballApiKey ?? process.env.API_FOOTBALL_KEY)?.trim();
    if (options?.fallback) {
      this.fallback = options.fallback;
    } else if (apiKey && apiKey.length > 0) {
      this.fallback = new ApiFootballProvider(apiKey, options?.apiFootballTier ?? process.env.API_FOOTBALL_TIER);
    } else {
      this.fallback = null;
    }

    this.logger = options?.logger ?? {
      warn: (msg, meta) => console.warn(`[RESILIENT-PROVIDER:WARN] ${msg}`, meta ? JSON.stringify(meta) : ''),
      info: (msg, meta) => console.log(`[RESILIENT-PROVIDER] ${msg}`, meta ? JSON.stringify(meta) : ''),
    };
  }

  public get circuitBreaker(): CircuitBreaker {
    return this.primary.circuitBreaker;
  }

  /**
   * Returns whether the primary FotMob adapter is actively tripping or OPEN.
   */
  public isPrimaryTripped(): boolean {
    return this.primary.circuitBreaker.getState() === CircuitState.OPEN;
  }

  /**
   * Returns the currently active underlying provider name for diagnostic ledger reporting.
   */
  public get activeProviderName(): string {
    if (this.isPrimaryTripped() && this.fallback) {
      return this.fallback.name;
    }
    return this.primary.name;
  }

  public async getLiveMatches(): Promise<NormalizedLiveMatch[]> {
    try {
      return await this.primary.getLiveMatches();
    } catch (error: any) {
      if (this.isCircuitOrNetworkError(error)) {
        if (this.fallback) {
          this.logger.warn(
            `FotMob live acquisition failed (${error?.message ?? error}). Falling back to ApiFootballProvider.`
          );
          try {
            return await this.fallback.getLiveMatches();
          } catch (fallbackErr: any) {
            this.logger.warn(
              `Fallback ApiFootballProvider also failed for live matches: ${fallbackErr?.message ?? fallbackErr}. Failing gracefully.`
            );
            return [];
          }
        }
        this.logger.warn(
          `FotMob live acquisition interrupted (${error?.message ?? error}) and no API_FOOTBALL_KEY configured. Returning empty live list gracefully.`
        );
        return [];
      }
      throw error;
    }
  }

  public async getDailyMatches(date: string): Promise<NormalizedMatch[]> {
    try {
      return await this.primary.getDailyMatches(date);
    } catch (error: any) {
      if (this.isCircuitOrNetworkError(error)) {
        if (this.fallback) {
          this.logger.warn(
            `FotMob daily schedule for ${date} failed (${error?.message ?? error}). Falling back to ApiFootballProvider.`
          );
          try {
            return await this.fallback.getDailyMatches(date);
          } catch (fallbackErr: any) {
            this.logger.warn(
              `Fallback ApiFootballProvider also failed for daily schedule (${date}): ${fallbackErr?.message ?? fallbackErr}. Failing gracefully.`
            );
            return [];
          }
        }
        this.logger.warn(
          `FotMob daily schedule for ${date} interrupted (${error?.message ?? error}) and no API_FOOTBALL_KEY configured. Returning empty schedule gracefully.`
        );
        return [];
      }
      throw error;
    }
  }

  public async getMatchDetails(providerMatchId: string): Promise<NormalizedMatchDetails> {
    try {
      return await this.primary.getMatchDetails(providerMatchId);
    } catch (error: any) {
      if (this.isCircuitOrNetworkError(error)) {
        if (this.fallback) {
          this.logger.warn(
            `FotMob match details for '${providerMatchId}' failed (${error?.message ?? error}). Falling back to ApiFootballProvider.`
          );
          return await this.fallback.getMatchDetails(providerMatchId);
        }
        throw error;
      }
      throw error;
    }
  }

  private isCircuitOrNetworkError(error: any): boolean {
    if (!error) return false;
    if (this.primary.circuitBreaker.getState() === CircuitState.OPEN) return true;
    const msg = String(error?.message || error);
    return (
      msg.includes('CircuitBreakerOpen') ||
      msg.includes('CircuitBreaker') ||
      msg.includes('fetch failed') ||
      msg.includes('ECONNREFUSED') ||
      msg.includes('ETIMEDOUT') ||
      msg.includes('ENOTFOUND') ||
      error.name === 'AbortError' ||
      error.name === 'ProviderChallengeError' ||
      error.name === 'ProviderNetworkError' ||
      error.name === 'ProviderSchemaError'
    );
  }
}
