import { describe, it, expect, vi, beforeEach } from 'vitest';
import {
  AcquisitionScheduler,
  getDatabaseBudgetContext,
} from '../src/acquisition/acquisition-scheduler.js';
import { SyncService, SyncSummary } from '../src/acquisition/sync.service.js';
import { FixturesRepository } from '../src/repositories/fixtures.repository.js';
import { BudgetContext, FixturesSummary, SyncPlan } from '../src/acquisition/acquisition-policy.js';

describe('AcquisitionScheduler - Budget-Aware Background Scheduler', () => {
  const silentLogger = {
    info: () => {},
    warn: () => {},
    error: () => {},
  };

  const createDummySyncSummary = (action: string): SyncSummary => ({
    provider: 'mock',
    fixturesDiscovered: 2,
    fixturesInserted: 0,
    fixturesUpdated: 1,
    eventsInserted: 0,
    eventsSkippedDuplicates: 0,
    hasChanges: true,
  });

  const createMockCyclesRepo = () => ({
    generateCycleId: () => 'cycle_mock_test',
    createCycle: vi.fn().mockResolvedValue({}),
    finalizeCycle: vi.fn().mockResolvedValue({}),
    recoverInterruptedCycles: vi.fn().mockResolvedValue(0),
    getLatestCycle: vi.fn().mockResolvedValue(null),
    getCorrelatedRequestStats: vi.fn().mockResolvedValue({ requestsCount: 0, requestsCost: 0 }),
    pruneIdleCycles: vi.fn().mockResolvedValue(0),
  });

  // ============================================================
  // 1. Lifecycle: start(), stop(), isRunning()
  // ============================================================
  describe('Lifecycle and Idempotency', () => {
    it('starts scheduler loop and transitions isRunning to true', async () => {
      let sleepCallCount = 0;
      const customSleep = vi.fn().mockImplementation(async (ms, signal) => {
        sleepCallCount++;
        // Keep waiting until aborted
        return new Promise<void>((resolve) => {
          signal?.addEventListener('abort', () => resolve());
        });
      });

      const mockSyncService: any = { syncWithPlan: vi.fn().mockResolvedValue(null) };
      const mockRepo: any = {
        getActiveSummary: vi.fn().mockResolvedValue({
          liveCount: 0,
          recentlyFinishedCount: 0,
          stalePrematchCount: 0,
          scheduleKnownForToday: true,
        }),
      };

      const scheduler = new AcquisitionScheduler({
        syncService: mockSyncService,
        fixturesRepository: mockRepo,
        getBudgetContext: async () => ({ consumedDaily: 10, remainingDaily: 90 }),
        sleepFn: customSleep,
        logger: silentLogger,
      });

      expect(scheduler.isRunning()).toBe(false);
      scheduler.start();
      expect(scheduler.isRunning()).toBe(true);

      await scheduler.stop();
      expect(scheduler.isRunning()).toBe(false);
    });

    it('duplicate start() calls do not create multiple concurrent loops', async () => {
      let cycleCount = 0;
      const customSleep = vi.fn().mockImplementation(async (ms, signal) => {
        return new Promise<void>((resolve) => {
          signal?.addEventListener('abort', () => resolve());
        });
      });

      const mockSyncService: any = { syncWithPlan: vi.fn().mockResolvedValue(null) };
      const mockRepo: any = {
        getActiveSummary: vi.fn().mockImplementation(async () => {
          cycleCount++;
          return {
            liveCount: 0,
            recentlyFinishedCount: 0,
            stalePrematchCount: 0,
            scheduleKnownForToday: true,
          };
        }),
      };

      const scheduler = new AcquisitionScheduler({
        syncService: mockSyncService,
        fixturesRepository: mockRepo,
        getBudgetContext: async () => ({ consumedDaily: 10, remainingDaily: 90 }),
        sleepFn: customSleep,
        logger: silentLogger,
      });

      scheduler.start();
      scheduler.start(); // Second call should be a no-op
      scheduler.start(); // Third call should be a no-op

      // Wait a microtask
      await new Promise((r) => setTimeout(r, 10));

      expect(cycleCount).toBe(1);

      await scheduler.stop();
    });

    it('stop() cancels a pending sleep immediately without waiting for timeout', async () => {
      const startTime = Date.now();
      let aborted = false;
      let sleepEntered!: () => void;
      const sleepEnteredPromise = new Promise<void>((r) => { sleepEntered = r; });

      const customSleep = vi.fn().mockImplementation(async (ms, signal) => {
        sleepEntered();
        return new Promise<void>((resolve) => {
          if (signal?.aborted) {
            aborted = true;
            resolve();
            return;
          }
          const timer = setTimeout(resolve, 600_000); // 10 minutes
          signal?.addEventListener('abort', () => {
            clearTimeout(timer);
            aborted = true;
            resolve();
          });
        });
      });

      const mockSyncService: any = { syncWithPlan: vi.fn().mockResolvedValue(null) };
      const mockRepo: any = {
        getActiveSummary: vi.fn().mockResolvedValue({
          liveCount: 0,
          recentlyFinishedCount: 0,
          stalePrematchCount: 0,
          scheduleKnownForToday: true,
        }),
      };

      const scheduler = new AcquisitionScheduler({
        syncService: mockSyncService,
        fixturesRepository: mockRepo,
        getBudgetContext: async () => ({ consumedDaily: 10, remainingDaily: 90 }),
        sleepFn: customSleep,
        logger: silentLogger,
      });

      scheduler.start();
      await sleepEnteredPromise;

      // Stop should abort immediately
      await scheduler.stop();
      const elapsed = Date.now() - startTime;

      expect(aborted).toBe(true);
      expect(elapsed).toBeLessThan(500); // Exited immediately
      expect(scheduler.isRunning()).toBe(false);
    });
  });

  // ============================================================
  // 2. Policy-Driven Cadence & Action Dispatch
  // ============================================================
  describe('Policy Cadence & Action Execution', () => {
    it('dispatches POLL_LIVE and waits for policy-recommended interval (180s for budget > 50)', async () => {
      let sleptMs = 0;
      const customSleep = vi.fn().mockImplementation(async (ms, signal) => {
        sleptMs = ms;
        return new Promise<void>((resolve) => {
          signal?.addEventListener('abort', () => resolve());
        });
      });

      let dispatchedPlan: SyncPlan | null = null;
      const mockSyncService: any = {
        syncWithPlan: vi.fn().mockImplementation(async (plan) => {
          dispatchedPlan = plan;
          return createDummySyncSummary('POLL_LIVE');
        }),
      };

      const mockRepo: any = {
        getActiveSummary: vi.fn().mockResolvedValue({
          liveCount: 2,
          recentlyFinishedCount: 0,
          stalePrematchCount: 0,
          scheduleKnownForToday: true,
        }),
      };

      const scheduler = new AcquisitionScheduler({
        syncService: mockSyncService,
        fixturesRepository: mockRepo,
        getBudgetContext: async () => ({ consumedDaily: 20, remainingDaily: 80 }),
        sleepFn: customSleep,
        logger: silentLogger,
      });

      scheduler.start();
      await new Promise((r) => setTimeout(r, 10));

      expect(dispatchedPlan?.action).toBe('POLL_LIVE');
      expect(mockSyncService.syncWithPlan).toHaveBeenCalledTimes(1);
      expect(sleptMs).toBe(180_000); // 180s fast cadence from policy

      await scheduler.stop();
    });

    it('dispatches DISCOVER_SCHEDULE when schedule is unknown today', async () => {
      let dispatchedPlan: SyncPlan | null = null;
      const mockSyncService: any = {
        syncWithPlan: vi.fn().mockImplementation(async (plan) => {
          dispatchedPlan = plan;
          return createDummySyncSummary('DISCOVER_SCHEDULE');
        }),
      };

      const mockRepo: any = {
        getActiveSummary: vi.fn().mockResolvedValue({
          liveCount: 0,
          recentlyFinishedCount: 0,
          stalePrematchCount: 0,
          scheduleKnownForToday: false, // Today's schedule unknown
        }),
      };

      const scheduler = new AcquisitionScheduler({
        syncService: mockSyncService,
        fixturesRepository: mockRepo,
        getBudgetContext: async () => ({ consumedDaily: 10, remainingDaily: 90 }),
        sleepFn: vi.fn().mockImplementation(async (_, signal) => {
          return new Promise<void>((r) => signal?.addEventListener('abort', () => r()));
        }),
        logger: silentLogger,
      });

      scheduler.start();
      await new Promise((r) => setTimeout(r, 10));

      expect(dispatchedPlan?.action).toBe('DISCOVER_SCHEDULE');
      expect(mockSyncService.syncWithPlan).toHaveBeenCalledTimes(1);

      await scheduler.stop();
    });

    it('dispatches FINALIZE when recently finished match exists', async () => {
      let dispatchedPlan: SyncPlan | null = null;
      const mockSyncService: any = {
        syncWithPlan: vi.fn().mockImplementation(async (plan) => {
          dispatchedPlan = plan;
          return createDummySyncSummary('FINALIZE');
        }),
      };

      const mockRepo: any = {
        getActiveSummary: vi.fn().mockResolvedValue({
          liveCount: 0,
          recentlyFinishedCount: 1,
          stalePrematchCount: 0,
          scheduleKnownForToday: true,
        }),
      };

      const scheduler = new AcquisitionScheduler({
        syncService: mockSyncService,
        fixturesRepository: mockRepo,
        getBudgetContext: async () => ({ consumedDaily: 10, remainingDaily: 90 }),
        sleepFn: vi.fn().mockImplementation(async (_, signal) => {
          return new Promise<void>((r) => signal?.addEventListener('abort', () => r()));
        }),
        logger: silentLogger,
      });

      scheduler.start();
      await new Promise((r) => setTimeout(r, 10));

      expect(dispatchedPlan?.action).toBe('FINALIZE');
      expect(mockSyncService.syncWithPlan).toHaveBeenCalledTimes(1);

      await scheduler.stop();
    });

    it('dispatches RECOVER_STALE when stale prematch match exists', async () => {
      let dispatchedPlan: SyncPlan | null = null;
      const mockSyncService: any = {
        syncWithPlan: vi.fn().mockImplementation(async (plan) => {
          dispatchedPlan = plan;
          return createDummySyncSummary('RECOVER_STALE');
        }),
      };

      const mockRepo: any = {
        getActiveSummary: vi.fn().mockResolvedValue({
          liveCount: 0,
          recentlyFinishedCount: 0,
          stalePrematchCount: 1,
          scheduleKnownForToday: true,
        }),
      };

      const scheduler = new AcquisitionScheduler({
        syncService: mockSyncService,
        fixturesRepository: mockRepo,
        getBudgetContext: async () => ({ consumedDaily: 10, remainingDaily: 90 }),
        sleepFn: vi.fn().mockImplementation(async (_, signal) => {
          return new Promise<void>((r) => signal?.addEventListener('abort', () => r()));
        }),
        logger: silentLogger,
      });

      scheduler.start();
      await new Promise((r) => setTimeout(r, 10));

      expect(dispatchedPlan?.action).toBe('RECOVER_STALE');
      expect(mockSyncService.syncWithPlan).toHaveBeenCalledTimes(1);

      await scheduler.stop();
    });
  });

  // ============================================================
  // 3. Critical Reserve Protection & Idle Behavior
  // ============================================================
  describe('Critical Reserve & Idle Protection', () => {
    it('does NOT call SyncService when remaining budget is at critical reserve (10)', async () => {
      const mockSyncService: any = { syncWithPlan: vi.fn() };
      const mockRepo: any = {
        getActiveSummary: vi.fn().mockResolvedValue({
          liveCount: 3, // Live matches exist!
          recentlyFinishedCount: 0,
          stalePrematchCount: 0,
          scheduleKnownForToday: true,
        }),
      };

      const scheduler = new AcquisitionScheduler({
        syncService: mockSyncService,
        fixturesRepository: mockRepo,
        getBudgetContext: async () => ({ consumedDaily: 90, remainingDaily: 10 }), // Reserve <= 10
        sleepFn: vi.fn().mockImplementation(async (_, signal) => {
          return new Promise<void>((r) => signal?.addEventListener('abort', () => r()));
        }),
        logger: silentLogger,
      });

      scheduler.start();
      await new Promise((r) => setTimeout(r, 10));

      // SyncService must NOT be invoked when reserve is protected
      expect(mockSyncService.syncWithPlan).not.toHaveBeenCalled();

      await scheduler.stop();
    });

    it('does NOT call SyncService when system is completely IDLE', async () => {
      const mockSyncService: any = { syncWithPlan: vi.fn() };
      const mockRepo: any = {
        getActiveSummary: vi.fn().mockResolvedValue({
          liveCount: 0,
          recentlyFinishedCount: 0,
          stalePrematchCount: 0,
          scheduleKnownForToday: true,
        }),
      };

      const scheduler = new AcquisitionScheduler({
        syncService: mockSyncService,
        fixturesRepository: mockRepo,
        getBudgetContext: async () => ({ consumedDaily: 20, remainingDaily: 80 }),
        sleepFn: vi.fn().mockImplementation(async (_, signal) => {
          return new Promise<void>((r) => signal?.addEventListener('abort', () => r()));
        }),
        logger: silentLogger,
      });

      scheduler.start();
      await new Promise((r) => setTimeout(r, 10));

      expect(mockSyncService.syncWithPlan).not.toHaveBeenCalled();

      await scheduler.stop();
    });
  });

  // ============================================================
  // 4. No Cycle Overlap & Concurrency Safety
  // ============================================================
  describe('Cycle Overlap Invariant', () => {
    it('guarantees executeSingleCycle rejects if another cycle is actively executing', async () => {
      let resolveFirstCycle: () => void = () => {};
      const mockSyncService: any = {
        syncWithPlan: vi.fn().mockImplementation(() => {
          return new Promise<SyncSummary>((resolve) => {
            resolveFirstCycle = () => resolve(createDummySyncSummary('POLL_LIVE'));
          });
        }),
      };

      const mockRepo: any = {
        getActiveSummary: vi.fn().mockResolvedValue({
          liveCount: 1,
          recentlyFinishedCount: 0,
          stalePrematchCount: 0,
          scheduleKnownForToday: true,
        }),
      };

      const scheduler = new AcquisitionScheduler({
        syncService: mockSyncService,
        fixturesRepository: mockRepo,
        schedulerCyclesRepository: createMockCyclesRepo() as any,
        getBudgetContext: async () => ({ consumedDaily: 10, remainingDaily: 90 }),
        logger: silentLogger,
      });

      // Start first cycle (will pause inside syncWithPlan)
      const firstCyclePromise = scheduler.executeSingleCycle();

      // Immediate second call should throw concurrency rejection
      await expect(scheduler.executeSingleCycle()).rejects.toThrow(
        'another cycle is already in progress'
      );

      // Release first cycle
      resolveFirstCycle();
      await firstCyclePromise;
    });
  });

  // ============================================================
  // 5. Error Resilience & Recovery
  // ============================================================
  describe('Error Resilience', () => {
    it('survives an unexpected error in sync and continues to next cycle without crashing', async () => {
      let cycleCount = 0;
      let errorReported: Error | null = null;

      const mockSyncService: any = {
        syncWithPlan: vi.fn().mockImplementation(async () => {
          cycleCount++;
          if (cycleCount === 1) {
            throw new Error('Simulated gateway network timeout');
          }
          return createDummySyncSummary('POLL_LIVE');
        }),
      };

      const mockRepo: any = {
        getActiveSummary: vi.fn().mockResolvedValue({
          liveCount: 1,
          recentlyFinishedCount: 0,
          stalePrematchCount: 0,
          scheduleKnownForToday: true,
        }),
      };

      let sleepTrigger: () => void = () => {};
      const customSleep = vi.fn().mockImplementation(async (ms, signal) => {
        return new Promise<void>((resolve) => {
          sleepTrigger = resolve;
          signal?.addEventListener('abort', () => resolve());
        });
      });

      const scheduler = new AcquisitionScheduler({
        syncService: mockSyncService,
        fixturesRepository: mockRepo,
        schedulerCyclesRepository: createMockCyclesRepo() as any,
        getBudgetContext: async () => ({ consumedDaily: 10, remainingDaily: 90 }),
        sleepFn: customSleep,
        onError: (err) => {
          errorReported = err;
        },
        logger: silentLogger,
      });

      scheduler.start();
      await new Promise((r) => setTimeout(r, 10));

      // First cycle failed and reported error
      expect(errorReported).not.toBeNull();
      expect((errorReported as any)?.message).toContain('Simulated gateway network timeout');

      // Trigger sleep to proceed to second cycle
      sleepTrigger();
      await new Promise((r) => setTimeout(r, 10));

      // Second cycle succeeded
      expect(cycleCount).toBe(2);
      expect(scheduler.isRunning()).toBe(true);

      await scheduler.stop();
    });
  });

  // ============================================================
  // 6. Deterministic UTC & Date Handling
  // ============================================================
  describe('Deterministic UTC Time Injection', () => {
    it('uses injected nowFn to evaluate exact UTC date string', async () => {
      let capturedDateStr: string | undefined;

      const mockSyncService: any = {
        syncWithPlan: vi.fn().mockImplementation(async (plan, options) => {
          capturedDateStr = options?.date;
          return createDummySyncSummary('DISCOVER_SCHEDULE');
        }),
      };

      const mockRepo: any = {
        getActiveSummary: vi.fn().mockResolvedValue({
          liveCount: 0,
          recentlyFinishedCount: 0,
          stalePrematchCount: 0,
          scheduleKnownForToday: false,
        }),
      };

      const fixedDate = new Date('2026-11-25T23:45:00Z');

      const scheduler = new AcquisitionScheduler({
        syncService: mockSyncService,
        fixturesRepository: mockRepo,
        getBudgetContext: async () => ({ consumedDaily: 10, remainingDaily: 90 }),
        nowFn: () => fixedDate,
        logger: silentLogger,
      });

      await scheduler.executeSingleCycle();

      expect(capturedDateStr).toBe('2026-11-25');
    });
  });

  // ============================================================
  // 7. Milestone 16.5A - Starvation Prevention & Lifecycle Hardening
  // ============================================================
  describe('Milestone 16.5A - Starvation Prevention & Lifecycle Hardening', () => {
    it('does NOT starve DISCOVER_SCHEDULE when schedule is unknown today, even if live matches are reported', async () => {
      let executedAction: string | null = null;
      const mockSyncService: any = {
        syncWithPlan: vi.fn().mockImplementation(async (plan) => {
          executedAction = plan.action;
          return createDummySyncSummary(plan.action);
        }),
      };

      const mockRepo: any = {
        getActiveSummary: vi.fn().mockResolvedValue({
          liveCount: 2, // Live matches present
          recentlyFinishedCount: 0,
          stalePrematchCount: 0,
          scheduleKnownForToday: false, // But daily schedule not yet discovered
        }),
      };

      const scheduler = new AcquisitionScheduler({
        syncService: mockSyncService,
        fixturesRepository: mockRepo,
        getBudgetContext: async () => ({ consumedDaily: 10, remainingDaily: 90 }),
        logger: silentLogger,
      });

      await scheduler.executeSingleCycle();

      expect(executedAction).toBe('DISCOVER_SCHEDULE');
      expect(mockSyncService.syncWithPlan).toHaveBeenCalledTimes(1);
    });

    it('scheduler exits POLL_LIVE and transitions to IDLE when liveCount drops to 0', async () => {
      let executedActions: string[] = [];
      let currentLiveCount = 1;

      const mockSyncService: any = {
        syncWithPlan: vi.fn().mockImplementation(async (plan) => {
          executedActions.push(plan.action);
          // Simulate live match ending on first sync
          currentLiveCount = 0;
          return createDummySyncSummary(plan.action);
        }),
      };

      const mockRepo: any = {
        getActiveSummary: vi.fn().mockImplementation(async () => ({
          liveCount: currentLiveCount,
          recentlyFinishedCount: 0,
          stalePrematchCount: 0,
          scheduleKnownForToday: true,
        })),
      };

      const scheduler = new AcquisitionScheduler({
        syncService: mockSyncService,
        fixturesRepository: mockRepo,
        getBudgetContext: async () => ({ consumedDaily: 10, remainingDaily: 90 }),
        logger: silentLogger,
      });

      // Cycle 1: liveCount is 1 -> POLL_LIVE
      await scheduler.executeSingleCycle();
      expect(executedActions).toEqual(['POLL_LIVE']);

      // Cycle 2: liveCount is now 0 -> IDLE (no sync call)
      await scheduler.executeSingleCycle();
      expect(executedActions).toEqual(['POLL_LIVE']); // No new call to syncWithPlan
    });

    it('does not starve FINALIZE when liveCount drops to 0 and recentlyFinishedCount > 0', async () => {
      let executedAction: string | null = null;
      const mockSyncService: any = {
        syncWithPlan: vi.fn().mockImplementation(async (plan) => {
          executedAction = plan.action;
          return createDummySyncSummary(plan.action);
        }),
      };

      const mockRepo: any = {
        getActiveSummary: vi.fn().mockResolvedValue({
          liveCount: 0,
          recentlyFinishedCount: 2,
          stalePrematchCount: 0,
          scheduleKnownForToday: true,
        }),
      };

      const scheduler = new AcquisitionScheduler({
        syncService: mockSyncService,
        fixturesRepository: mockRepo,
        getBudgetContext: async () => ({ consumedDaily: 10, remainingDaily: 90 }),
        logger: silentLogger,
      });

      await scheduler.executeSingleCycle();
      expect(executedAction).toBe('FINALIZE');
    });

    it('critical reserve ceiling (10) strictly prevents POLL_LIVE even with 5 live matches', async () => {
      const mockSyncService: any = {
        syncWithPlan: vi.fn(),
      };

      const mockRepo: any = {
        getActiveSummary: vi.fn().mockResolvedValue({
          liveCount: 5,
          recentlyFinishedCount: 0,
          stalePrematchCount: 0,
          scheduleKnownForToday: true,
        }),
      };

      const scheduler = new AcquisitionScheduler({
        syncService: mockSyncService,
        fixturesRepository: mockRepo,
        getBudgetContext: async () => ({ consumedDaily: 90, remainingDaily: 10 }), // at reserve ceiling
        logger: silentLogger,
      });

      await scheduler.executeSingleCycle();
      expect(mockSyncService.syncWithPlan).not.toHaveBeenCalled();
    });
  });
});
