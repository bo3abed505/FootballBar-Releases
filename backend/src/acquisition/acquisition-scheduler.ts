/**
 * acquisition-scheduler.ts
 *
 * Single-instance, cancellable, budget-aware background scheduler for FootballTray.
 *
 * Responsibilities:
 *   - Repeatedly evaluates AcquisitionPolicyEngine against SQLite fixture state & budget.
 *   - Dispatches resulting SyncPlan via SyncService (orchestration only).
 *   - Waits for policy-recommended cadence intervals before repeating.
 *   - Ensures strictly zero cycle overlap.
 *   - Implements clean cancellation via AbortController / AbortSignal without busy-waiting.
 *   - Never contains provider-specific logic, direct fetch, or hard-coded cadence thresholds.
 */

import { and, desc, eq, gte, lt, notInArray, notLike, or, sql } from 'drizzle-orm';
import { db, schema } from '../db/index.js';
import {
  AcquisitionPolicyEngine,
  BudgetContext,
  FixturesSummary,
  SyncPlan,
} from './acquisition-policy.js';
import { SyncService, SyncSummary } from './sync.service.js';
import { FixturesRepository } from '../repositories/fixtures.repository.js';
import {
  SchedulerCyclesRepository,
  SchedulerCycleOutcome,
  SchedulerErrorType,
} from '../repositories/scheduler-cycles.repository.js';

export interface SchedulerLogger {
  info(message: string, meta?: Record<string, unknown>): void;
  warn(message: string, meta?: Record<string, unknown>): void;
  error(message: string, meta?: Record<string, unknown>): void;
}

export interface SchedulerMilestones {
  lastSuccessfulDiscovery: {
    cycleId: string | null;
    timestamp: string | null;
    date: string | null;
  };
  lastScheduleInvalidation: {
    cycleId: string | null;
    timestamp: string | null;
    reason: string | null;
  } | null;
  lastLivePoll: {
    cycleId: string | null;
    timestamp: string | null;
    matchesUpdated: number;
  } | null;
  lastStaleRecovery: {
    cycleId: string | null;
    timestamp: string | null;
    competitionsRecovered: number[];
  } | null;
}

export interface SchedulerDiagnostics {
  running: boolean;
  executingCycle: boolean;
  currentState: 'IDLE' | 'EXECUTING' | 'SLEEPING' | 'STOPPED';
  lastCycleAt: string | null;
  lastCycleAction: string | null;
  lastCycleDurationMs: number | null;
  lastCycleOutcome: SchedulerCycleOutcome | null;
  consecutiveErrors: number;
  nextRecommendedWakeAt: string | null;
  nextPlannedAction: {
    action: string;
    cadenceMs: number | null;
    wakeAt: string | null;
    reason: string;
  } | null;
  lastCycle: any | null;
  milestones: SchedulerMilestones;
}

