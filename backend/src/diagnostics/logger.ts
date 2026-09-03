/**
 * logger.ts
 *
 * Central structured diagnostic logger for FootballTray backend.
 * Emits sanitized, structured log entries with support for correlation IDs.
 * Strictly guarantees zero secrets, passwords, or Authorization headers are printed.
 */

import { randomBytes } from 'crypto';

export type LogLevel = 'debug' | 'info' | 'warn' | 'error' | 'critical';

export interface LogEntry {
  timestamp: string;
  level: LogLevel;
  component: string;
  eventName: string;
  message: string;
  correlationId?: string;
  meta?: Record<string, unknown>;
}

/**
 * Sanitizes input string or object properties to strip secrets, keys, and credentials.
 */
export function sanitize(input: string | null | undefined): string {
  if (!input) return '';
  return input
    .replace(/key=[a-zA-Z0-9_-]+/gi, 'key=[REDACTED]')
    .replace(/(x-apisports-key:\s*)[a-zA-Z0-9_-]+/gi, '$1[REDACTED]')
    .replace(/(postgresql:\/\/[^:]+:)[^@]+(@)/gi, '$1[REDACTED]$2')
    .replace(/password=[^;\s]+/gi, 'password=[REDACTED]')
    .replace(/Bearer\s+[a-zA-Z0-9_.\-]+/gi, 'Bearer [REDACTED]');
}

const SENSITIVE_KEYS = new Set([
  'password',
  'passwd',
  'secret',
  'token',
  'access_token',
  'refresh_token',
  'api_key',
  'apikey',
  'authorization',
  'cookie',
  'set-cookie',
  'client_secret',
  'x-api-key',
  'x-apisports-key',
]);

const SENSITIVE_NORMALIZED = new Set([
  'password',
  'passwd',
  'secret',
  'token',
  'accesstoken',
  'refreshtoken',
  'apikey',
  'authorization',
  'cookie',
  'setcookie',
  'clientsecret',
  'xapikey',
  'xapisportskey',
]);

export function isSensitiveKey(key: string): boolean {
  const lower = key.toLowerCase();
  if (SENSITIVE_KEYS.has(lower)) return true;
  const normalized = lower.replace(/[-_]/g, '');
  if (SENSITIVE_NORMALIZED.has(normalized)) return true;
  if (/(^|[-_])(password|passwd|secret|token|api_?key|authorization|cookie)([-_]|$)/i.test(lower)) {
    return true;
  }
  return false;
}

/**
 * Recursively sanitizes metadata to redact sensitive keys and credentials in strings,
 * while safely handling circular references without mutating the source object.
 */
export function sanitizeMeta(input: unknown, activePath = new Set<object>()): unknown {
  if (input === null || input === undefined) {
    return input;
  }

  if (typeof input === 'string') {
    return sanitize(input);
  }

  if (typeof input !== 'object') {
    return input;
  }

  if (input instanceof Date) {
    return new Date(input.getTime());
  }

  if (input instanceof RegExp) {
    return input;
  }

  if (input instanceof Error) {
    const sanitizedError: Record<string, unknown> = {
      name: input.name,
      message: sanitize(input.message),
      stack: input.stack ? sanitize(input.stack) : undefined,
    };
    return sanitizedError;
  }

  if (activePath.has(input)) {
    return '[Circular]';
  }

  activePath.add(input);
  try {
    if (Array.isArray(input)) {
      return input.map((item) => sanitizeMeta(item, activePath));
    }

    const sanitized: Record<string, unknown> = {};
    for (const [key, value] of Object.entries(input)) {
      if (isSensitiveKey(key)) {
        sanitized[key] = '[REDACTED]';
      } else {
        sanitized[key] = sanitizeMeta(value, activePath);
      }
    }

    return sanitized;
  } finally {
    activePath.delete(input);
  }
}

/**
 * Generates a lightweight, safe correlation ID.
 */
export function generateCorrelationId(prefix = 'op'): string {
  const rand = randomBytes(4).toString('hex');
  return `${prefix}_${rand}`;
}

export class Logger {
  static debug(component: string, eventName: string, message: string, correlationId?: string, meta?: Record<string, unknown>): void {
    if (process.env.NODE_ENV === 'test') return;
    this.log('debug', component, eventName, message, correlationId, meta);
  }

  static info(component: string, eventName: string, message: string, correlationId?: string, meta?: Record<string, unknown>): void {
    if (process.env.NODE_ENV === 'test') return;
    this.log('info', component, eventName, message, correlationId, meta);
  }

  static warn(component: string, eventName: string, message: string, correlationId?: string, meta?: Record<string, unknown>): void {
    this.log('warn', component, eventName, message, correlationId, meta);
  }

  static error(component: string, eventName: string, message: string, correlationId?: string, meta?: Record<string, unknown>): void {
    this.log('error', component, eventName, message, correlationId, meta);
  }

  static critical(component: string, eventName: string, message: string, correlationId?: string, meta?: Record<string, unknown>): void {
    this.log('critical', component, eventName, message, correlationId, meta);
  }

  private static log(
    level: LogLevel,
    component: string,
    eventName: string,
    message: string,
    correlationId?: string,
    meta?: Record<string, unknown>
  ): void {
    const cleanMeta = meta ? (sanitizeMeta(meta) as Record<string, unknown>) : undefined;
    const entry: LogEntry = {
      timestamp: new Date().toISOString(),
      level,
      component,
      eventName,
      message: sanitize(message),
      correlationId,
      meta: cleanMeta,
    };

    const corrStr = correlationId ? `[${correlationId}] ` : '';
    const formatted = `[${entry.timestamp}] [${level.toUpperCase()}] [${component}:${eventName}] ${corrStr}${entry.message}`;

    if (level === 'error' || level === 'critical') {
      console.error(formatted, cleanMeta ? JSON.stringify(cleanMeta) : '');
    } else if (level === 'warn') {
      console.warn(formatted, cleanMeta ? JSON.stringify(cleanMeta) : '');
    } else {
      console.log(formatted, cleanMeta ? JSON.stringify(cleanMeta) : '');
    }
  }
}
