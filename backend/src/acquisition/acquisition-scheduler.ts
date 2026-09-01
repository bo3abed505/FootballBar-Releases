/**
 * acquisition-scheduler.ts
 *
 * Single-instance, cancellable, budget-aware background scheduler for FootballTray.
 *
 * Responsibilities:
 *   - Repeatedly evaluates AcquisitionPolicyEngine against PostgreSQL fixture state & budget.
 *   - Dispatches resulting SyncPlan via SyncService (orchestration only).
 *   - Waits for policy-recommended cadence intervals before repeating.
 *   - Ensures strictly zero cycle overlap.
 *   - Implements clean cancellation via AbortController / AbortSignal without busy-waiting.
 *   - Never contains provider-specific logic, direct fetch, or hard-coded cadence thresholds.
 */

import { and, eq } from 'drizzle-orm';
import { db, schema } from '../db/index.js';
import {
  AcquisitionPolicyEngine,
  BudgetContext,
  FixturesSummary,
  SyncPlan,
} from './acquisition-policy.js';
import { SyncService, SyncSummary } from './sync.service.js';
import { FixturesRepository } from '../repositories/fixtures.repository.js';

export interface SchedulerLogger {
  info(message: string, meta?: Record<string, unknown>): void;
  warn(message: string, meta?: Record<string, unknown>): void;
  error(message: string, meta?: Record<string, unknown>): void;
}

export interface SchedulerDependencies {
  syncService: SyncService;
  fixturesRepository: FixturesRepository;
  getBudgetContext?: (provider?: string) => Promise<BudgetContext>;
  nowFn?: () => Date;
  sleepFn?: (ms: number, signal?: AbortSignal) => Promise<void>;
  targetCompetitionIds?: number[];
  providerName?: string;
  onCycleComplete?: (plan: SyncPlan, summary: SyncSummary | null) => void;
  onError?: (error: Error) => void;
  logger?: SchedulerLogger;
}

/**
 * Default cancellable sleep using setTimeout and AbortSignal.
 */
async function defaultSleep(ms: number, signal?: AbortSignal): Promise<void> {
  if (signal?.aborted) return;
  return new Promise((resolve) => {
    const timer = setTimeout(() => {
      signal?.removeEventListener('abort', onAbort);
      resolve();
    }, ms);

    const onAbort = () => {
      clearTimeout(timer);
      signal?.removeEventListener('abort', onAbort);
      resolve();
    };

    signal?.addEventListener('abort', onAbort);
  });
}

/**
 * Default PostgreSQL reader for provider daily budget state.
 */
export async function getDatabaseBudgetContext(provider = 'api_football'): Promise<BudgetContext> {
  const todayStr = new Date().toISOString().slice(0, 10);
  const row = await db.query.providerDailyBudget.findFirst({
    where: and(
      eq(schema.providerDailyBudget.provider, provider),
      eq(schema.providerDailyBudget.budgetDate, todayStr)
    ),
  });

  if (!row) {
    // If no budget row exists yet for today, assume full fresh daily allocation
    return {
      allocatedDaily: 100,
      consumedDaily: 0,
      remainingDaily: 100,
      isExhausted: false,
    };
  }

  return {
    allocatedDaily: row.allocatedCalls,
    consumedDaily: row.consumedCalls,
    remainingDaily: row.remainingCalls,
    isExhausted: row.isExhausted,
  };
}

export class AcquisitionScheduler {
  private readonly syncService: SyncService;
  private readonly fixturesRepo: FixturesRepository;
  private readonly getBudget: (provider?: string) => Promise<BudgetContext>;
  private readonly now: () => Date;
  private readonly sleep: (ms: number, signal?: AbortSignal) => Promise<void>;
  private readonly targetCompetitionIds?: number[];
  private readonly providerName: string;
  private readonly onCycleComplete?: (plan: SyncPlan, summary: SyncSummary | null) => void;
  private readonly onErrorCallback?: (error: Error) => void;
  private readonly logger: SchedulerLogger;

  // Single-instance and lifecycle state
  private running = false;
  private executingCycle = false;
  private abortController: AbortController | null = null;
  private loopPromise: Promise<void> | null = null;

  // Default fallback sleep when policy returns IDLE with no recommended delay (e.g., 5 minutes)
  static readonly DEFAULT_IDLE_DELAY_MS = 300_000;
  // Default error backoff to avoid aggressive looping on persistent network/database issues
  static readonly DEFAULT_ERROR_BACKOFF_MS = 30_000;

  constructor(deps: SchedulerDependencies) {
    this.syncService = deps.syncService;
    this.fixturesRepo = deps.fixturesRepository;
    this.getBudget = deps.getBudgetContext ?? getDatabaseBudgetContext;
    this.now = deps.nowFn ?? (() => new Date());
    this.sleep = deps.sleepFn ?? defaultSleep;
    this.targetCompetitionIds = deps.targetCompetitionIds;
    this.providerName = deps.providerName ?? 'api_football';
    this.onCycleComplete = deps.onCycleComplete;
    this.onErrorCallback = deps.onError;
    this.logger = deps.logger ?? {
      info: (msg, meta) => console.log(`[SCHEDULER] ${msg}`, meta ? JSON.stringify(meta) : ''),
      warn: (msg, meta) => console.warn(`[SCHEDULER:WARN] ${msg}`, meta ? JSON.stringify(meta) : ''),
      error: (msg, meta) => console.error(`[SCHEDULER:ERROR] ${msg}`, meta ? JSON.stringify(meta) : ''),
    };
  }