export interface SchedulerDependencies {
  syncService: SyncService;
  fixturesRepository: FixturesRepository;
  schedulerCyclesRepository?: SchedulerCyclesRepository;
  preferencesService?: import('./daemon-preferences.service.js').DaemonPreferencesService;
  getBudgetContext?: (provider?: string, now?: Date) => Promise<BudgetContext>;
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
 * Default SQLite reader for provider daily budget state.
 */
export async function getDatabaseBudgetContext(provider = 'api_football', now = new Date()): Promise<BudgetContext> {
  const todayStr = now.toISOString().slice(0, 10);
  const startOfDay = new Date(todayStr + 'T00:00:00.000Z');
  const endOfDay = new Date(startOfDay.getTime() + 24 * 60 * 60 * 1000);

  // Authoritative daily accounting: query today's ledger for real ground truth
  try {
    const ledgerToday = await db.query.apiRequestLedger.findMany({
      where: and(
        eq(schema.apiRequestLedger.provider, provider),
        gte(schema.apiRequestLedger.requestedAt, startOfDay),
        lt(schema.apiRequestLedger.requestedAt, endOfDay)
      ),
      columns: { costUnits: true },
    });
    const ledgerConsumed = ledgerToday.reduce((acc, r) => acc + (r.costUnits ?? 1), 0);
    if (ledgerConsumed > 0 || provider === 'api_football') {
      const remaining = Math.max(0, 100 - ledgerConsumed);
      return {
        allocatedDaily: 100,
        consumedDaily: ledgerConsumed,
        remainingDaily: remaining,
        isExhausted: remaining <= 0,
      };
    }
  } catch {
    // If ledger query fails, fallback to provider_daily_budget table
  }

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
  private readonly cyclesRepo: SchedulerCyclesRepository;
  private readonly getBudget: (provider?: string, now?: Date) => Promise<BudgetContext>;
  private readonly now: () => Date;
  private readonly sleep: (ms: number, signal?: AbortSignal) => Promise<void>;
  private targetCompetitionIds?: number[];
  private readonly providerName: string;
  private readonly onCycleComplete?: (plan: SyncPlan, summary: SyncSummary | null) => void;
  private readonly onErrorCallback?: (error: Error) => void;
  private readonly logger: SchedulerLogger;

  // Dynamic user preferences & pinned matches
  private preferencesService?: import('./daemon-preferences.service.js').DaemonPreferencesService;
  private pinnedMatchIds: Set<string> = new Set();

  // Twice-daily schedule sync slot tracking
  private lastScheduleSyncSlot: string | null = null;
  private lastScheduleSyncAt: Date | null = null;
  private lastScheduleSyncAttemptAt: Date | null = null;

  // Single-instance and lifecycle state
  private running = false;
  private executingCycle = false;
  private abortController: AbortController | null = null;
  private loopPromise: Promise<void> | null = null;

  // Diagnostic state metrics
  private lastCycleAt: Date | null = null;
  private lastCycleAction: string | null = null;
  private lastCycleDurationMs: number | null = null;
  private lastCycleOutcome: SchedulerCycleOutcome | null = null;
  private consecutiveErrors = 0;
  private nextRecommendedWakeAt: Date | null = null;
  private nextPlannedAction: {
    action: string;
    cadenceMs: number | null;
    wakeAt: string | null;
    reason: string;
  } | null = null;
  private lastCycleRecord: any | null = null;
  private milestones: SchedulerMilestones = {
    lastSuccessfulDiscovery: {
      cycleId: null,
      timestamp: null,
      date: null,
    },
    lastScheduleInvalidation: null,
    lastLivePoll: null,
    lastStaleRecovery: null,
  };

  // Default fallback sleep when policy returns IDLE with no recommended delay (e.g., 5 minutes)
  static readonly DEFAULT_IDLE_DELAY_MS = 300_000;
  // Default error backoff to avoid aggressive looping on persistent network/database issues
  static readonly DEFAULT_ERROR_BACKOFF_MS = 30_000;

  constructor(deps: SchedulerDependencies) {
    this.syncService = deps.syncService;
    this.fixturesRepo = deps.fixturesRepository;
    this.cyclesRepo = deps.schedulerCyclesRepository ?? new SchedulerCyclesRepository();
    this.preferencesService = deps.preferencesService;
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

  public bindPreferencesService(service: import('./daemon-preferences.service.js').DaemonPreferencesService): void {
    this.preferencesService = service;
  }

  public setPinnedMatchIds(ids: Array<string | number>): void {
    this.pinnedMatchIds = new Set(ids.map(String));
  }

  public getPinnedMatchIds(): string[] {
    return Array.from(this.pinnedMatchIds);
  }

  public isTwiceDailyScheduleSyncDue(now: Date = this.now()): boolean {
    const todayStr = now.toISOString().slice(0, 10);
    const slotHour = now.getUTCHours() < 12 ? '00' : '12';
    const currentSlot = `${todayStr}-${slotHour}`;

    // 1. If current slot was already successfully completed, not due
    if (this.lastScheduleSyncSlot === currentSlot) {
      return false;
    }

    // 2. Minimum retry cooldown: prevent tight 15-minute retry loops on unexpected failure
    // Minimum 2 hours must elapse before attempting another schedule discovery
    if (this.lastScheduleSyncAttemptAt !== null) {
      const elapsedMs = now.getTime() - this.lastScheduleSyncAttemptAt.getTime();
      if (elapsedMs < 2 * 60 * 60 * 1000) {
        return false;
      }
    }

    return true;
  }

  public setTargetCompetitionIds(ids?: number[]): void {
    this.targetCompetitionIds = ids;
    this.logger.info(`Updated scheduler target competition IDs: [${ids ? ids.join(', ') : 'ALL'}]`);
  }

  public getTargetCompetitionIds(): number[] | undefined {
    return this.targetCompetitionIds;
  }

  /**
   * Hydrates scheduler state, historical cycles, and milestones from SQLite.
   * Recovers any unfinished cycles left by an unexpected process restart.
   */
  async hydrateStateFromDatabase(): Promise<void> {
    try {
      // 1. Recover any unfinished cycles (completed_at IS NULL -> ERROR, CRASH_RECOVERED)
      const recoveredCount = await this.cyclesRepo.recoverInterruptedCycles(this.now());
      if (recoveredCount > 0) {
        this.logger.warn(`Recovered ${recoveredCount} interrupted cycle(s) from previous process crash/restart.`);
      }

      // 2. Fetch latest cycle
      const latest = await this.cyclesRepo.getLatestCycle();
      if (latest) {
        this.lastCycleRecord = latest;
        this.lastCycleAt = new Date(latest.startedAt);
        this.lastCycleAction = latest.action;
        this.lastCycleDurationMs = latest.durationMs;
        this.lastCycleOutcome = (latest.outcome as any) ?? null;
      }

      // 3. Hydrate milestones from database
      const todayStr = this.now().toISOString().slice(0, 10);
      const latestDiscovery = await this.cyclesRepo.getLatestSuccessfulDiscovery?.(this.now());
      if (latestDiscovery) {
        this.milestones.lastSuccessfulDiscovery = {
          cycleId: latestDiscovery.cycleId ?? null,
          timestamp: new Date(latestDiscovery.requestedAt).toISOString(),
          date: todayStr,
        };
      }

      const latestInvalidation = await this.cyclesRepo.getLatestScheduleInvalidation?.();
      if (latestInvalidation) {
        this.milestones.lastScheduleInvalidation = {
          cycleId: latestInvalidation.cycleId ?? null,
          timestamp: new Date(latestInvalidation.requestedAt).toISOString(),
          reason: latestInvalidation.errorMessage ?? 'SCHEDULE_INVALIDATED',
        };
      }

      // 4. Hydrate latest schedule discovery slot marker from database
      const latestSlotMarker = await db.query.apiRequestLedger.findFirst({
        where: and(
          eq(schema.apiRequestLedger.provider, 'system'),
          eq(schema.apiRequestLedger.operation, 'DISCOVER_SCHEDULE_SLOT'),
          eq(schema.apiRequestLedger.success, true)
        ),
        orderBy: [desc(schema.apiRequestLedger.requestedAt)],
      });

      if (latestSlotMarker?.endpoint) {
        const match = latestSlotMarker.endpoint.match(/slot=([^&]+)/);
        if (match) {
          this.lastScheduleSyncSlot = match[1];
          this.lastScheduleSyncAt = new Date(latestSlotMarker.requestedAt);
          this.logger.info(`Hydrated last schedule sync slot from database: ${this.lastScheduleSyncSlot}`);
        }
      }
    } catch (err: any) {
      this.logger.warn(`Could not hydrate scheduler state from database: ${err?.message}`);
    }
  }

  /**
   * Returns a diagnostic snapshot of the scheduler.
   */
  getDiagnostics(): SchedulerDiagnostics {
    let currentState: 'IDLE' | 'EXECUTING' | 'SLEEPING' | 'STOPPED' = 'IDLE';
    if (!this.running) {
      currentState = 'STOPPED';
    } else if (this.executingCycle) {
      currentState = 'EXECUTING';
    } else if (this.nextRecommendedWakeAt && this.nextRecommendedWakeAt.getTime() > this.now().getTime()) {
      currentState = 'SLEEPING';
    }

    return {
      running: this.running,
      executingCycle: this.executingCycle,
      currentState,
      lastCycleAt: this.lastCycleAt ? this.lastCycleAt.toISOString() : null,
      lastCycleAction: this.lastCycleAction,
      lastCycleDurationMs: this.lastCycleDurationMs,
      lastCycleOutcome: this.lastCycleOutcome,
      consecutiveErrors: this.consecutiveErrors,
      nextRecommendedWakeAt: this.nextRecommendedWakeAt ? this.nextRecommendedWakeAt.toISOString() : null,
      nextPlannedAction: this.nextPlannedAction,
      lastCycle: this.lastCycleRecord,
      milestones: this.milestones,
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
   * Executes the 7-day schedule synchronization routine (Yesterday through Today + 5).
   * Runs strictly twice a day (midnight UTC and midday UTC) to capture postponed matches,
   * kickoff adjustments, and finalize past results without wasting daily quota.
   * Fetches days -1 through +5 individually using the date= parameter, paced by 9 seconds between queries
   * to guarantee never exceeding the MINUTE_CEILING (8 requests/minute).
   */
  /**
   * Executes the 3-day schedule synchronization routine (Yesterday, Today, Tomorrow).
   * Runs strictly twice a day (midnight UTC and midday UTC) to capture postponed matches,
   * kickoff adjustments, and finalize past results without wasting daily quota.
   * Fetches days -1 through +1 individually using the date= parameter, paced by 9 seconds between queries
   * to guarantee never exceeding the MINUTE_CEILING (8 requests/minute).
   */
  async sync3DaySchedule(currentDate: Date = this.now(), cycleId?: string): Promise<SyncSummary> {
    const todayDateStr = currentDate.toISOString().slice(0, 10);
    const targetComps = this.targetCompetitionIds;
    this.lastScheduleSyncAttemptAt = currentDate;

    const combinedSummary: SyncSummary = {
      provider: 'unknown',
      fixturesDiscovered: 0,
      fixturesInserted: 0,
      fixturesUpdated: 0,
      eventsInserted: 0,
      eventsSkippedDuplicates: 0,
      hasChanges: false,
    };

    this.logger.info(`[3-DAY-SLIDING-WINDOW] Starting paced 3-day schedule sync (-1 to +1 days) for base date ${todayDateStr}...`);

    for (let offset = -1; offset <= 1; offset++) {
      const d = new Date(Date.UTC(
        currentDate.getUTCFullYear(),
        currentDate.getUTCMonth(),
        currentDate.getUTCDate() + offset
      ));
      const targetDateStr = d.toISOString().slice(0, 10);
      const offsetLabel = offset === 0 ? 'today' : (offset > 0 ? `+${offset}` : `${offset}`);

      try {
        const daySummary = await this.syncService.syncSchedule(targetDateStr, targetComps, cycleId);
        if (daySummary) {
          combinedSummary.provider = daySummary.provider || combinedSummary.provider;
          combinedSummary.fixturesDiscovered += daySummary.fixturesDiscovered ?? 0;
          combinedSummary.fixturesInserted += daySummary.fixturesInserted ?? 0;
          combinedSummary.fixturesUpdated += daySummary.fixturesUpdated ?? 0;
          combinedSummary.eventsInserted += daySummary.eventsInserted ?? 0;
          combinedSummary.eventsSkippedDuplicates += daySummary.eventsSkippedDuplicates ?? 0;
          if (daySummary.hasChanges) {
            combinedSummary.hasChanges = true;
          }
        }

        if (typeof this.fixturesRepo.markScheduleDiscovered === 'function') {
          await this.fixturesRepo.markScheduleDiscovered(targetDateStr, currentDate, cycleId);
        }

        this.logger.info(`Fetched day ${offsetLabel} (${targetDateStr})... waiting 9s`);

        // Pacing delay: exactly 9 seconds between each iteration (skip delay after final iteration)
        if (offset < 1) {
          await new Promise((resolve) => setTimeout(resolve, 9000));
        }
      } catch (err: any) {
        this.logger.warn(`Failed schedule sync for day ${offsetLabel} (${targetDateStr}): ${err?.message || err}`);
        // Fault-tolerant: log warning but do not throw, allowing the routine to proceed and commit slot
      }
    }

    // Unconditional slot commitment
    const slotKey = `${todayDateStr}-${currentDate.getUTCHours() < 12 ? '00' : '12'}`;
    this.lastScheduleSyncSlot = slotKey;
    this.lastScheduleSyncAt = currentDate;

    // Persist durable slot record to database
    try {
      await db.insert(schema.apiRequestLedger).values({
        id: `slot_${slotKey}_${Date.now()}_${Math.random().toString(36).substring(2, 7)}`,
        cycleId: cycleId ?? null,
        provider: 'system',
        endpoint: `/schedule?slot=${slotKey}`,
        operation: 'DISCOVER_SCHEDULE_SLOT',
        priority: 'NORMAL',
        success: true,
        httpStatus: 200,
        responseTimeMs: 0,
        costUnits: 0,
        requestedAt: currentDate,
      }).onConflictDoNothing();
    } catch (err: any) {
      this.logger.warn(`Failed persisting schedule slot record: ${err?.message}`);
    }

    return combinedSummary;
  }

  /**
   * Backwards-compatibility alias for 3-day schedule sync routine.
   */
  async sync7DaySchedule(currentDate: Date = this.now(), cycleId?: string): Promise<SyncSummary> {
    return this.sync3DaySchedule(currentDate, cycleId);
  }

  /**
   * Public trigger for the twice-daily schedule sync routine.
   */
  async executeScheduleSyncRoutine(now?: Date): Promise<SyncSummary> {
    const targetDate = now ?? this.now();
    return this.sync3DaySchedule(targetDate);
  }

  /**
   * Executes a single evaluation and sync cycle.
   * Can be called directly for testing or one-shot sync execution.
   */
  async executeSingleCycle(options?: { sync7Days?: boolean }): Promise<{ plan: SyncPlan; summary: SyncSummary | null }> {
    if (this.executingCycle) {
      throw new Error('Cannot execute cycle: another cycle is already in progress.');
    }

    const startTime = Date.now();
    this.executingCycle = true;
    let cycleId: string | undefined;
    let plan: SyncPlan | undefined;
    let syncSummary: SyncSummary | null = null;
    let targetComps = this.targetCompetitionIds;

    try {
      const currentDate = this.now();
      const todayDateStr = currentDate.toISOString().slice(0, 10);

      // 1. Fetch current budget state
      const budget = await this.getBudget(this.providerName, currentDate);

      // 2. Fetch fixture summary from repository with dynamic user preferences
      const prefState = this.preferencesService?.getPreferences();
      const summary = await this.fixturesRepo.getActiveSummary(currentDate, {
        followedTeamIds: prefState?.followedTeams,
        followedCompetitionIds: prefState?.followedCompetitions ?? this.targetCompetitionIds,
        pinnedMatchIds: prefState?.pinnedMatchIds ?? Array.from(this.pinnedMatchIds),
      });

      // 3. Evaluate pure acquisition policy
      plan = AcquisitionPolicyEngine.evaluateSyncPlan(budget, summary, currentDate);

      // 3b. Scheduler Bootstrapping: If policy evaluates to IDLE but database has zero fixtures
      // for the active target date (e.g. freshly seeded catalog or purged fixtures), generate a
      // POLL_SCHEDULE / bootstrap discovery plan to immediately download today's fixtures rather than idling.
      if (plan.action === 'IDLE' && budget.remainingDaily > 10) {
        let missingScheduleIn3DayWindow = false;
        try {
          for (let offset = -1; offset <= 1; offset++) {
            const targetD = new Date(Date.UTC(
              currentDate.getUTCFullYear(),
              currentDate.getUTCMonth(),
              currentDate.getUTCDate() + offset
            ));
            const targetDateStr = targetD.toISOString().slice(0, 10);
            let hasDayFixtures = false;

            if (typeof (this.fixturesRepo as any).hasFixturesForDate === 'function') {
              hasDayFixtures = await (this.fixturesRepo as any).hasFixturesForDate(targetDateStr);
            } else if (this.fixturesRepo instanceof FixturesRepository) {
              const startOfDay = new Date(`${targetDateStr}T00:00:00.000Z`);
              const endOfDay = new Date(startOfDay.getTime() + 24 * 60 * 60 * 1000);
              const fixturesDay = await db
                .select({ id: schema.fixtures.id })
                .from(schema.fixtures)
                .where(
                  and(
                    gte(schema.fixtures.kickoffTime, startOfDay),
                    lt(schema.fixtures.kickoffTime, endOfDay)
                  )
                )
                .limit(1);
              hasDayFixtures = fixturesDay.length > 0;
            } else {
              hasDayFixtures = summary.scheduleKnownForToday;
            }

            if (!hasDayFixtures) {
              missingScheduleIn3DayWindow = true;
              break;
            }
          }
        } catch {
          missingScheduleIn3DayWindow = false;
        }

        if (missingScheduleIn3DayWindow) {
          plan = {
            action: 'DISCOVER_SCHEDULE',
            priority: 'NORMAL',
            recommendedNextPollMs: 3600_000,
            reason: `3-day sliding window [-1, 0, 1] has zero fixtures for one or more dates. Bootstrapping 3-day schedule.`,
          };
        }
      }

      // 4. Generate cycleId and create record in DB (completed_at and outcome remain NULL in-flight)
      cycleId = this.cyclesRepo.generateCycleId(currentDate);
      try {
        await this.cyclesRepo.createCycle({
          id: cycleId,
          action: plan.action,
          priority: plan.priority,
          reason: plan.reason,
          targetCompetitionIds: targetComps,
          startedAt: currentDate,
        });
      } catch (err: any) {
        this.logger.warn(`Failed to create scheduler cycle record in database: ${err?.message}`);
      }

      this.logger.info(`Evaluated sync plan: ${plan.action}`, {
        cycleId,
        action: plan.action,
        priority: plan.priority,
        recommendedNextPollMs: plan.recommendedNextPollMs,
        reason: plan.reason,
        remainingBudget: budget.remainingDaily,
      });

      // 5. Dispatch action via SyncService
      if (plan.action !== 'IDLE') {
        if (plan.action === 'RECOVER_STALE') {
          if (summary.staleCompetitionIds && summary.staleCompetitionIds.length > 0) {
            targetComps = summary.staleCompetitionIds.filter(
              (id) => !this.targetCompetitionIds || this.targetCompetitionIds.includes(id)
            );
          } else if (summary.staleCompetitionIds && summary.staleCompetitionIds.length === 0) {
            targetComps = [];
          } else {
            targetComps = this.targetCompetitionIds;
          }
        }

        if (plan.action === 'RECOVER_STALE' && targetComps && targetComps.length === 0) {
          this.logger.warn('RECOVER_STALE plan triggered but no target competition IDs match stale fixtures. Skipping provider call.');
          syncSummary = null;
        } else if (
          plan.action === 'DISCOVER_SCHEDULE' ||
          (plan.action as string) === 'POLL_SCHEDULE' ||
          (plan.action as string) === 'BOOTSTRAP'
        ) {
          if (plan.reason.includes('Twice-daily') || plan.reason.includes('Bootstrapping') || (options && options.sync7Days)) {
            syncSummary = await this.sync3DaySchedule(currentDate, cycleId);
          } else {
            syncSummary = await this.syncService.syncWithPlan(plan, {
              date: todayDateStr,
              competitionIds: targetComps,
              cycleId,
            });
            if (typeof this.fixturesRepo.markScheduleDiscovered === 'function') {
              await this.fixturesRepo.markScheduleDiscovered(todayDateStr, currentDate, cycleId);
            }
          }
        } else {
          syncSummary = await this.syncService.syncWithPlan(plan, {
            date: todayDateStr,
            competitionIds: targetComps,
            cycleId,
          });

          if (plan.action === 'POLL_LIVE') {
            // The Halftime (HT) Suspend Detection:
            // If live match just entered HT status, pause polling/burst timer for 15-minute break
            const postSyncSummary = await this.fixturesRepo.getActiveSummary(currentDate, {
              followedTeamIds: prefState?.followedTeams,
              followedCompetitionIds: prefState?.followedCompetitions ?? this.targetCompetitionIds,
              pinnedMatchIds: prefState?.pinnedMatchIds ?? Array.from(this.pinnedMatchIds),
            });

            const isHtSuspended = postSyncSummary.hasPinnedOrFollowedLive
              ? (postSyncSummary.pinnedOrFollowedInHalftime ?? postSyncSummary.allLiveInHalftime)
              : postSyncSummary.allLiveInHalftime;

            if (isHtSuspended && postSyncSummary.liveCount > 0) {
              const remainingBreak = postSyncSummary.halftimeRemainingMs ?? AcquisitionPolicyEngine.HALFTIME_SUSPEND_MS;
              plan.recommendedNextPollMs = remainingBreak;
              plan.reason = 'Live match entered HT (Halftime). Polling suspended for 15-minute break.';
              this.logger.info(`Halftime suspend activated: live match is in HT. Pausing polling for ${Math.round(remainingBreak / 1000)}s.`);
            }
          }
        }

        if (plan.action === 'DISCOVER_SCHEDULE' || (plan.action as string) === 'POLL_SCHEDULE' || (plan.action as string) === 'BOOTSTRAP') {
          if (typeof this.fixturesRepo.markScheduleDiscovered === 'function') {
            await this.fixturesRepo.markScheduleDiscovered(todayDateStr, currentDate, cycleId);
          }
        }

        this.logger.info(`Sync cycle completed for ${plan.action}`, {
          cycleId,
          fixturesDiscovered: syncSummary?.fixturesDiscovered ?? 0,
          fixturesInserted: syncSummary?.fixturesInserted ?? 0,
          fixturesUpdated: syncSummary?.fixturesUpdated ?? 0,
          eventsInserted: syncSummary?.eventsInserted ?? 0,
        });
      } else {
        this.logger.info(`System idle: ${plan.reason}`);
      }

      // 6. Determine final cycle outcome (strictly one of the 6 defined outcomes)
      let outcome: SchedulerCycleOutcome;
      if (plan.action === 'IDLE') {
        if (budget.remainingDaily <= 10 || plan.reason.includes('reserve')) {
          outcome = 'QUOTA_BLOCKED';
        } else {
          outcome = 'IDLE';
        }
      } else {
        // Formal outcome semantics:
        // - SUCCESS: Provider action completed successfully and resulted in meaningful domain/entity mutations (fixtures/events inserted or updated).
        // - NO_OP: Provider action completed successfully but produced no meaningful domain/entity mutation.
        //   (Note: NO_OP does NOT mean 'no database write occurred', because system markers and cycle records are written).
        const hasMutations =
          (syncSummary?.fixturesInserted ?? 0) > 0 ||
          (syncSummary?.fixturesUpdated ?? 0) > 0 ||
          (syncSummary?.eventsInserted ?? 0) > 0;
        outcome = hasMutations ? 'SUCCESS' : 'NO_OP';
      }

      // 7. Finalize cycle in DB
      let finalizedRecord = null;
      try {
        finalizedRecord = await this.cyclesRepo.finalizeCycle(cycleId, {
          completedAt: this.now(),
          durationMs: Date.now() - startTime,
          outcome,
          fixturesDiscovered: syncSummary?.fixturesDiscovered ?? 0,
          fixturesUpdated: (syncSummary?.fixturesUpdated ?? 0) + (syncSummary?.fixturesInserted ?? 0),
        });
      } catch (err: any) {
        this.logger.warn(`Failed to finalize scheduler cycle in database: ${err?.message}`);
      }

      // 8. Update milestone metrics
      if ((plan.action === 'DISCOVER_SCHEDULE' || (plan.action as string) === 'POLL_SCHEDULE' || (plan.action as string) === 'BOOTSTRAP') && (outcome === 'SUCCESS' || outcome === 'NO_OP')) {
        this.milestones.lastSuccessfulDiscovery = {
          cycleId,
          timestamp: currentDate.toISOString(),
          date: todayDateStr,
        };
      } else if (plan.action === 'POLL_LIVE') {
        this.milestones.lastLivePoll = {
          cycleId,
          timestamp: currentDate.toISOString(),
          matchesUpdated: syncSummary?.fixturesUpdated ?? 0,
        };
      } else if (plan.action === 'RECOVER_STALE') {
        this.milestones.lastStaleRecovery = {
          cycleId,
          timestamp: currentDate.toISOString(),
          competitionsRecovered: targetComps ?? [],
        };
      }

      // Update in-memory state metrics
      this.lastCycleAt = currentDate;
      this.lastCycleAction = plan.action;
      this.lastCycleDurationMs = Date.now() - startTime;
      this.lastCycleOutcome = outcome;
      this.lastCycleRecord = finalizedRecord ?? {
        id: cycleId,
        action: plan.action,
        priority: plan.priority,
        reason: plan.reason,
        targetCompetitionIds: targetComps,
        startedAt: currentDate.toISOString(),
        completedAt: this.now().toISOString(),
        durationMs: Date.now() - startTime,
        outcome,
        providerRequestsCount: 0,
        providerRequestsCost: 0,
        errorType: null,
        errorMessage: null,
        fixturesDiscovered: syncSummary?.fixturesDiscovered ?? 0,
        fixturesUpdated: (syncSummary?.fixturesUpdated ?? 0) + (syncSummary?.fixturesInserted ?? 0),
      };
      this.consecutiveErrors = 0;

      // 9. Notify cycle complete callback
      this.onCycleComplete?.(plan, syncSummary);

      return { plan, summary: syncSummary };
    } catch (err: any) {
      this.consecutiveErrors++;
      const durationMs = Date.now() - startTime;
      this.lastCycleAt = this.now();
      this.lastCycleDurationMs = durationMs;
      this.lastCycleOutcome = 'ERROR';

      // Classify error type
      const errMsg = err?.message || String(err);
      let errorType: SchedulerErrorType = 'PROVIDER_ERROR';
      let outcome: SchedulerCycleOutcome = 'ERROR';

      if (errMsg.includes('RATE_LIMIT') || errMsg.includes('MINUTE_CEILING')) {
        errorType = 'RATE_LIMITED';
        outcome = 'RATE_LIMITED';
      } else if (errMsg.includes('BUDGET') || errMsg.includes('QUOTA') || errMsg.includes('EXHAUSTED')) {
        errorType = 'QUOTA_EXHAUSTED';
        outcome = 'QUOTA_BLOCKED';
      } else if (errMsg.includes('database') || errMsg.includes('postgres') || errMsg.includes('sqlite') || errMsg.includes('pool') || errMsg.includes('connection')) {
        errorType = 'DATABASE_ERROR';
        outcome = 'ERROR';
      }

      if (cycleId) {
        try {
          const finalizedRecord = await this.cyclesRepo.finalizeCycle(cycleId, {
            completedAt: this.now(),
            durationMs,
            outcome,
            errorType,
            errorMessage: errMsg,
          });
          this.lastCycleRecord = finalizedRecord;
        } catch {
          // ignore DB error while logging cycle error
        }
      }

      throw err;
    } finally {
      this.executingCycle = false;
    }
  }

  /**
   * Explicitly prunes old IDLE cycles based on retention policy.
   */
  async pruneIdleCycles(olderThanDays = 7): Promise<number> {
    const pruned = await this.cyclesRepo.pruneIdleCycles(olderThanDays, this.now());
    this.logger.info(`Pruned ${pruned} IDLE cycle(s) older than ${olderThanDays} days.`);
    return pruned;
  }

  /**
   * Checks whether a cold-boot 7-day bootstrap sync is required.
   * Returns true if database has 0 fixtures overall or lacks fixtures for the current date,
   * definitively triggering the mandatory 7-day schedule sync routine.
   */
  async isBootstrapSyncNeeded(currentDate: Date = this.now()): Promise<boolean> {
    const todayStr = currentDate.toISOString().slice(0, 10);
    try {
      // 1. Total fixtures count check
      let totalFixtures = 0;
      if (typeof (this.fixturesRepo as any).countTotalFixtures === 'function') {
        totalFixtures = await (this.fixturesRepo as any).countTotalFixtures();
      } else {
        const result = await db.select({ count: sql<number>`count(*)` }).from(schema.fixtures);
        totalFixtures = Number(result[0]?.count ?? 0);
      }

      if (totalFixtures === 0) {
        this.logger.info('[DEBUG] Executing mandatory 7-day bootstrap sync: Database fixtures table is empty (0 fixtures).');
        return true;
      }

      // 1b. Mock eradication safeguard: check non-mock fixtures if repo supports it
      if (typeof (this.fixturesRepo as any).countNonMockFixtures === 'function') {
        const nonMock = await (this.fixturesRepo as any).countNonMockFixtures();
        if (nonMock === 0) {
          this.logger.info('[DEBUG] Executing mandatory 7-day bootstrap sync: Zero real non-mock fixtures present in database.');
          return true;
        }
      }

      // 2. Check if fixtures exist for all days in 3-day sliding window [-1, 0, 1]
      for (let offset = -1; offset <= 1; offset++) {
        const targetD = new Date(Date.UTC(
          currentDate.getUTCFullYear(),
          currentDate.getUTCMonth(),
          currentDate.getUTCDate() + offset
        ));
        const targetStr = targetD.toISOString().slice(0, 10);
        let hasDay = false;

        if (typeof (this.fixturesRepo as any).hasFixturesForDate === 'function') {
          hasDay = await (this.fixturesRepo as any).hasFixturesForDate(targetStr);
        } else {
          const startOfDay = new Date(`${targetStr}T00:00:00.000Z`);
          const endOfDay = new Date(startOfDay.getTime() + 24 * 60 * 60 * 1000);
          const rows = await db
            .select({ id: schema.fixtures.id })
            .from(schema.fixtures)
            .where(
              and(
                gte(schema.fixtures.kickoffTime, startOfDay),
                lt(schema.fixtures.kickoffTime, endOfDay),
                notLike(schema.fixtures.id, '%mock%'),
                notInArray(schema.fixtures.id, ['fix_elclasico_2026', 'fix_spain_egypt_2026'])
              )
            )
            .limit(1);
          hasDay = rows.length > 0;
        }

        if (!hasDay) {
          this.logger.info(`[DEBUG] Executing mandatory 3-day bootstrap sync: No fixtures found for day offset ${offset} (${targetStr}).`);
          return true;
        }
      }

      return false;
    } catch (err: any) {
      this.logger.warn(`Could not verify bootstrap fixtures status: ${err?.message}`);
      this.logger.info('[DEBUG] Executing mandatory 7-day bootstrap sync: Fallback triggered on verification error.');
      return true;
    }
  }

  /**
   * Internal autonomous background loop.
   */
  private async runLoop(signal: AbortSignal): Promise<void> {
    // 1. Initial hydration and recovery on boot
    await this.hydrateStateFromDatabase();

    // 2. Cold-boot check: if database is empty or current day lacks data,
    // sync7DaySchedule() runs immediately and synchronously before standard live loop starts.
    if (this.running && !signal.aborted) {
      const needsBootstrap = await this.isBootstrapSyncNeeded(this.now());
      if (needsBootstrap) {
        this.logger.info('[DEBUG] Executing mandatory 3-day bootstrap sync: Cold boot detected (empty or missing today data). Running immediate 3-day schedule sync (-1 to +1 days)...');
        try {
          await this.sync3DaySchedule(this.now());
          this.logger.info('[DEBUG] Executing mandatory 3-day bootstrap sync: Completed successfully.');
        } catch (coldBootErr: any) {
          this.logger.warn(`Cold-boot 3-day schedule sync encountered an error: ${coldBootErr?.message}`);
        }
      }
    }

    while (this.running && !signal.aborted) {
      let nextWaitMs = AcquisitionScheduler.DEFAULT_IDLE_DELAY_MS;

      try {
        // Twice-Daily Schedule Sync Background Routine:
        // Strictly twice a day (midnight UTC and midday), fetch and cache 3-day schedule (-1 to +1).
        if (this.isTwiceDailyScheduleSyncDue(this.now())) {
          this.logger.info('Executing twice-daily 3-day schedule sync routine (midnight/midday UTC).');
          try {
            await this.sync3DaySchedule(this.now());
          } catch (syncErr: any) {
            this.logger.warn(`Twice-daily schedule sync routine encountered an error: ${syncErr?.message}`);
          }
        }

        const { plan } = await this.executeSingleCycle();

        // Calculate next polling delay from policy recommendation
        if (plan.recommendedNextPollMs !== null && plan.recommendedNextPollMs > 0) {
          nextWaitMs = plan.recommendedNextPollMs;
        } else {
          nextWaitMs = AcquisitionScheduler.DEFAULT_IDLE_DELAY_MS;
        }

        this.nextPlannedAction = {
          action: plan.action,
          cadenceMs: nextWaitMs,
          wakeAt: new Date(this.now().getTime() + nextWaitMs).toISOString(),
          reason: plan.reason,
        };
      } catch (err: any) {
        const error = err instanceof Error ? err : new Error(String(err));
        this.logger.error(`Error during scheduler execution cycle: ${error.message}`, {
          error: error.message,
          stack: error.stack,
        });

        this.onErrorCallback?.(error);

        // Safe error backoff to prevent tight retry loops on error
        nextWaitMs = AcquisitionScheduler.DEFAULT_ERROR_BACKOFF_MS;
        this.nextPlannedAction = {
          action: 'ERROR_BACKOFF',
          cadenceMs: nextWaitMs,
          wakeAt: new Date(this.now().getTime() + nextWaitMs).toISOString(),
          reason: `Error backoff: ${error.message}`,
        };
      }

      if (!this.running || signal.aborted) {
        break;
      }

      this.nextRecommendedWakeAt = new Date(this.now().getTime() + nextWaitMs);
      this.logger.info(`Scheduler waiting ${Math.round(nextWaitMs / 1000)}s until next evaluation cycle.`);
      await this.sleep(nextWaitMs, signal);
    }
  }
}
