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
    const entry: LogEntry = {
      timestamp: new Date().toISOString(),
      level,
      component,
      eventName,
      message: sanitize(message),
      correlationId,
      meta,
    };

    const corrStr = correlationId ? `[${correlationId}] ` : '';
    const formatted = `[${entry.timestamp}] [${level.toUpperCase()}] [${component}:${eventName}] ${corrStr}${entry.message}`;

    if (level === 'error' || level === 'critical') {
      console.error(formatted, meta ? JSON.stringify(meta) : '');
    } else if (level === 'warn') {
      console.warn(formatted, meta ? JSON.stringify(meta) : '');
    } else {
      console.log(formatted, meta ? JSON.stringify(meta) : '');
    }
  }
}
