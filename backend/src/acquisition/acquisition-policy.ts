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
  staleCompetitionIds?: number[];
  scheduleKnownForToday: boolean;
  lastScheduleDiscoveryAt?: Date | string | null;

  // Strike 24A Quota-Shield fields
  hasPinnedOrFollowedLive?: boolean;
  pinnedOrFollowedLiveCount?: number;
  unpinnedLiveCount?: number;
  allLiveInHalftime?: boolean;
  pinnedOrFollowedInHalftime?: boolean;
  halftimeRemainingMs?: number | null;
  isScheduleSyncDue?: boolean;
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

  // Strike 24A Quota-Shield Cadence Constants
  // Idle / No Live Matches: Check every 4–6 hours (4h = 14,400s)
  static readonly IDLE_NO_LIVE_CADENCE_MS = 4 * 60 * 60 * 1000;
  // Unpinned Live Matches: Broad 15–20 minute interval (15m = 900s)
  static readonly UNPINNED_LIVE_CADENCE_MS = 15 * 60 * 1000;
  // Pinned / Followed Live Matches: Strict 90-second Live Burst Mode (90s)
  static readonly PINNED_LIVE_BURST_CADENCE_MS = 90 * 1000;
  // The Halftime (HT) Suspend: Pause polling for the 15-minute break (15m = 900s)
  static readonly HALFTIME_SUSPEND_MS = 15 * 60 * 1000;

  // Fallback Polling intervals in milliseconds
  // Calibrated for API-Football 100/day free-tier quota (90 operational daily ceiling).
  static readonly FAST_CADENCE_MS = 180_000; // 180s (3m)
  static readonly STANDARD_CADENCE_MS = 300_000; // 300s (5m)
  static readonly SLOW_CADENCE_MS = 600_000; // 600s (10m)

  // Reconciliation window for recently finished matches (15 minutes)
  static readonly RECENTLY_FINISHED_WINDOW_MS = 15 * 60 * 1000;

  // Stale prematch threshold (3 hours past kickoff)
  static readonly STALE_PREMATCH_THRESHOLD_MS = 3 * 60 * 60 * 1000;

  // Approaching kickoff window (15 minutes before kickoff)
  static readonly APPROACHING_WINDOW_MS = 15 * 60 * 1000;

  // Maximum duration a match can remain in a LIVE/in-play state before being considered expired/stale.
  static readonly MAX_LIVE_MATCH_AGE_MS = 210 * 60 * 1000; // 3.5 hours (210 minutes)

  /**
   * Returns the UTC start timestamp of the current twice-daily schedule slot:
   * Slot 0: 00:00:00.000Z (Midnight UTC)
   * Slot 1: 12:00:00.000Z (Midday UTC)
   */
  static getScheduleSyncSlotStart(now: Date): Date {
    const year = now.getUTCFullYear();
    const month = now.getUTCMonth();
    const day = now.getUTCDate();
    const slotHour = now.getUTCHours() < 12 ? 0 : 12;
    return new Date(Date.UTC(year, month, day, slotHour, 0, 0, 0));
  }

  /**
   * Returns the UTC start timestamp of the next upcoming twice-daily schedule slot.
   */
  static getNextScheduleSyncSlotStart(now: Date): Date {
    const year = now.getUTCFullYear();
    const month = now.getUTCMonth();
    const day = now.getUTCDate();
    if (now.getUTCHours() < 12) {
      return new Date(Date.UTC(year, month, day, 12, 0, 0, 0));
    }
    return new Date(Date.UTC(year, month, day + 1, 0, 0, 0, 0));
  }

  /**
   * Deterministically evaluates whether twice-daily schedule discovery is due.
   * Due if never discovered, or if the last discovery was before the start of the current slot.
   */
  static isScheduleSyncDue(now: Date, lastDiscovery?: Date | string | null): boolean {
    if (!lastDiscovery) return true;
    const lastDate = lastDiscovery instanceof Date ? lastDiscovery : new Date(lastDiscovery);
    if (isNaN(lastDate.getTime())) return true;
    const currentSlotStart = this.getScheduleSyncSlotStart(now);
    return lastDate.getTime() < currentSlotStart.getTime();
  }

  /**
   * Calculates recommended polling interval based on remaining daily budget.
   *
   * Thresholds:
   *   remainingBudget > 50           → 180s (180,000 ms)
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
      // Live state expiration guard: if kickoff was > MAX_LIVE_MATCH_AGE_MS ago,
      // the match cannot plausibly still be in progress.
      if (diffMs < -this.MAX_LIVE_MATCH_AGE_MS) {
        const timePastMaxAge = -diffMs - this.MAX_LIVE_MATCH_AGE_MS;
        if (timePastMaxAge <= this.RECENTLY_FINISHED_WINDOW_MS) {
          return 'RECENTLY_FINISHED';
        }
        return 'FINAL';
      }
      // If kickoff is in the future beyond approaching window (> 15m), match cannot be in progress
      if (diffMs > this.APPROACHING_WINDOW_MS) {
        return 'SCHEDULED_FUTURE';
      }
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
    // 4a. Ancient prematch: kickoff was > 24 hours in the past — match is abandoned/postponed
    if (diffMs < -24 * 60 * 60 * 1000) {
      return 'FINAL';
    }

    // 4b. Stale prematch: kickoff was > 3 hours in the past but < 24 hours
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
   *   2. Undiscovered Daily Schedule: Return DISCOVER_SCHEDULE (prevents starvation of daily schedule).
   *   3. Active Live Matches: Return POLL_LIVE with budget-scaled interval.
   *   4. Recently Finished Matches: Return FINALIZE for final score/event sync.
   *   5. Stale Prematch: Return RECOVER_STALE to update postponed/cancelled status.
   *   6. Approaching Match: Wait for kickoff.
   *   7. Otherwise: Return IDLE.
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

    // 2. Twice-Daily Schedule Sync Priority
    // Fetch and cache the 3-day fixture schedule strictly twice a day (midnight UTC and midday)
    const scheduleSyncDue = fixturesSummary.isScheduleSyncDue ??
      this.isScheduleSyncDue(now, fixturesSummary.lastScheduleDiscoveryAt);

    if (!fixturesSummary.scheduleKnownForToday || (fixturesSummary.isScheduleSyncDue === true)) {
      return {
        action: 'DISCOVER_SCHEDULE',
        priority: 'NORMAL',
        recommendedNextPollMs: 3600_000,
        reason: !fixturesSummary.scheduleKnownForToday
          ? "Today's match schedule has not been discovered yet."
          : "Twice-daily schedule sync due (midnight/midday UTC).",
      };
    }

    // 3. Live Fixtures Priority (Dynamic Priority Scaling, Live Burst Mode, & The Halftime (HT) Suspend)
    if (fixturesSummary.liveCount > 0) {
      // 3a. The Halftime (HT) Suspend:
      // If all active live matches (or all followed live matches) are in HT,
      // pause polling for the 15-minute break to eliminate wasted requests.
      const isHtSuspended = fixturesSummary.hasPinnedOrFollowedLive
        ? (fixturesSummary.pinnedOrFollowedInHalftime ?? fixturesSummary.allLiveInHalftime)
        : fixturesSummary.allLiveInHalftime;

      if (isHtSuspended) {
        const suspendMs = fixturesSummary.halftimeRemainingMs ?? this.HALFTIME_SUSPEND_MS;
        return {
          action: 'IDLE',
          priority: 'NORMAL',
          recommendedNextPollMs: suspendMs,
          reason: fixturesSummary.hasPinnedOrFollowedLive
            ? 'Halftime suspend: followed live match is in HT. Pausing polling for 15-minute break.'
            : 'Halftime suspend: all live matches in HT. Pausing polling for 15-minute break.',
        };
      }

      // 3b. Dynamic Priority Scaling:
      // - Pinned / Followed Live Matches: Strict 90-second Live Burst Mode running exclusively from 1H through FT
      if (fixturesSummary.hasPinnedOrFollowedLive) {
        return {
          action: 'POLL_LIVE',
          priority: 'NORMAL',
          recommendedNextPollMs: this.PINNED_LIVE_BURST_CADENCE_MS,
          reason: `Pinned/followed live match in active play (${fixturesSummary.pinnedOrFollowedLiveCount ?? 1} active). 90-second Live Burst Mode active.`,
        };
      }

      // - Unpinned Live Matches: Broad 15–20 minute interval
      if (fixturesSummary.hasPinnedOrFollowedLive === false) {
        return {
          action: 'POLL_LIVE',
          priority: 'NORMAL',
          recommendedNextPollMs: this.UNPINNED_LIVE_CADENCE_MS,
          reason: `Live match in progress (${fixturesSummary.liveCount} active unpinned). Polling with broad 15-minute interval.`,
        };
      }

      // Fallback for generic/legacy callers where pinned state is unspecified
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

    // 4. Recently Finished Reconciliation
    if (fixturesSummary.recentlyFinishedCount > 0) {
      return {
        action: 'FINALIZE',
        priority: 'NORMAL',
        recommendedNextPollMs: 60_000,
        reason: `Recently finished match requires final reconciliation (${fixturesSummary.recentlyFinishedCount} pending).`,
      };
    }

    // 5. Stale Prematch Recovery
    if (fixturesSummary.stalePrematchCount > 0) {
      return {
        action: 'RECOVER_STALE',
        priority: 'NORMAL',
        recommendedNextPollMs: 300_000,
        reason: `Stale prematch fixtures detected (${fixturesSummary.stalePrematchCount} past kickoff). Probing for postponed/cancelled status.`,
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

    // 7. Fully Idle (Check every 4–6 hours, bounded by next twice-daily schedule slot)
    if (fixturesSummary.hasPinnedOrFollowedLive !== undefined) {
      const msUntilNextScheduleSlot = Math.max(
        60_000,
        this.getNextScheduleSyncSlotStart(now).getTime() - now.getTime()
      );
      const idleDelayMs = Math.min(this.IDLE_NO_LIVE_CADENCE_MS, msUntilNextScheduleSlot);

      return {
        action: 'IDLE',
        priority: 'NORMAL',
        recommendedNextPollMs: idleDelayMs,
        reason: `No live matches. System idle (checking every ${Math.round(idleDelayMs / 3600000)}h).`,
      };
    }

    return {
      action: 'IDLE',
      priority: 'NORMAL',
      recommendedNextPollMs: null,
      reason: 'No live, approaching, stale, or unfinalized fixtures. System is idle.',
    };
  }
}
