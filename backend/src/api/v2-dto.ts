/**
 * v2-dto.ts
 *
 * Delivery API v2 serializers and trust metadata computation.
 * Maps internal models to canonical IDs (ft_...) and computes per-fixture
 * freshness and trust states.
 */

export interface V2TrustMetadata {
  freshnessState: 'FRESH' | 'STALE' | 'FINAL' | 'UNKNOWN';
  sourceObservedAt: string | null;
  dataAgeSeconds: number | null;
  provider: string | null;
}

export interface V2TrayMatch {
  id: string; // canonical_id e.g. 'ft_fix_...'
  legacyId?: string; // legacy primary key e.g. 'fix_...'
  competition: {
    id: string; // canonical_id e.g. 'ft_comp_...'
    legacyId?: string;
    name?: string;
    code?: string;
    country?: string;
    logoUrl?: string;
  };
  homeTeam: {
    id: string; // canonical_id e.g. 'ft_team_...'
    legacyId?: string;
    name?: string;
    shortName?: string;
    logoUrl?: string;
  };
  awayTeam: {
    id: string; // canonical_id e.g. 'ft_team_...'
    legacyId?: string;
    name?: string;
    shortName?: string;
    logoUrl?: string;
  };
  state: string;
  elapsedMinute: number;
  extraMinute: number;
  score: {
    home: number;
    away: number;
    display: string;
  };
  kickoffTime: Date | string;
  recentEvents: Array<{
    id: string;
    type: string;
    minute: number;
    extraMinute: number;
    teamId: string;
    playerName?: string;
    description?: string;
  }>;
  trust: V2TrustMetadata;
}

export interface V2MatchDetail {
  id: string; // canonical_id
  legacyId?: string;
  competition: {
    id: string; // canonical_id
    legacyId?: string;
    name?: string;
    code?: string;
    type?: string;
    country?: string;
    logoUrl?: string;
  };
  season?: any;
  homeTeam: {
    id: string; // canonical_id
    legacyId?: string;
    name?: string;
    shortName?: string;
    code?: string;
    country?: string;
    logoUrl?: string;
  };
  awayTeam: {
    id: string; // canonical_id
    legacyId?: string;
    name?: string;
    shortName?: string;
    code?: string;
    country?: string;
    logoUrl?: string;
  };
  state: string;
  elapsedMinute: number;
  extraMinute: number;
  score: {
    home: number;
    away: number;
    halfTime?: {
      home?: number | null;
      away?: number | null;
    };
    display: string;
  };
  venue?: string | null;
  referee?: string | null;
  kickoffTime: Date | string;
  events: any[];
  lineups: any[];
  statistics: any[];
  trust: V2TrustMetadata;
}

/**
 * Computes per-fixture trust metadata based on state and provenance columns.
 */