  /**
   * Returns whether the background scheduler loop is currently active.
   */
  isRunning(): boolean {
    return this.running;
  }

  /**
   * Returns whether a synchronization cycle is actively in-flight.
   */
  isExecutingCycle(): boolean {
    return this.executingCycle;
  }

  /**
   * Starts the background acquisition scheduler loop.
   * Idempotent: does nothing if already running.
   */
  start(): void {
    if (this.running) {
      this.logger.warn('Scheduler start() requested but loop is already active.');
      return;
    }

    this.running = true;
    this.abortController = new AbortController();
    this.logger.info('Background acquisition scheduler started.');

    this.loopPromise = this.runLoop(this.abortController.signal);
  }

  /**
   * Stops the scheduler loop and cancels any pending delay.
   * Resolves when the active cycle (if any) completes.
   */
  async stop(): Promise<void> {
    if (!this.running) {
      return;
    }

    this.logger.info('Stopping background acquisition scheduler...');
    this.running = false;

    if (this.abortController) {
      this.abortController.abort();
    }

    if (this.loopPromise) {
      await this.loopPromise;
      this.loopPromise = null;
    }

    this.abortController = null;
    this.logger.info('Background acquisition scheduler stopped cleanly.');
  }

  /**
   * Executes a single evaluation and sync cycle.
   * Can be called directly for testing or one-shot sync execution.
   */
  async executeSingleCycle(): Promise<{ plan: SyncPlan; summary: SyncSummary | null }> {
    if (this.executingCycle) {
      throw new Error('Cannot execute cycle: another cycle is already in progress.');
    }

    this.executingCycle = true;
    try {
      const currentDate = this.now();
      const todayDateStr = currentDate.toISOString().slice(0, 10);

      // 1. Fetch current budget state
      const budget = await this.getBudget(this.providerName);

      // 2. Fetch fixture summary from repository
      const summary = await this.fixturesRepo.getActiveSummary(currentDate);

      // 3. Evaluate pure acquisition policy
      const plan = AcquisitionPolicyEngine.evaluateSyncPlan(budget, summary, currentDate);

      this.logger.info(`Evaluated sync plan: ${plan.action}`, {
        action: plan.action,
        priority: plan.priority,
        recommendedNextPollMs: plan.recommendedNextPollMs,
        reason: plan.reason,
        remainingBudget: budget.remainingDaily,
      });

      // 4. Dispatch action via SyncService
      let syncSummary: SyncSummary | null = null;
      if (plan.action !== 'IDLE') {
        syncSummary = await this.syncService.syncWithPlan(plan, {
          date: todayDateStr,
          competitionIds: this.targetCompetitionIds,
        });

        this.logger.info(`Sync cycle completed for ${plan.action}`, {
          fixturesDiscovered: syncSummary?.fixturesDiscovered ?? 0,
          fixturesInserted: syncSummary?.fixturesInserted ?? 0,
          fixturesUpdated: syncSummary?.fixturesUpdated ?? 0,
          eventsInserted: syncSummary?.eventsInserted ?? 0,
        });
      } else {
        this.logger.info(`System idle: ${plan.reason}`);
      }

      // 5. Notify cycle complete callback
      this.onCycleComplete?.(plan, syncSummary);

      return { plan, summary: syncSummary };
    } finally {
      this.executingCycle = false;
    }
  }

  /**
   * Internal autonomous background loop.
   */
  private async runLoop(signal: AbortSignal): Promise<void> {
    while (this.running && !signal.aborted) {
      let nextWaitMs = AcquisitionScheduler.DEFAULT_IDLE_DELAY_MS;

      try {
        const { plan } = await this.executeSingleCycle();

        // Calculate next polling delay from policy recommendation
        if (plan.recommendedNextPollMs !== null && plan.recommendedNextPollMs > 0) {
          nextWaitMs = plan.recommendedNextPollMs;
        } else {
          nextWaitMs = AcquisitionScheduler.DEFAULT_IDLE_DELAY_MS;
        }
      } catch (err: any) {
        const error = err instanceof Error ? err : new Error(String(err));
        this.logger.error(`Error during scheduler execution cycle: ${error.message}`, {
          error: error.message,
          stack: error.stack,
        });

        this.onErrorCallback?.(error);

        // Safe error backoff to prevent tight retry loops on error
        nextWaitMs = AcquisitionScheduler.DEFAULT_ERROR_BACKOFF_MS;
      }

      if (!this.running || signal.aborted) {
        break;
      }

      this.logger.info(`Scheduler waiting ${Math.round(nextWaitMs / 1000)}s until next evaluation cycle.`);
      await this.sleep(nextWaitMs, signal);
    }
  }
}
