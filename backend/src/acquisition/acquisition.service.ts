import { IDataProvider, NormalizedMatch } from '../providers/types.js';
import { FixtureFilter } from '../providers/types.js';
import {
  CanonicalCompetition,
  CanonicalSeason,
  CanonicalTeam,
  CanonicalFixture,
  normalizeCompetition,
  normalizeSeason,
  normalizeTeam,
  normalizeFixture,
} from '../normalization/fixture.js';
import { CanonicalEvent, normalizeEvent } from '../normalization/event.js';

export interface NormalizedAcquisitionBatch {
  competitions: CanonicalCompetition[];
  seasons: CanonicalSeason[];
  teams: CanonicalTeam[];
  fixtures: CanonicalFixture[];
  events: CanonicalEvent[];
}

export class AcquisitionService {
  private readonly inFlight = new Map<string, Promise<NormalizedAcquisitionBatch>>();

  constructor(private readonly provider: IDataProvider) {}

  /**
   * Fetches data from provider and normalizes all entities into canonical format.
   * Single-flight promise coalescing deduplicates identical concurrent requests.
   */
  async acquireAll(filter?: FixtureFilter): Promise<NormalizedAcquisitionBatch> {
    const key = filter ? JSON.stringify(filter) : 'all';
    const existing = this.inFlight.get(key);
    if (existing) {
      return existing;
    }

    const promise = (async () => {
      try {
        const providerName = this.provider?.name || (this.provider as any)?.providerName || 'unknown';

        if (typeof (this.provider as any).getFixtures === 'function') {
          const rawFixtures = await (this.provider as any).getFixtures(filter);
          const compMap = new Map<string, CanonicalCompetition>();
          const seasonMap = new Map<string, CanonicalSeason>();
          const teamMap = new Map<string, CanonicalTeam>();
          const fixtureList: CanonicalFixture[] = [];
          const eventList: CanonicalEvent[] = [];

          for (const raw of rawFixtures) {
            if (raw.competition && !compMap.has(raw.competition.id)) {
              compMap.set(raw.competition.id, normalizeCompetition(raw.competition));
            }
            if (raw.season && !seasonMap.has(raw.season.id)) {
              seasonMap.set(raw.season.id, normalizeSeason(raw.season));
            }
            if (raw.homeTeam && !teamMap.has(raw.homeTeam.id)) {
              teamMap.set(raw.homeTeam.id, normalizeTeam(raw.homeTeam));
            }
            if (raw.awayTeam && !teamMap.has(raw.awayTeam.id)) {
              teamMap.set(raw.awayTeam.id, normalizeTeam(raw.awayTeam));
            }
            fixtureList.push(normalizeFixture(raw));
            if (raw.events && raw.events.length > 0) {
              for (const ev of raw.events) {
                eventList.push(normalizeEvent(ev));
              }
            }
          }

          return {
            competitions: Array.from(compMap.values()),
            seasons: Array.from(seasonMap.values()),
            teams: Array.from(teamMap.values()),
            fixtures: fixtureList,
            events: eventList,
          };
        }

        let rawMatches: NormalizedMatch[] = [];
        if (filter?.liveOnly) {
          rawMatches = await this.provider.getLiveMatches();
        } else if (filter?.date) {
          rawMatches = await this.provider.getDailyMatches(filter.date);
        } else {
          rawMatches = await this.provider.getLiveMatches();
        }

        const compMap = new Map<string, CanonicalCompetition>();
        const seasonMap = new Map<string, CanonicalSeason>();
        const teamMap = new Map<string, CanonicalTeam>();
        const fixtureList: CanonicalFixture[] = [];
        const eventList: CanonicalEvent[] = [];

        for (const raw of rawMatches) {
          const compId = raw.competitionId || 'unknown_comp';
          if (!compMap.has(compId)) {
            compMap.set(compId, {
              id: compId,
              name: raw.competitionName || 'Unknown Competition',
              code: raw.competitionCode ?? null,
              type: 'league',
              country: raw.competitionCountry ?? null,
              logoUrl: raw.competitionLogoUrl ?? null,
              provider: providerName,
              providerId: compId,
            });
          }

          const seasonId = `season_${compId}_unknown`;
          if (!seasonMap.has(seasonId)) {
            seasonMap.set(seasonId, {
              id: seasonId,
              competitionId: compId,
              year: new Date().getFullYear(),
              displayYear: String(new Date().getFullYear()),
              startDate: new Date(new Date().getFullYear(), 0, 1),
              endDate: new Date(new Date().getFullYear(), 11, 31),
              isCurrent: true,
            });
          }

          if (raw.homeTeam?.providerId && !teamMap.has(raw.homeTeam.providerId)) {
            teamMap.set(raw.homeTeam.providerId, {
              id: raw.homeTeam.providerId,
              name: raw.homeTeam.name,
              shortName: raw.homeTeam.shortName || raw.homeTeam.name,
              code: raw.homeTeam.shortName ?? null,
              country: null,
              logoUrl: raw.homeTeam.logoUrl ?? null,
              venueName: null,
              provider: providerName,
              providerId: raw.homeTeam.providerId,
            });
          }

          if (raw.awayTeam?.providerId && !teamMap.has(raw.awayTeam.providerId)) {
            teamMap.set(raw.awayTeam.providerId, {
              id: raw.awayTeam.providerId,
              name: raw.awayTeam.name,
              shortName: raw.awayTeam.shortName || raw.awayTeam.name,
              code: raw.awayTeam.shortName ?? null,
              country: null,
              logoUrl: raw.awayTeam.logoUrl ?? null,
              venueName: null,
              provider: providerName,
              providerId: raw.awayTeam.providerId,
            });
          }

          const isFinished = ['FINISHED', 'FT', 'AET', 'PEN', 'POSTMATCH'].includes(raw.time.status);
          fixtureList.push({
            id: raw.providerMatchId,
            competitionId: compId,
            seasonId: seasonId,
            homeTeamId: raw.homeTeam.providerId || 'unknown_team',
            awayTeamId: raw.awayTeam.providerId || 'unknown_team',
            round: null,
            kickoffTime: new Date(raw.time.scheduledKickoff),
            state: raw.time.status,
            elapsedMinute: raw.time.minute || 0,
            extraMinute: raw.time.addedTime || 0,
            homeScore: raw.homeTeam.score || 0,
            awayScore: raw.awayTeam.score || 0,
            homeScoreHt: null,
            awayScoreHt: null,
            homeScoreEt: null,
            awayScoreEt: null,
            homeScorePen: null,
            awayScorePen: null,
            venue: null,
            referee: null,
            lastPolledAt: new Date(),
            finalizedAt: isFinished ? new Date() : null,
            provider: this.provider.name,
            providerId: raw.providerMatchId,
          });
        }

        return {
          competitions: Array.from(compMap.values()),
          seasons: Array.from(seasonMap.values()),
          teams: Array.from(teamMap.values()),
          fixtures: fixtureList,
          events: eventList,
        };
      } finally {
        this.inFlight.delete(key);
      }
    })();

    this.inFlight.set(key, promise);
    return promise;
  }

