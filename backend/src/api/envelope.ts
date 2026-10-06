export interface ApiResponseEnvelope<T> {
  schemaVersion: string;
  requestId: string;
  version: number;
  generatedAt: string;
  serverTime: string;
  stale: boolean;
  dataAgeSeconds: number;
  data: T | null;
  quotaExhausted?: boolean;
  error?: {
    code: string;
    message: string;
    details?: unknown;
  };
}

let requestCounter = 0;

/**
 * Creates a standardized ApiResponseEnvelope conforming to docs/API_CONTRACT.md
 */
export function createResponseEnvelope<T>(
  data: T,
  options?: {
    requestId?: string;
    version?: number;
    generatedAt?: Date;
    stale?: boolean;
    dataAgeSeconds?: number;
    schemaVersion?: string;
    quotaExhausted?: boolean;
  }
): ApiResponseEnvelope<T> {
  const now = new Date();
  requestCounter = (requestCounter + 1) % 1_000_000;
  const reqId = options?.requestId || `req_${Date.now()}_${requestCounter.toString().padStart(6, '0')}`;

  return {
    schemaVersion: options?.schemaVersion || '1.0',
    requestId: reqId,
    version: options?.version ?? 1,
    generatedAt: (options?.generatedAt || now).toISOString(),
    serverTime: now.toISOString(),
    stale: options?.stale ?? false,
    dataAgeSeconds: options?.dataAgeSeconds ?? 0,
    data,
    ...(options?.quotaExhausted !== undefined ? { quotaExhausted: options.quotaExhausted } : {}),
  };
}

/**
 * Creates a standardized ErrorResponseEnvelope conforming to docs/API_CONTRACT.md
 */
export function createErrorEnvelope(
  code: string,
  message: string,
  details?: unknown,
  options?: {
    requestId?: string;
    schemaVersion?: string;
  }
): ApiResponseEnvelope<null> {
  const now = new Date();
  requestCounter = (requestCounter + 1) % 1_000_000;
  const reqId = options?.requestId || `req_${Date.now()}_${requestCounter.toString().padStart(6, '0')}`;

  return {
    schemaVersion: options?.schemaVersion || '1.0',
    requestId: reqId,
    version: 1,
    generatedAt: now.toISOString(),
    serverTime: now.toISOString(),
    stale: false,
    dataAgeSeconds: 0,
    data: null,
    error: {
      code,
      message,
      ...(details !== undefined ? { details } : {}),
    },
  };
}
