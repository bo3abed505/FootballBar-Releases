import { FootballProvider, ProviderFixture } from '../providers/football-provider.js';
import {
  normalizeCompetition,
  normalizeSeason,
  normalizeTeam,
  normalizeFixture,
  CanonicalCompetition,
  CanonicalSeason,
  CanonicalTeam,
  CanonicalFixture,
} from '../normalization/fixture.js';
import { normalizeEvent, CanonicalEvent } from '../normalization/event.js';

export interface NormalizedAcquisitionBatch {
  competitions: CanonicalCompetition[];
  seasons: CanonicalSeason[];
  teams: CanonicalTeam[];
  fixtures: CanonicalFixture[];
  events: CanonicalEvent[];
}

export class AcquisitionService {
  constructor(private readonly provider: FootballProvider) {}

  /**
   * Fetches data from provider and normalizes all entities into canonical format
   */
  async acquireAll(): Promise<NormalizedAcquisitionBatch> {
    const rawFixtures: ProviderFixture[] = await this.provider.getFixtures();

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
}
