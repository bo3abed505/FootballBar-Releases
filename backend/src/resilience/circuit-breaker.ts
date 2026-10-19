import { ProviderChallengeError, ProviderSchemaError, ProviderNetworkError, ProviderResourceNotFoundError } from '../providers/errors.js';

export enum CircuitState { CLOSED, OPEN, HALF_OPEN }
export interface CircuitBreakerOptions { failureThreshold?: number; resetTimeoutMs?: number; }

export class CircuitBreaker {
  private state: CircuitState = CircuitState.CLOSED;
  private failureCount: number = 0;
  private nextAttemptAt: number = 0;
  private halfOpenProbeInFlight: boolean = false;
  private readonly failureThreshold: number;
  private readonly resetTimeoutMs: number;

  constructor(options: CircuitBreakerOptions = {}) {
    this.failureThreshold = options.failureThreshold ?? 1; 
    this.resetTimeoutMs = options.resetTimeoutMs ?? 60000;
  }

  public async execute<T>(action: () => Promise<T>): Promise<T> {
    if (this.state === CircuitState.OPEN) {
      if (Date.now() > this.nextAttemptAt) {
        this.state = CircuitState.HALF_OPEN;
        this.halfOpenProbeInFlight = true;
      } else {
        throw new Error('CircuitBreakerOpen');
      }
    } else if (this.state === CircuitState.HALF_OPEN) {
      if (this.halfOpenProbeInFlight) {
        throw new Error('CircuitBreakerOpen');
      }
      this.halfOpenProbeInFlight = true;
    }

    try {
      const result = await action();
      this.onSuccess();
      return result;
    } catch (error: any) {
      if (
        error instanceof ProviderChallengeError || 
        error instanceof ProviderSchemaError ||
        error instanceof ProviderNetworkError
      ) {
         this.onFailure();
      } else if (this.state === CircuitState.HALF_OPEN) {
         // Non-tripping error (e.g. 404, or internal exception) means upstream didn't block us.
         // Close the circuit so we don't leak state.
         this.onSuccess();
      }
      throw error;
    } finally {
      this.halfOpenProbeInFlight = false;
    }
  }

  private onSuccess(): void {
    this.state = CircuitState.CLOSED;
    this.failureCount = 0;
  }

  private onFailure(): void {
    this.failureCount++;
    if (this.state === CircuitState.HALF_OPEN || this.failureCount >= this.failureThreshold) {
      this.state = CircuitState.OPEN;
      const backoffMs = this.resetTimeoutMs * Math.pow(2, this.failureCount - this.failureThreshold);
      this.nextAttemptAt = Date.now() + backoffMs;
    }
  }

  public getState(): CircuitState { return this.state; }
}