  /**
   * Acquires active live matches (consolidated single provider query).
   */
  async acquireLive(competitionIds?: number[], cycleId?: string): Promise<NormalizedAcquisitionBatch> {
    return this.acquireAll({ liveOnly: true, competitionIds, cycleId });
  }

  /**
   * Acquires full match schedule for a specific date across target competitions.
   */
  async acquireScheduled(date: string, competitionIds?: number[], cycleId?: string): Promise<NormalizedAcquisitionBatch> {
    return this.acquireAll({ date, competitionIds, cycleId });
  }

  /**
   * Acquires full match schedule for a date range across target competitions.
   */
  async acquireScheduledRange(fromDate: string, toDate: string, competitionIds?: number[], cycleId?: string): Promise<NormalizedAcquisitionBatch> {
    return this.acquireAll({ fromDate, toDate, competitionIds, cycleId });
  }

  /**
   * Acquires finalization data for recently completed matches.
   */
  async acquireFinalization(competitionIds?: number[], cycleId?: string): Promise<NormalizedAcquisitionBatch> {
    return this.acquireAll({ liveOnly: true, competitionIds, cycleId });
  }

  /**
   * Acquires updated status for stale prematch fixtures to check for postponement/cancellation.
   */
  async acquireStaleRecovery(competitionIds?: number[], cycleId?: string): Promise<NormalizedAcquisitionBatch> {
    return this.acquireAll({ competitionIds, cycleId });
  }
}

