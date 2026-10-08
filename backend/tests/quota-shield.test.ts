/**
 * quota-shield.test.ts
 *
 * Unit tests verifying Strike 24A — The Quota-Shield Backend Engine:
 * 1. Twice-Daily Schedule Sync (midnight UTC and midday UTC 7-day schedule caching)
 * 2. Global Batch Live Polling (GET /fixtures?live=all costing strictly 1 API call)
 * 3. The Halftime (HT) Suspend (15-minute polling pause on HT status)
 * 4. Dynamic Priority Scaling & Live Burst Mode (Idle: 4-6h, Unpinned: 15-20m, Pinned: 90s)
 */

import { describe, it, expect, vi, beforeEach } from 'vitest';
import {
  AcquisitionPolicyEngine,
  BudgetContext,
  FixturesSummary,
} from '../src/acquisition/acquisition-policy.js';
import { AcquisitionScheduler } from '../src/acquisition/acquisition-scheduler.js';
import { SyncService } from '../src/acquisition/sync.service.js';
import { FootballProvider, ProviderFixture } from '../src/providers/football-provider.js';

describe('Strike 24A — The Quota-Shield Backend Engine', () => {
  const normalBudget: BudgetContext = { consumedDaily: 20, remainingDaily: 80 };
  const reserveBudget: BudgetContext = { consumedDaily: 92, remainingDaily: 8 };

  const silentLogger = {
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  // =========================================================================
  // 1. Twice-Daily Schedule Sync
  // =========================================================================
  describe('Twice-Daily Schedule Sync', () => {
    it('isScheduleSyncDue returns true when last discovery is null or undefined', () => {
      const now = new Date('2026-10-07T05:00:00Z');
      expect(AcquisitionPolicyEngine.isScheduleSyncDue(now, null)).toBe(true);
      expect(AcquisitionPolicyEngine.isScheduleSyncDue(now, undefined)).toBe(true);
    });

    it('isScheduleSyncDue returns false when discovery occurred within the same 12-hour UTC slot', () => {
      // Midnight slot: 00:00 - 11:59 UTC
      const nowMorning = new Date('2026-10-07T08:30:00Z');
      const lastDiscoveryMorning = new Date('2026-10-07T00:15:00Z');
      expect(AcquisitionPolicyEngine.isScheduleSyncDue(nowMorning, lastDiscoveryMorning)).toBe(false);

      // Midday slot: 12:00 - 23:59 UTC
      const nowEvening = new Date('2026-10-07T19:45:00Z');
      const lastDiscoveryMidday = new Date('2026-10-07T12:05:00Z');
      expect(AcquisitionPolicyEngine.isScheduleSyncDue(nowEvening, lastDiscoveryMidday)).toBe(false);
    });

    it('isScheduleSyncDue returns true when crossing midday (12:00 UTC) milestone', () => {
      // Morning discovery at 06:00 UTC, now is 12:01 UTC (crossed midday)
      const nowAfternoon = new Date('2026-10-07T12:01:00Z');
      const lastDiscoveryMorning = new Date('2026-10-07T06:00:00Z');
      expect(AcquisitionPolicyEngine.isScheduleSyncDue(nowAfternoon, lastDiscoveryMorning)).toBe(true);
    });

    it('isScheduleSyncDue returns true when crossing midnight (00:00 UTC) milestone', () => {
      // Prior day midday discovery at 14:00 UTC, now is 00:05 UTC next day (crossed midnight)
      const nowNextDay = new Date('2026-10-08T00:05:00Z');
      const lastDiscoveryPriorDay = new Date('2026-10-07T14:00:00Z');
      expect(AcquisitionPolicyEngine.isScheduleSyncDue(nowNextDay, lastDiscoveryPriorDay)).toBe(true);
    });

    it('evaluateSyncPlan recommends DISCOVER_SCHEDULE when twice-daily schedule sync is due', () => {
      const summary: FixturesSummary = {
        liveCount: 0,
        recentlyFinishedCount: 0,
        stalePrematchCount: 0,
        scheduleKnownForToday: true,
        isScheduleSyncDue: true,
      };

      const plan = AcquisitionPolicyEngine.evaluateSyncPlan(normalBudget, summary);
      expect(plan.action).toBe('DISCOVER_SCHEDULE');
      expect(plan.reason).toContain('Twice-daily schedule sync due');
    });

    it('sync7DaySchedule queries paced daily schedule requests across 7 consecutive dates (Yesterday through Today + 5)', async () => {
      vi.useFakeTimers();
      try {
        const syncedDates: string[] = [];
        const mockSyncService: any = {
          syncSchedule: vi.fn().mockImplementation(async (date: string) => {
            syncedDates.push(date);
            return {
              provider: 'test_provider',
              fixturesDiscovered: 5,
              fixturesInserted: 0,
              fixturesUpdated: 0,
              eventsInserted: 0,
              eventsSkippedDuplicates: 0,
              hasChanges: false,
            };
          }),
        };

        const markedDates: string[] = [];
        const mockRepo: any = {
          getActiveSummary: vi.fn().mockResolvedValue({
            liveCount: 0,
            recentlyFinishedCount: 0,
            stalePrematchCount: 0,
            scheduleKnownForToday: true,
          }),
          markScheduleDiscovered: vi.fn().mockImplementation(async (date: string) => {
            markedDates.push(date);
          }),
        };

        const testDate = new Date('2026-10-07T08:00:00Z');
        const scheduler = new AcquisitionScheduler({
          syncService: mockSyncService,
          fixturesRepository: mockRepo,
          targetCompetitionIds: [39, 140],
          nowFn: () => testDate,
          logger: silentLogger,
        });

        const syncPromise = scheduler.sync7DaySchedule(testDate);
        await vi.runAllTimersAsync();
        const summary = await syncPromise;

        // Verify 7 daily requests fetched spanning 2026-10-06 (-1) through 2026-10-12 (+5)
        expect(mockSyncService.syncSchedule).toHaveBeenCalledTimes(7);
        expect(syncedDates).toEqual([
          '2026-10-06',
          '2026-10-07',
          '2026-10-08',
          '2026-10-09',
          '2026-10-10',
          '2026-10-11',
          '2026-10-12',
        ]);

        // All 7 dates marked discovered in repo
        expect(markedDates.length).toBe(7);
        expect(markedDates[0]).toBe('2026-10-06');
        expect(markedDates[6]).toBe('2026-10-12');
        expect(summary.fixturesDiscovered).toBe(35);

        // Verify that after syncing, isTwiceDailyScheduleSyncDue evaluates to false for this slot
        expect(scheduler.isTwiceDailyScheduleSyncDue(testDate)).toBe(false);
      } finally {
        vi.useRealTimers();
      }
    });

    it('executeSingleCycle with options.sync7Days triggers 7-day schedule sync', async () => {
      vi.useFakeTimers();
      try {
        let callsCount = 0;
        const mockSyncService: any = {
          syncSchedule: vi.fn().mockImplementation(async (date: string) => {
            callsCount++;
            return {
              provider: 'test_provider',
              fixturesDiscovered: 2,
              fixturesInserted: 0,
              fixturesUpdated: 0,
              eventsInserted: 0,
              eventsSkippedDuplicates: 0,
              hasChanges: false,
            };
          }),
        };

        const mockRepo: any = {
          getActiveSummary: vi.fn().mockResolvedValue({
            liveCount: 0,
            recentlyFinishedCount: 0,
            stalePrematchCount: 0,
            scheduleKnownForToday: false,
          }),
          markScheduleDiscovered: vi.fn(),
        };

        const testDate = new Date('2026-10-07T08:00:00Z');
        const scheduler = new AcquisitionScheduler({
          syncService: mockSyncService,
          fixturesRepository: mockRepo,
          nowFn: () => testDate,
          getBudgetContext: async () => normalBudget,
          logger: silentLogger,
        });

        const cyclePromise = scheduler.executeSingleCycle({ sync7Days: true });
        await vi.runAllTimersAsync();
        const { plan } = await cyclePromise;
        expect(plan.action).toBe('DISCOVER_SCHEDULE');
        expect(mockSyncService.syncSchedule).toHaveBeenCalledTimes(7);
        expect(callsCount).toBe(7);
      } finally {
        vi.useRealTimers();
      }
    });
  });

  // =========================================================================
  // 2. Global Batch Live Polling (GET /fixtures?live=all)
  // =========================================================================
  describe('Global Batch Live Polling (GET /fixtures?live=all)', () => {
    it('syncLive executes exactly 1 provider request costing exactly 1 API call per cycle', async () => {
      let providerCallsCount = 0;
      const requestedFilters: any[] = [];

      const mockProvider: FootballProvider = {
        providerName: 'api_football',
        getFixtures: vi.fn().mockImplementation(async (filter) => {
          providerCallsCount++;
          requestedFilters.push(filter);
          return [
            {
              id: 'fix_101',
              competitionId: 'comp_39',
              seasonId: 'season_39_2026',
              homeTeamId: 'team_1',
              awayTeamId: 'team_2',
              kickoffTime: new Date().toISOString(),
              state: 'LIVE',
              elapsedMinute: 23,
              score: {
                home: 1,
                away: 0,
              },
              competition: {
                id: 'comp_39',
                name: 'Premier League',
                country: 'England',
              },
              season: {
                id: 'season_39_2026',
                competitionId: 'comp_39',
                year: 2026,
                displayYear: '2026/2027',
                startDate: '2026-08-01',
                endDate: '2027-05-30',
                isCurrent: true,
              },
              homeTeam: {
                id: 'team_1',
                name: 'Arsenal',
                shortName: 'ARS',
              },
              awayTeam: {
                id: 'team_2',
                name: 'Chelsea',
                shortName: 'CHE',
              },
            } as any,
          ];
        }),
        getFixturesByDate: async () => [],
        getCompetitions: async () => [],
        getStandings: async () => [],
        getFixtureDetails: vi.fn().mockImplementation(async () => {
          // Should NEVER be called during live batch polling
          providerCallsCount++;
          return null;
        }),
      };

      const syncService = new SyncService(mockProvider);
      const summary = await syncService.syncLive([39]);

      // Strictly 1 API call
      expect(providerCallsCount).toBe(1);
      expect(mockProvider.getFixtures).toHaveBeenCalledTimes(1);
      expect(mockProvider.getFixtureDetails).not.toHaveBeenCalled();

      // Filter passed liveOnly: true
      expect(requestedFilters[0].liveOnly).toBe(true);
      expect(summary.fixturesDiscovered).toBe(1);
    });
  });

  // =========================================================================
  // 3. The Halftime (HT) Suspend
  // =========================================================================
  describe('The Halftime (HT) Suspend', () => {
    it('suspends polling for 15 minutes when all live matches enter HALFTIME / HT', () => {
      const summary: FixturesSummary = {
        liveCount: 2,
        allLiveInHalftime: true,
        halftimeRemainingMs: AcquisitionPolicyEngine.HALFTIME_SUSPEND_MS,
        recentlyFinishedCount: 0,
        stalePrematchCount: 0,
        scheduleKnownForToday: true,
      };

      const plan = AcquisitionPolicyEngine.evaluateSyncPlan(normalBudget, summary);

      // Halftime suspend: returns IDLE action with 15-minute wait
      expect(plan.action).toBe('IDLE');
      expect(plan.recommendedNextPollMs).toBe(15 * 60 * 1000);
      expect(plan.reason).toContain('Halftime suspend');
    });

    it('suspends burst mode when pinned/followed match is in HALFTIME even if liveCount > 0', () => {
      const summary: FixturesSummary = {
        liveCount: 1,
        hasPinnedOrFollowedLive: true,
        pinnedOrFollowedLiveCount: 1,
        pinnedOrFollowedInHalftime: true,
        halftimeRemainingMs: 12 * 60 * 1000, // 12 minutes remaining in break
        recentlyFinishedCount: 0,
        stalePrematchCount: 0,
        scheduleKnownForToday: true,
      };

      const plan = AcquisitionPolicyEngine.evaluateSyncPlan(normalBudget, summary);

      // Followed match in HT: burst timer suspended for remainder of break
      expect(plan.action).toBe('IDLE');
      expect(plan.recommendedNextPollMs).toBe(12 * 60 * 1000);
      expect(plan.reason).toContain('followed live match is in HT');
    });

    it('executeSingleCycle dynamically activates Halftime Suspend immediately upon detecting HT', async () => {
      const mockSyncService: any = {
        syncWithPlan: vi.fn().mockResolvedValue({
          provider: 'api_football',
          fixturesDiscovered: 1,
          fixturesInserted: 0,
          fixturesUpdated: 1,
          eventsInserted: 0,
          eventsSkippedDuplicates: 0,
          hasChanges: true,
        }),
      };

      // Repository reports match entered HALFTIME
      const mockRepo: any = {
        getActiveSummary: vi.fn().mockResolvedValue({
          liveCount: 1,
          hasPinnedOrFollowedLive: true,
          pinnedOrFollowedLiveCount: 1,
          pinnedOrFollowedInHalftime: true,
          allLiveInHalftime: true,
          halftimeRemainingMs: 15 * 60 * 1000,
          recentlyFinishedCount: 0,
          stalePrematchCount: 0,
          scheduleKnownForToday: true,
        }),
      };

      const scheduler = new AcquisitionScheduler({
        syncService: mockSyncService,
        fixturesRepository: mockRepo,
        getBudgetContext: async () => normalBudget,
        logger: silentLogger,
      });

      const { plan } = await scheduler.executeSingleCycle();

      // HT suspend was activated and timer paused for 15 minutes
      expect(plan.recommendedNextPollMs).toBe(15 * 60 * 1000);
    });
  });

  // =========================================================================
  // 4. Dynamic Priority Scaling & Live Burst Mode
  // =========================================================================
  describe('Dynamic Priority Scaling & Live Burst Mode', () => {
    it('Idle / No Live Matches: checks every 4-6 hours (recommendedNextPollMs is 4h)', () => {
      const summary: FixturesSummary = {
        liveCount: 0,
        hasPinnedOrFollowedLive: false,
        recentlyFinishedCount: 0,
        stalePrematchCount: 0,
        scheduleKnownForToday: true,
        isScheduleSyncDue: false,
      };

      // Set time to 02:00 UTC so next slot (12:00 UTC) is 10h away (idle delay will be full 4h)
      const now = new Date('2026-10-07T02:00:00Z');
      const plan = AcquisitionPolicyEngine.evaluateSyncPlan(normalBudget, summary, now);

      expect(plan.action).toBe('IDLE');
      // 4 hours = 14,400,000 ms
      expect(plan.recommendedNextPollMs).toBe(4 * 60 * 60 * 1000);
      expect(plan.reason).toContain('checking every 4h');
    });

    it('Unpinned Live Matches: polls with broad 15-20 minute interval (15 minutes)', () => {
      const summary: FixturesSummary = {
        liveCount: 3,
        hasPinnedOrFollowedLive: false, // unpinned
        unpinnedLiveCount: 3,
        allLiveInHalftime: false,
        recentlyFinishedCount: 0,
        stalePrematchCount: 0,
        scheduleKnownForToday: true,
      };

      const plan = AcquisitionPolicyEngine.evaluateSyncPlan(normalBudget, summary);

      expect(plan.action).toBe('POLL_LIVE');
      // 15 minutes = 900,000 ms
      expect(plan.recommendedNextPollMs).toBe(15 * 60 * 1000);
      expect(plan.reason).toContain('broad 15-minute interval');
    });

    it('Pinned / Followed Live Matches: runs strict 90-second Live Burst Mode in active play', () => {
      const summary: FixturesSummary = {
        liveCount: 1,
        hasPinnedOrFollowedLive: true, // followed/pinned match
        pinnedOrFollowedLiveCount: 1,
        pinnedOrFollowedInHalftime: false, // in active play (e.g. 1H or 2H)
        recentlyFinishedCount: 0,
        stalePrematchCount: 0,
        scheduleKnownForToday: true,
      };

      const plan = AcquisitionPolicyEngine.evaluateSyncPlan(normalBudget, summary);

      expect(plan.action).toBe('POLL_LIVE');
      // Strict 90 seconds = 90,000 ms
      expect(plan.recommendedNextPollMs).toBe(90 * 1000);
      expect(plan.reason).toContain('90-second Live Burst Mode active');
    });

    it('Critical Reserve Ceiling (<= 10) halts polling even with followed live match', () => {
      const summary: FixturesSummary = {
        liveCount: 1,
        hasPinnedOrFollowedLive: true,
        pinnedOrFollowedLiveCount: 1,
        pinnedOrFollowedInHalftime: false,
        recentlyFinishedCount: 0,
        stalePrematchCount: 0,
        scheduleKnownForToday: true,
      };

      const plan = AcquisitionPolicyEngine.evaluateSyncPlan(reserveBudget, summary);

      expect(plan.action).toBe('IDLE');
      expect(plan.recommendedNextPollMs).toBeNull();
      expect(plan.reason).toContain('Critical reserve protected');
    });
  });
});
