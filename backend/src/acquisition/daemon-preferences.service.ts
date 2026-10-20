/**
 * daemon-preferences.service.ts
 *
 * Manages dynamic runtime user preferences in the Node.js daemon.
 * Bridges client-selected leagues/teams with background acquisition policies,
 * resolving canonical and alias IDs into provider-level numeric IDs without restarting.
 */

import { inArray } from 'drizzle-orm';
import { db, schema } from '../db/index.js';
import { extractProviderId } from '../db/provider-alias.js';
import { getTargetCompetitionIds } from './competition-config.js';

export interface IProviderSwapObserver {
  swapProvider(newProviderName: string): void;
  setTargetCompetitionIds(ids?: number[]): void;
  setPinnedMatchIds(ids: Array<string | number>): void;
}

export interface UpdatePreferencesPayload {
  competitionIds?: Array<string | number>;
  followedCompetitions?: Array<{ id: string; name?: string; code?: string }>;
  teamIds?: Array<string | number>;
  followedTeams?: Array<{ id: string; name?: string; code?: string }>;
  pinnedMatchIds?: Array<string | number>;
  pinnedMatchId?: string | number | null;
  activeProvider?: string | null;
}

export interface DaemonPreferencesState {
  followedCompetitions: string[];
  followedTeams: string[];
  effectiveCompetitionNumericIds: number[];
  effectiveTeamNumericIds: number[];
  pinnedMatchIds: string[];
  activeProvider: string;
  lastUpdatedAt: string;
}

export class DaemonPreferencesService {
  private followedCompetitions: Set<string> = new Set();
  private followedTeams: Set<string> = new Set();
  private pinnedMatchIds: Set<string> = new Set();
  private effectiveCompetitionNumericIds: number[] = [];
  private effectiveTeamNumericIds: number[] = [];
  private lastUpdatedAt: Date = new Date();
  private activeProvider: string = 'fotmob';
  private scheduler: IProviderSwapObserver | null = null;

  constructor() {
    // 1. Initialize baseline allowlist from .env TARGET_COMPETITION_IDS
    const initialNumericIds = getTargetCompetitionIds();
    this.effectiveCompetitionNumericIds = initialNumericIds;
    for (const id of initialNumericIds) {
      this.followedCompetitions.add(id.toString());
      this.followedCompetitions.add(`comp_${id}`);
    }
  }

  public bindScheduler(scheduler: IProviderSwapObserver): void {
    this.scheduler = scheduler;
    if (scheduler && typeof (scheduler as any).bindPreferencesService === 'function') {
      (scheduler as any).bindPreferencesService(this);
    }
    if (
      this.effectiveCompetitionNumericIds.length > 0 &&
      scheduler &&
      typeof (scheduler as any).setTargetCompetitionIds === 'function'
    ) {
      scheduler.setTargetCompetitionIds(this.effectiveCompetitionNumericIds);
    }
    if (
      this.pinnedMatchIds.size > 0 &&
      scheduler &&
      typeof (scheduler as any).setPinnedMatchIds === 'function'
    ) {
      (scheduler as any).setPinnedMatchIds(Array.from(this.pinnedMatchIds));
    }
  }

  public getPreferences(): DaemonPreferencesState {
    return {
      followedCompetitions: Array.from(this.followedCompetitions),
      followedTeams: Array.from(this.followedTeams),
      effectiveCompetitionNumericIds: this.effectiveCompetitionNumericIds,
      effectiveTeamNumericIds: this.effectiveTeamNumericIds,
      pinnedMatchIds: Array.from(this.pinnedMatchIds),
      activeProvider: this.activeProvider,
      lastUpdatedAt: this.lastUpdatedAt.toISOString(),
    };
  }

  public getEffectiveCompetitionNumericIds(): number[] {
    return [...this.effectiveCompetitionNumericIds];
  }