export function calculateTrustMetadata(fixture: any, now: Date = new Date()): V2TrustMetadata {
  const state = (fixture.state || '').toUpperCase();
  const LIVE_STATES = ['LIVE', 'HALFTIME', '1H', '2H', 'ET', 'P', 'BT', 'IN_PLAY'];
  const FINISHED_STATES = ['FINISHED', 'FT', 'AET', 'PEN', 'POSTMATCH'];
  const PREMATCH_STATES = ['PREMATCH', 'NS', 'TBD'];

  let sourceObservedAt: Date | null = null;
  let provider: string | null = null;
  let freshnessState: 'FRESH' | 'STALE' | 'FINAL' | 'UNKNOWN' = 'UNKNOWN';

  if (LIVE_STATES.includes(state)) {
    // Live match: observation timestamp from live polling
    if (fixture.liveObservedAt) {
      sourceObservedAt = new Date(fixture.liveObservedAt);
    } else if (fixture.lastPolledAt) {
      sourceObservedAt = new Date(fixture.lastPolledAt);
    } else if (fixture.updatedAt) {
      sourceObservedAt = new Date(fixture.updatedAt);
    }
    provider = fixture.liveSourceProvider || null;

    if (sourceObservedAt && !isNaN(sourceObservedAt.getTime())) {
      const dataAgeSeconds = Math.max(0, Math.floor((now.getTime() - sourceObservedAt.getTime()) / 1000));
      // Live matches are considered FRESH if observed within 120 seconds
      freshnessState = dataAgeSeconds <= 120 ? 'FRESH' : 'STALE';
      return {
        freshnessState,
        sourceObservedAt: sourceObservedAt.toISOString(),
        dataAgeSeconds,
        provider,
      };
    }
  } else if (FINISHED_STATES.includes(state)) {
    // Finished match: final state
    if (fixture.liveObservedAt) {
      sourceObservedAt = new Date(fixture.liveObservedAt);
    } else if (fixture.lastPolledAt) {
      sourceObservedAt = new Date(fixture.lastPolledAt);
    } else if (fixture.updatedAt) {
      sourceObservedAt = new Date(fixture.updatedAt);
    }
    provider = fixture.liveSourceProvider || fixture.scheduleSourceProvider || null;

    return {
      freshnessState: 'FINAL',
      sourceObservedAt: sourceObservedAt ? sourceObservedAt.toISOString() : null,
      dataAgeSeconds: sourceObservedAt ? Math.max(0, Math.floor((now.getTime() - sourceObservedAt.getTime()) / 1000)) : null,
      provider,
    };
  } else if (PREMATCH_STATES.includes(state) || !state) {
    // Scheduled match: observation timestamp from schedule discovery
    if (fixture.scheduleObservedAt) {
      sourceObservedAt = new Date(fixture.scheduleObservedAt);
    } else if (fixture.lastPolledAt) {
      sourceObservedAt = new Date(fixture.lastPolledAt);
    } else if (fixture.updatedAt) {
      sourceObservedAt = new Date(fixture.updatedAt);
    }
    provider = fixture.scheduleSourceProvider || null;

    if (sourceObservedAt && !isNaN(sourceObservedAt.getTime())) {
      const dataAgeSeconds = Math.max(0, Math.floor((now.getTime() - sourceObservedAt.getTime()) / 1000));
      // Scheduled fixtures considered FRESH if observed within 24 hours (86400 seconds)
      freshnessState = dataAgeSeconds <= 86400 ? 'FRESH' : 'STALE';
      return {
        freshnessState,
        sourceObservedAt: sourceObservedAt.toISOString(),
        dataAgeSeconds,
        provider,
      };
    }
  }

  // Fallback if timestamps are not yet populated
  if (fixture.lastPolledAt || fixture.updatedAt) {
    sourceObservedAt = new Date(fixture.lastPolledAt || fixture.updatedAt);
    const dataAgeSeconds = Math.max(0, Math.floor((now.getTime() - sourceObservedAt.getTime()) / 1000));
    return {
      freshnessState: 'UNKNOWN',
      sourceObservedAt: sourceObservedAt ? sourceObservedAt.toISOString() : null,
      dataAgeSeconds,
      provider,
    };
  }

  return {
    freshnessState: 'UNKNOWN',
    sourceObservedAt: null,
    dataAgeSeconds: null,
    provider: null,
  };
}

/**
 * Serializes a database fixture into the Delivery API v2 Tray Match contract.
 */
export function formatV2TrayMatch(m: any, now: Date = new Date()): V2TrayMatch {
  return {
    id: m.canonicalId || m.id,
    legacyId: m.id,
    competition: {
      id: m.competition?.canonicalId || m.competition?.id,
      legacyId: m.competition?.id,
      name: m.competition?.name,
      code: m.competition?.code,
      country: m.competition?.country,
      logoUrl: m.competition?.logoUrl,
    },
    homeTeam: {
      id: m.homeTeam?.canonicalId || m.homeTeam?.id,
      legacyId: m.homeTeam?.id,
      name: m.homeTeam?.name,
      shortName: m.homeTeam?.shortName,
      logoUrl: m.homeTeam?.logoUrl,
    },
    awayTeam: {
      id: m.awayTeam?.canonicalId || m.awayTeam?.id,
      legacyId: m.awayTeam?.id,
      name: m.awayTeam?.name,
      shortName: m.awayTeam?.shortName,
      logoUrl: m.awayTeam?.logoUrl,
    },
    state: m.state,
    elapsedMinute: m.elapsedMinute,
    extraMinute: m.extraMinute,
    score: {
      home: m.homeScore,
      away: m.awayScore,
      display: `${m.homeScore} - ${m.awayScore}`,
    },
    kickoffTime: m.kickoffTime,
    recentEvents: (m.events || []).map((e: any) => ({
      id: e.id,
      type: e.type,
      minute: e.minute,
      extraMinute: e.extraMinute,
      teamId: e.teamId,
      playerName: e.playerName,
      description: e.description,
    })),
    trust: calculateTrustMetadata(m, now),
  };
}

