/**
 * acquisition-policy.ts
 *
 * Deterministic, provider-independent policy engine that decides whether an
 * external acquisition action is justified and recommends a future polling delay.
 *
 * Architectural boundaries:
 *   - Pure decision engine: no database, network, or provider dependencies.
 *   - Does NOT reserve budget slots or enforce rate limits (RequestGateway owns enforcement).
 *   - Does NOT execute HTTP calls or database queries.
 *   - Plain deterministic inputs and outputs for 100% testability.
 */

// ----------------------------------------------------
// 1. Fixture Lifecycle State Model
// ----------------------------------------------------
export type FixtureLifecycleState =
  | 'SCHEDULED_FUTURE'   // Kickoff > 24 hours in the future
  | 'PREMATCH_TODAY'     // Match kickoff occurs today in UTC, not yet approaching
  | 'APPROACHING'        // Prematch fixture within 15 minutes before kickoff
  | 'LIVE_WINDOW'        // In-progress match (LIVE, HALFTIME, extra time, etc.)
  | 'RECENTLY_FINISHED'  // Finished within the reconciliation window (e.g. <=15m)
  | 'FINAL'              // Finished / completed past the reconciliation window
  | 'STALE_PREMATCH';    // Prematch status but kickoff is >3 hours in the past

// ----------------------------------------------------
// 2. Action & Priority Models
// ----------------------------------------------------
export type SyncAction =
  | 'DISCOVER_SCHEDULE'  // Fetch daily fixture schedule for target competitions
  | 'POLL_LIVE'          // Poll active in-progress matches (via live=all)
  | 'FINALIZE'           // Final reconciliation query for recently completed matches
  | 'RECOVER_STALE'      // Probe for postponed/cancelled status on overdue prematch
  | 'IDLE';              // No external acquisition warranted

export type SyncPriority = 'NORMAL' | 'HIGH' | 'CRITICAL';

export interface SyncPlan {
  action: SyncAction;
  priority: SyncPriority;
  recommendedNextPollMs: number | null;
  reason: string;
}

// ----------------------------------------------------
// 3. Context Inputs
// ----------------------------------------------------
export interface BudgetContext {
  allocatedDaily?: number;
  consumedDaily: number;
  remainingDaily: number;
  isExhausted?: boolean;
}

export interface FixturesSummary {
  liveCount: number;
  approachingCount?: number;
  recentlyFinishedCount: number;
  stalePrematchCount: number;
  scheduleKnownForToday: boolean;
  lastScheduleDiscoveryAt?: Date | string | null;
}

export interface FixtureClassificationInput {
  state: string;
  kickoffTime: Date | string;
  lastPolledAt?: Date | string | null;
  updatedAt?: Date | string | null;
}

// ----------------------------------------------------
// 4. Policy Engine Implementation
// ----------------------------------------------------
export class AcquisitionPolicyEngine {
  // Explicit budget thresholds (in remaining requests)
  static readonly FAST_CADENCE_BUDGET_MIN = 51; // > 50
  static readonly STANDARD_CADENCE_BUDGET_MIN = 20; // 20..50
  static readonly SLOW_CADENCE_BUDGET_MIN = 11; // 11..19
  static readonly CRITICAL_RESERVE_CEILING = 10; // <= 10

  // Polling intervals in milliseconds
  static readonly FAST_CADENCE_MS = 120_000; // 120s (2m)
  static readonly STANDARD_CADENCE_MS = 300_000; // 300s (5m)
  static readonly SLOW_CADENCE_MS = 600_000; // 600s (10m)

  // Reconciliation window for recently finished matches (15 minutes)
  static readonly RECENTLY_FINISHED_WINDOW_MS = 15 * 60 * 1000;

  // Stale prematch threshold (3 hours past kickoff)
  static readonly STALE_PREMATCH_THRESHOLD_MS = 3 * 60 * 60 * 1000;

  // Approaching kickoff window (15 minutes before kickoff)
  static readonly APPROACHING_WINDOW_MS = 15 * 60 * 1000;

  /**
   * Calculates recommended polling interval based on remaining daily budget.
   *
   * Thresholds:
   *   remainingBudget > 50           → 120s (120,000 ms)
   *   remainingBudget >= 20 && <= 50 → 300s (300,000 ms)
   *   remainingBudget > 10 && < 20   → 600s (600,000 ms)
   *   remainingBudget <= 10          → null (disabled / reserve protection)
   */
  static calculateLivePollingInterval(remainingBudget: number): number | null {
    if (remainingBudget > 50) {
      return this.FAST_CADENCE_MS;
    }
    if (remainingBudget >= 20) {
      return this.STANDARD_CADENCE_MS;
    }
    if (remainingBudget > 10) {
      return this.SLOW_CADENCE_MS;
    }
    return null;
  }

