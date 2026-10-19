import { describe, it, expect, beforeEach, afterEach, vi as jest } from 'vitest';
import { CircuitBreaker, CircuitState } from './circuit-breaker.js';
import { ProviderChallengeError, ProviderResourceNotFoundError } from '../providers/errors.js';

describe('CircuitBreaker', () => {
  let cb: CircuitBreaker;

  beforeEach(() => {
    // Threshold 1, backoff 100ms
    cb = new CircuitBreaker({ failureThreshold: 1, resetTimeoutMs: 100 });
  });

  afterEach(() => {
    jest.useRealTimers();
  });

  it('should trip to OPEN after failureThreshold is reached', async () => {
    const action = jest.fn().mockRejectedValue(new ProviderChallengeError());
    
    await expect(cb.execute(action)).rejects.toThrow(ProviderChallengeError);
    expect(cb.getState()).toBe(CircuitState.OPEN);
  });

  it('should allow exactly one probe request during HALF_OPEN state concurrently', async () => {
    jest.useFakeTimers();
    const action = jest.fn().mockRejectedValue(new ProviderChallengeError());
    
    await expect(cb.execute(action)).rejects.toThrow(ProviderChallengeError);
    expect(cb.getState()).toBe(CircuitState.OPEN);
    
    jest.advanceTimersByTime(200);

    let resolveProbe: any;
    const slowAction = jest.fn().mockImplementation(() => new Promise((resolve) => {
      resolveProbe = resolve;
    }));

    const probePromise = cb.execute(slowAction);

    const concurrent1 = cb.execute(slowAction);
    const concurrent2 = cb.execute(slowAction);

    await expect(concurrent1).rejects.toThrow('CircuitBreakerOpen');
    await expect(concurrent2).rejects.toThrow('CircuitBreakerOpen');

    resolveProbe('success');
    await expect(probePromise).resolves.toBe('success');

    expect(cb.getState()).toBe(CircuitState.CLOSED);
  });

  it('should close circuit if probe throws non-tripping error (e.g. 404)', async () => {
    jest.useFakeTimers();
    const action = jest.fn().mockRejectedValue(new ProviderChallengeError());
    
    await expect(cb.execute(action)).rejects.toThrow(ProviderChallengeError);
    expect(cb.getState()).toBe(CircuitState.OPEN);
    
    jest.advanceTimersByTime(200);

    // This error does not trip the circuit
    const probeAction = jest.fn().mockRejectedValue(new ProviderResourceNotFoundError());
    await expect(cb.execute(probeAction)).rejects.toThrow(ProviderResourceNotFoundError);

    // Because the transport succeeded, the circuit should close
    expect(cb.getState()).toBe(CircuitState.CLOSED);
  });
  
  it('should clear halfOpenProbeInFlight even if internal exception occurs in HALF_OPEN', async () => {
    jest.useFakeTimers();
    const action = jest.fn().mockRejectedValue(new ProviderChallengeError());
    
    await expect(cb.execute(action)).rejects.toThrow(ProviderChallengeError);
    expect(cb.getState()).toBe(CircuitState.OPEN);
    
    jest.advanceTimersByTime(200);

    // This is an internal TypeError, not a ProviderNetworkError/ProviderChallengeError
    const probeAction = jest.fn().mockRejectedValue(new TypeError('Something internal'));
    await expect(cb.execute(probeAction)).rejects.toThrow(TypeError);

    expect(cb.getState()).toBe(CircuitState.CLOSED);
    
    // Ensure another request can go through since probe flight flag was cleared
    const nextAction = jest.fn().mockResolvedValue('success');
    await expect(cb.execute(nextAction)).resolves.toBe('success');
  });
});