  /**
   * Resolves raw identifiers (canonical 'ft_comp_...', 'comp_39', '39') to numeric provider IDs.
   */
  public async resolveCompetitionNumericIds(identifiers: string[]): Promise<number[]> {
    const numericSet = new Set<number>();
    const canonicalCandidates: string[] = [];

    for (const raw of identifiers) {
      const trimmed = String(raw).trim();
      if (!trimmed) continue;

      if (/^\d+$/.test(trimmed)) {
        numericSet.add(parseInt(trimmed, 10));
      } else if (trimmed.startsWith('comp_') && /^\d+$/.test(trimmed.slice(5))) {
        numericSet.add(parseInt(trimmed.slice(5), 10));
      } else if (trimmed.startsWith('ft_comp_')) {
        canonicalCandidates.push(trimmed);
      }
    }

    if (canonicalCandidates.length > 0) {
      const rows = await db
        .select({
          providerId: schema.competitionProviderAliases.providerId,
        })
        .from(schema.competitionProviderAliases)
        .where(
          inArray(schema.competitionProviderAliases.canonicalCompetitionId, canonicalCandidates)
        );

      for (const row of rows) {
        const num = parseInt(extractProviderId(row.providerId, 'comp'), 10);
        if (!isNaN(num) && num > 0) {
          numericSet.add(num);
        }
      }
    }

    return Array.from(numericSet);
  }

  /**
   * Resolves raw team identifiers to numeric provider IDs.
   */
  public async resolveTeamNumericIds(identifiers: string[]): Promise<number[]> {
    const numericSet = new Set<number>();
    const canonicalCandidates: string[] = [];

    for (const raw of identifiers) {
      const trimmed = String(raw).trim();
      if (!trimmed) continue;

      if (/^\d+$/.test(trimmed)) {
        numericSet.add(parseInt(trimmed, 10));
      } else if (trimmed.startsWith('team_') && /^\d+$/.test(trimmed.slice(5))) {
        numericSet.add(parseInt(trimmed.slice(5), 10));
      } else if (trimmed.startsWith('ft_team_')) {
        canonicalCandidates.push(trimmed);
      }
    }

    if (canonicalCandidates.length > 0) {
      const rows = await db
        .select({
          providerId: schema.teamProviderAliases.providerId,
        })
        .from(schema.teamProviderAliases)
        .where(inArray(schema.teamProviderAliases.canonicalTeamId, canonicalCandidates));

      for (const row of rows) {
        const num = parseInt(extractProviderId(row.providerId, 'team'), 10);
        if (!isNaN(num) && num > 0) {
          numericSet.add(num);
        }
      }
    }

    return Array.from(numericSet);
  }

  /**
   * Dynamically applies preferences submitted by the WinUI 3 client.
   */
  public async updatePreferences(payload: UpdatePreferencesPayload): Promise<DaemonPreferencesState> {
    const compInputs: string[] = [];
    if (payload.competitionIds) {
      compInputs.push(...payload.competitionIds.map(String));
    }
    if (payload.followedCompetitions) {
      compInputs.push(...payload.followedCompetitions.map((c) => c.id));
    }

    const teamInputs: string[] = [];
    if (payload.teamIds) {
      teamInputs.push(...payload.teamIds.map(String));
    }
    if (payload.followedTeams) {
      teamInputs.push(...payload.followedTeams.map((t) => t.id));
    }

    const pinnedInputs: string[] = [];
    if (payload.pinnedMatchIds) {
      pinnedInputs.push(...payload.pinnedMatchIds.map(String));
    }
    if (payload.pinnedMatchId) {
      pinnedInputs.push(String(payload.pinnedMatchId));
    }

    // Resolve numeric provider IDs
    const resolvedCompIds = await this.resolveCompetitionNumericIds(compInputs);
    const resolvedTeamIds = await this.resolveTeamNumericIds(teamInputs);

    // Update state
    this.followedCompetitions = new Set(compInputs);
    this.followedTeams = new Set(teamInputs);
    this.pinnedMatchIds = new Set(pinnedInputs);
    this.effectiveCompetitionNumericIds = resolvedCompIds;
    this.effectiveTeamNumericIds = resolvedTeamIds;
    this.lastUpdatedAt = new Date();

    // Propagate to background scheduler
    if (this.scheduler) {
      this.scheduler.setTargetCompetitionIds(
        resolvedCompIds.length > 0 ? resolvedCompIds : undefined
      );
      this.scheduler.setPinnedMatchIds(Array.from(this.pinnedMatchIds));
    }
    
    if (payload.activeProvider && typeof payload.activeProvider === 'string') {
      // Directive 2: FotMob is the authoritative consolidated provider in v1.2.
      this.activeProvider = 'fotmob';
    }

    return this.getPreferences();
  }
}