  /**
   * Deterministically classifies a fixture into its lifecycle state.
   * Uses UTC Date arithmetic.
   */
  static classifyFixtureState(
    fixture: FixtureClassificationInput,
    now: Date = new Date()
  ): FixtureLifecycleState {
    const stateUpper = (fixture.state || '').toUpperCase().trim();
    const kickoff = new Date(fixture.kickoffTime);
    const nowMs = now.getTime();
    const kickoffMs = kickoff.getTime();
    const diffMs = kickoffMs - nowMs; // positive if in future, negative if in past

    // 1. Live match states (uses existing domain representations)
    const liveStates = new Set(['LIVE', 'HALFTIME', '1H', '2H', 'ET', 'P', 'BT', 'IN_PLAY']);
    if (liveStates.has(stateUpper)) {
      return 'LIVE_WINDOW';
    }

    // 2. Finished match states
    const finishedStates = new Set(['FINISHED', 'FT', 'AET', 'PEN', 'POSTMATCH']);
    if (finishedStates.has(stateUpper)) {
      // Check when the finish was recorded / polled
      const referenceTime = fixture.lastPolledAt
        ? new Date(fixture.lastPolledAt).getTime()
        : fixture.updatedAt
        ? new Date(fixture.updatedAt).getTime()
        : kickoffMs + 105 * 60 * 1000; // fallback: approximate end time

      const timeSinceFinish = nowMs - referenceTime;
      if (timeSinceFinish <= this.RECENTLY_FINISHED_WINDOW_MS && timeSinceFinish >= 0) {
        return 'RECENTLY_FINISHED';
      }
      return 'FINAL';
    }

    // 3. Postponed / Cancelled terminal states
    const cancelledStates = new Set(['POSTPONED', 'CANCELLED', 'SUSP', 'INT', 'ABD', 'AWD', 'WO']);
    if (cancelledStates.has(stateUpper)) {
      return 'FINAL';
    }

    // 4. Prematch states (PREMATCH, NS, TBD)
    // 4a. Stale prematch: kickoff was > 3 hours in the past but state is still PREMATCH
    if (diffMs < -this.STALE_PREMATCH_THRESHOLD_MS) {
      return 'STALE_PREMATCH';
    }

    // 4b. Approaching: within 15 minutes before kickoff up to kickoff window
    if (diffMs <= this.APPROACHING_WINDOW_MS && diffMs >= -this.STALE_PREMATCH_THRESHOLD_MS) {
      return 'APPROACHING';
    }

    // 4c. Scheduled future: more than 24 hours in the future
    if (diffMs > 24 * 60 * 60 * 1000) {
      return 'SCHEDULED_FUTURE';
    }

    // 4d. Prematch today: occurs today in UTC calendar day (or < 24h away)
    return 'PREMATCH_TODAY';
  }

  /**
   * Evaluates the optimal synchronization action and cadence based on current
   * budget availability and fixture states.
   *
   * Priority order:
   *   1. Budget Exhaustion / Critical Reserve Protection: If budget <= 10, return IDLE.
   *   2. Active Live Matches: Return POLL_LIVE with budget-scaled interval.
   *   3. Recently Finished Matches: Return FINALIZE for final score/event sync.
   *   4. Stale Prematch: Return RECOVER_STALE to update postponed/cancelled status.
   *   5. Undiscovered Daily Schedule: Return DISCOVER_SCHEDULE.
   *   6. Otherwise: Return IDLE.
   */
  static evaluateSyncPlan(
    budget: BudgetContext,
    fixturesSummary: FixturesSummary,
    now: Date = new Date()
  ): SyncPlan {
    const remaining = budget.remainingDaily;

    // 1. Critical Reserve Protection (Autonomous polling never consumes the 10-call reserve)
    if (remaining <= this.CRITICAL_RESERVE_CEILING) {
      return {
        action: 'IDLE',
        priority: 'NORMAL',
        recommendedNextPollMs: null,
        reason: `Critical reserve protected (remaining budget: ${remaining} <= ${this.CRITICAL_RESERVE_CEILING}); autonomous polling disabled.`,
      };
    }

    // 2. Live Fixtures Priority (Consolidated poll for all live matches)
    if (fixturesSummary.liveCount > 0) {
      const intervalMs = this.calculateLivePollingInterval(remaining);
      if (intervalMs === null) {
        return {
          action: 'IDLE',
          priority: 'NORMAL',
          recommendedNextPollMs: null,
          reason: 'Autonomous live polling halted due to low budget reserve.',
        };
      }

      return {
        action: 'POLL_LIVE',
        priority: 'NORMAL',
        recommendedNextPollMs: intervalMs,
        reason: `Live match in progress (${fixturesSummary.liveCount} active). Polling with ${intervalMs / 1000}s interval.`,
      };
    }

    // 3. Recently Finished Reconciliation
    if (fixturesSummary.recentlyFinishedCount > 0) {
      return {
        action: 'FINALIZE',
        priority: 'NORMAL',
        recommendedNextPollMs: 60_000,
        reason: `Recently finished match requires final reconciliation (${fixturesSummary.recentlyFinishedCount} pending).`,
      };
    }

    // 4. Stale Prematch Recovery
    if (fixturesSummary.stalePrematchCount > 0) {
      return {
        action: 'RECOVER_STALE',
        priority: 'NORMAL',
        recommendedNextPollMs: 300_000,
        reason: `Stale prematch fixtures detected (${fixturesSummary.stalePrematchCount} past kickoff). Probing for postponed/cancelled status.`,
      };
    }

    // 5. Undiscovered Daily Schedule
    if (!fixturesSummary.scheduleKnownForToday) {
      return {
        action: 'DISCOVER_SCHEDULE',
        priority: 'NORMAL',
        recommendedNextPollMs: 3600_000,
        reason: "Today's match schedule has not been discovered yet.",
      };
    }

    // 6. Approaching Match Window
    if ((fixturesSummary.approachingCount ?? 0) > 0) {
      return {
        action: 'IDLE',
        priority: 'NORMAL',
        recommendedNextPollMs: 60_000,
        reason: `Match approaching kickoff (${fixturesSummary.approachingCount} within 15m). Waiting for kickoff.`,
      };
    }

    // 7. Fully Idle
    return {
      action: 'IDLE',
      priority: 'NORMAL',
      recommendedNextPollMs: null,
      reason: 'No live, approaching, stale, or unfinalized fixtures. System is idle.',
    };
  }
}