/**
 * Serializes a database fixture into the Delivery API v2 Match Detail contract.
 */
export function formatV2MatchDetail(fixture: any, now: Date = new Date()): V2MatchDetail {
  return {
    id: fixture.canonicalId || fixture.id,
    legacyId: fixture.id,
    competition: {
      id: fixture.competition?.canonicalId || fixture.competition?.id,
      legacyId: fixture.competition?.id,
      name: fixture.competition?.name,
      code: fixture.competition?.code,
      type: fixture.competition?.type,
      country: fixture.competition?.country,
      logoUrl: fixture.competition?.logoUrl,
    },
    season: fixture.season,
    homeTeam: {
      id: fixture.homeTeam?.canonicalId || fixture.homeTeam?.id,
      legacyId: fixture.homeTeam?.id,
      name: fixture.homeTeam?.name,
      shortName: fixture.homeTeam?.shortName,
      code: fixture.homeTeam?.code,
      country: fixture.homeTeam?.country,
      logoUrl: fixture.homeTeam?.logoUrl,
    },
    awayTeam: {
      id: fixture.awayTeam?.canonicalId || fixture.awayTeam?.id,
      legacyId: fixture.awayTeam?.id,
      name: fixture.awayTeam?.name,
      shortName: fixture.awayTeam?.shortName,
      code: fixture.awayTeam?.code,
      country: fixture.awayTeam?.country,
      logoUrl: fixture.awayTeam?.logoUrl,
    },
    state: fixture.state,
    elapsedMinute: fixture.elapsedMinute,
    extraMinute: fixture.extraMinute,
    score: {
      home: fixture.homeScore,
      away: fixture.awayScore,
      halfTime: {
        home: fixture.homeScoreHt,
        away: fixture.awayScoreHt,
      },
      display: `${fixture.homeScore} - ${fixture.awayScore}`,
    },
    venue: fixture.venue,
    referee: fixture.referee,
    kickoffTime: fixture.kickoffTime,
    events: fixture.events || [],
    lineups: fixture.lineups || [],
    statistics: fixture.statistics || [],
    trust: calculateTrustMetadata(fixture, now),
  };
}

export interface V2CatalogTeam {
  id: string; // canonical_id e.g. 'ft_team_...'
  legacyId?: string;
  name: string;
  shortName?: string;
  code?: string | null;
  country?: string | null;
  logo: string | null;
  logoUrl?: string | null;
}

export interface V2CatalogCompetition {
  id: string; // canonical_id e.g. 'ft_comp_...'
  legacyId?: string;
  name: string;
  code?: string | null;
  country?: string | null;
  logo: string | null;
  logoUrl?: string | null;
}

export function formatV2CatalogTeam(team: {
  canonicalId: string;
  id?: string;
  name: string;
  shortName?: string;
  code?: string | null;
  country?: string | null;
  logoUrl?: string | null;
}): V2CatalogTeam {
  return {
    id: team.canonicalId,
    legacyId: team.id,
    name: team.name,
    shortName: team.shortName,
    code: team.code ?? null,
    country: team.country ?? null,
    logo: team.logoUrl ?? null,
    logoUrl: team.logoUrl ?? null,
  };
}

export function formatV2CatalogCompetition(comp: {
  canonicalId: string;
  id?: string;
  name: string;
  code?: string | null;
  country?: string | null;
  logoUrl?: string | null;
}): V2CatalogCompetition {
  return {
    id: comp.canonicalId,
    legacyId: comp.id,
    name: comp.name,
    code: comp.code ?? null,
    country: comp.country ?? null,
    logo: comp.logoUrl ?? null,
    logoUrl: comp.logoUrl ?? null,
  };
}

