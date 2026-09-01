import { describe, it, expect } from 'vitest';
import {
  AcquisitionPolicyEngine,
  BudgetContext,
  FixturesSummary,
} from '../src/acquisition/acquisition-policy.js';

describe('AcquisitionPolicyEngine - Pure Budget & Polling Policy', () => {
  // ============================================================
  // A. Live Polling Cadence Tests
  // ============================================================
  describe('calculateLivePollingInterval()', () => {
    it('recommends 120s (2m) fast interval when budget > 50', () => {
      expect(AcquisitionPolicyEngine.calculateLivePollingInterval(80)).toBe(120_000);
      expect(AcquisitionPolicyEngine.calculateLivePollingInterval(90)).toBe(120_000);
      expect(AcquisitionPolicyEngine.calculateLivePollingInterval(51)).toBe(120_000);
    });

    it('recommends 300s (5m) standard interval when budget is between 20 and 50 inclusive', () => {
      expect(AcquisitionPolicyEngine.calculateLivePollingInterval(50)).toBe(300_000);
      expect(AcquisitionPolicyEngine.calculateLivePollingInterval(40)).toBe(300_000);
      expect(AcquisitionPolicyEngine.calculateLivePollingInterval(20)).toBe(300_000);
    });

    it('recommends 600s (10m) slow interval when budget is between 11 and 19', () => {
      expect(AcquisitionPolicyEngine.calculateLivePollingInterval(19)).toBe(600_000);
      expect(AcquisitionPolicyEngine.calculateLivePollingInterval(15)).toBe(600_000);
      expect(AcquisitionPolicyEngine.calculateLivePollingInterval(11)).toBe(600_000);
    });

    it('disables autonomous polling (returns null) when budget <= 10 (critical reserve protection)', () => {
      expect(AcquisitionPolicyEngine.calculateLivePollingInterval(10)).toBeNull();
      expect(AcquisitionPolicyEngine.calculateLivePollingInterval(5)).toBeNull();
      expect(AcquisitionPolicyEngine.calculateLivePollingInterval(0)).toBeNull();
      expect(AcquisitionPolicyEngine.calculateLivePollingInterval(-1)).toBeNull();
    });
  });

  // ============================================================
  // B. Fixture Classification Tests
  // ============================================================
  describe('classifyFixtureState()', () => {
    const fixedNow = new Date('2026-03-01T15:00:00Z');

    it('classifies fixtures with kickoff > 24h in the future as SCHEDULED_FUTURE', () => {
      const fixture = {
        state: 'PREMATCH',
        kickoffTime: new Date('2026-03-03T15:00:00Z'), // 48h in future
      };
      expect(AcquisitionPolicyEngine.classifyFixtureState(fixture, fixedNow)).toBe('SCHEDULED_FUTURE');
    });

    it('classifies fixtures scheduled today (> 15m away) as PREMATCH_TODAY', () => {
      const fixture = {
        state: 'PREMATCH',
        kickoffTime: new Date('2026-03-01T18:00:00Z'), // 3h in future
      };
      expect(AcquisitionPolicyEngine.classifyFixtureState(fixture, fixedNow)).toBe('PREMATCH_TODAY');
    });

    it('classifies fixtures within 15 minutes before kickoff as APPROACHING', () => {
      const fixture = {
        state: 'PREMATCH',
        kickoffTime: new Date('2026-03-01T15:10:00Z'), // 10m in future
      };
      expect(AcquisitionPolicyEngine.classifyFixtureState(fixture, fixedNow)).toBe('APPROACHING');
    });

    it('classifies in-progress matches (LIVE, HALFTIME, 1H, 2H) as LIVE_WINDOW', () => {
      expect(
        AcquisitionPolicyEngine.classifyFixtureState(
          { state: 'LIVE', kickoffTime: new Date('2026-03-01T14:30:00Z') },
          fixedNow
        )
      ).toBe('LIVE_WINDOW');

      expect(
        AcquisitionPolicyEngine.classifyFixtureState(
          { state: 'HALFTIME', kickoffTime: new Date('2026-03-01T14:15:00Z') },
          fixedNow
        )
      ).toBe('LIVE_WINDOW');

      expect(
        AcquisitionPolicyEngine.classifyFixtureState(
          { state: '1H', kickoffTime: new Date('2026-03-01T14:45:00Z') },
          fixedNow
        )
      ).toBe('LIVE_WINDOW');

      expect(
        AcquisitionPolicyEngine.classifyFixtureState(
          { state: '2H', kickoffTime: new Date('2026-03-01T14:00:00Z') },
          fixedNow
        )
      ).toBe('LIVE_WINDOW');
    });

    it('classifies matches finished within the last 15m as RECENTLY_FINISHED', () => {
      const fixture = {
        state: 'FINISHED',
        kickoffTime: new Date('2026-03-01T13:00:00Z'),
        lastPolledAt: new Date('2026-03-01T14:55:00Z'), // polled 5m ago
      };
      expect(AcquisitionPolicyEngine.classifyFixtureState(fixture, fixedNow)).toBe('RECENTLY_FINISHED');
    });

    it('classifies matches finished > 15m ago as FINAL', () => {
      const fixture = {
        state: 'FINISHED',
        kickoffTime: new Date('2026-03-01T12:00:00Z'),
        lastPolledAt: new Date('2026-03-01T14:00:00Z'), // polled 60m ago
      };
      expect(AcquisitionPolicyEngine.classifyFixtureState(fixture, fixedNow)).toBe('FINAL');
    });

    it('classifies prematch fixtures > 3h past kickoff as STALE_PREMATCH', () => {
      const fixture = {
        state: 'PREMATCH',
        kickoffTime: new Date('2026-03-01T11:00:00Z'), // 4h in past, still PREMATCH
      };
      expect(AcquisitionPolicyEngine.classifyFixtureState(fixture, fixedNow)).toBe('STALE_PREMATCH');
    });

    it('classifies postponed or cancelled matches as FINAL', () => {
      expect(
        AcquisitionPolicyEngine.classifyFixtureState(
          { state: 'POSTPONED', kickoffTime: new Date('2026-03-01T15:00:00Z') },
          fixedNow
        )
      ).toBe('FINAL');

      expect(
        AcquisitionPolicyEngine.classifyFixtureState(
          { state: 'CANCELLED', kickoffTime: new Date('2026-03-01T15:00:00Z') },
          fixedNow
        )
      ).toBe('FINAL');
    });
  });

  // ============================================================
  // C. Plan Prioritization Tests
  // ============================================================
  describe('evaluateSyncPlan() - Prioritization', () => {
    const abundantBudget: BudgetContext = {
      consumedDaily: 20,
      remainingDaily: 80,
    };

    it('prioritizes POLL_LIVE when live fixtures exist', () => {
      const summary: FixturesSummary = {
        liveCount: 2,
        approachingCount: 1,
        recentlyFinishedCount: 1,
        stalePrematchCount: 1,
        scheduleKnownForToday: false,
      };

      const plan = AcquisitionPolicyEngine.evaluateSyncPlan(abundantBudget, summary);
      expect(plan.action).toBe('POLL_LIVE');
      expect(plan.priority).toBe('NORMAL');
      expect(plan.recommendedNextPollMs).toBe(120_000);
      expect(plan.reason).toContain('Live match in progress');
    });

    it('prioritizes FINALIZE when no live matches exist but recently finished matches are pending', () => {
      const summary: FixturesSummary = {
        liveCount: 0,
        approachingCount: 0,
        recentlyFinishedCount: 1,
        stalePrematchCount: 1,
        scheduleKnownForToday: false,
      };

      const plan = AcquisitionPolicyEngine.evaluateSyncPlan(abundantBudget, summary);
      expect(plan.action).toBe('FINALIZE');
      expect(plan.recommendedNextPollMs).toBe(60_000);
      expect(plan.reason).toContain('Recently finished match requires final reconciliation');
    });

    it('prioritizes RECOVER_STALE when stale prematch matches exist and no live/recently-finished matches', () => {
      const summary: FixturesSummary = {
        liveCount: 0,
        approachingCount: 0,
        recentlyFinishedCount: 0,
        stalePrematchCount: 2,
        scheduleKnownForToday: false,
      };

      const plan = AcquisitionPolicyEngine.evaluateSyncPlan(abundantBudget, summary);
      expect(plan.action).toBe('RECOVER_STALE');
      expect(plan.recommendedNextPollMs).toBe(300_000);
      expect(plan.reason).toContain('Stale prematch fixtures detected');
    });

    it('prioritizes DISCOVER_SCHEDULE when today schedule is unknown and no active/stale matches exist', () => {
      const summary: FixturesSummary = {
        liveCount: 0,
        approachingCount: 0,
        recentlyFinishedCount: 0,
        stalePrematchCount: 0,
        scheduleKnownForToday: false,
      };

      const plan = AcquisitionPolicyEngine.evaluateSyncPlan(abundantBudget, summary);
      expect(plan.action).toBe('DISCOVER_SCHEDULE');
      expect(plan.recommendedNextPollMs).toBe(3600_000);
      expect(plan.reason).toContain("Today's match schedule has not been discovered yet");
    });

    it('returns IDLE when schedule is known and no live/approaching/stale matches exist', () => {
      const summary: FixturesSummary = {
        liveCount: 0,
        approachingCount: 0,
        recentlyFinishedCount: 0,
        stalePrematchCount: 0,
        scheduleKnownForToday: true,
      };

      const plan = AcquisitionPolicyEngine.evaluateSyncPlan(abundantBudget, summary);
      expect(plan.action).toBe('IDLE');
      expect(plan.recommendedNextPollMs).toBeNull();
      expect(plan.reason).toContain('System is idle');
    });
  });

  // ============================================================
  // D. Budget Reserve Protection Tests
  // ============================================================
  describe('evaluateSyncPlan() - Budget Reserve Protection', () => {
    const liveSummary: FixturesSummary = {
      liveCount: 3,
      recentlyFinishedCount: 0,
      stalePrematchCount: 0,
      scheduleKnownForToday: true,
    };

    it('halts autonomous live polling and returns IDLE when remaining budget is exactly at critical reserve (10)', () => {
      const budget: BudgetContext = { consumedDaily: 90, remainingDaily: 10 };
      const plan = AcquisitionPolicyEngine.evaluateSyncPlan(budget, liveSummary);

      expect(plan.action).toBe('IDLE');
      expect(plan.recommendedNextPollMs).toBeNull();
      expect(plan.reason).toContain('Critical reserve protected');
    });

    it('halts autonomous live polling when remaining budget is below critical reserve (5)', () => {
      const budget: BudgetContext = { consumedDaily: 95, remainingDaily: 5 };
      const plan = AcquisitionPolicyEngine.evaluateSyncPlan(budget, liveSummary);

      expect(plan.action).toBe('IDLE');
      expect(plan.recommendedNextPollMs).toBeNull();
      expect(plan.reason).toContain('Critical reserve protected');
    });

    it('permits live polling with 600s interval when budget is just above critical reserve (15)', () => {
      const budget: BudgetContext = { consumedDaily: 85, remainingDaily: 15 };
      const plan = AcquisitionPolicyEngine.evaluateSyncPlan(budget, liveSummary);

      expect(plan.action).toBe('POLL_LIVE');
      expect(plan.recommendedNextPollMs).toBe(600_000);
      expect(plan.reason).toContain('Polling with 600s interval');
    });
  });

  // ============================================================
  // E. Consolidation Tests
  // ============================================================
  describe('evaluateSyncPlan() - Consolidation', () => {
    const budget: BudgetContext = { consumedDaily: 30, remainingDaily: 70 };

    it('produces a single POLL_LIVE action whether there is 1 or 10 active live matches', () => {
      const summary1: FixturesSummary = {
        liveCount: 1,
        recentlyFinishedCount: 0,
        stalePrematchCount: 0,
        scheduleKnownForToday: true,
      };

      const summary10: FixturesSummary = {
        liveCount: 10,
        recentlyFinishedCount: 0,
        stalePrematchCount: 0,
        scheduleKnownForToday: true,
      };

      const plan1 = AcquisitionPolicyEngine.evaluateSyncPlan(budget, summary1);
      const plan10 = AcquisitionPolicyEngine.evaluateSyncPlan(budget, summary10);

      expect(plan1.action).toBe('POLL_LIVE');
      expect(plan10.action).toBe('POLL_LIVE');
      expect(plan1.recommendedNextPollMs).toBe(plan10.recommendedNextPollMs);
    });
  });

  // ============================================================
  // F. Determinism Tests
  // ============================================================
  describe('evaluateSyncPlan() - Determinism', () => {
    it('returns identical SyncPlan for identical inputs and timestamps', () => {
      const budget: BudgetContext = { consumedDaily: 45, remainingDaily: 55 };
      const summary: FixturesSummary = {
        liveCount: 2,
        approachingCount: 1,
        recentlyFinishedCount: 0,
        stalePrematchCount: 0,
        scheduleKnownForToday: true,
      };
      const fixedNow = new Date('2026-03-01T15:00:00Z');

      const planA = AcquisitionPolicyEngine.evaluateSyncPlan(budget, summary, fixedNow);
      const planB = AcquisitionPolicyEngine.evaluateSyncPlan(budget, summary, fixedNow);

      expect(planA).toEqual(planB);
    });
  });
});
