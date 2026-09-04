export type RequestPriority = 'NORMAL' | 'CRITICAL';

export interface GatewayRequestOptions {
  provider?: string; // default 'api_football'
  endpoint: string; // e.g. '/status', '/leagues'
  operation: string; // e.g. 'status_probe', 'get_leagues'
  priority?: RequestPriority; // default 'NORMAL'
  timeoutMs?: number; // default 10000
  maxRetries?: number; // default 0 (explicit retries managed if specified)
  cycleId?: string;
  competitionId?: string;
}

export interface GatewayResponse<T = any> {
  success: boolean;
  httpStatus: number;
  data: T | null;
  responseTimeMs: number;
  providerDailyRemaining?: number;
  providerMinuteRemaining?: number;
  error?: string;
  ledgerId: string;
}

export interface ReservationResult {
  allowed: boolean;
  ledgerId?: string;
  waitMs?: number;
  reason?: 'OPERATIONAL_BUDGET_EXHAUSTED' | 'HARD_BUDGET_EXHAUSTED' | 'MINUTE_RATE_LIMIT';
  remainingDaily?: number;
  consumedDaily?: number;
}
