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
  syncState?: 'SYNCING' | 'IDLE' | 'SYNCED' | string | null;
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
  commentary: any[];
  lineups: any[];
  statistics: any[];
  trust: V2TrustMetadata;
}

/**
 * Computes per-fixture trust metadata based on state and provenance columns.
 */
export function calculateTrustMetadata(fixture: any, now: Date = new Date()): V2TrustMetadata {
  const state = (fixture.state || '').toUpperCase();
  const LIVE_STATES = [
    'LIVE', 'HALFTIME', 'HT', '1H', '2H', 'ET', 'P', 'BT', 'IN_PLAY',
    'FIRST_HALF', 'SECOND_HALF', 'EXTRA_TIME', 'EXTRA_TIME_HALFTIME', 'PENALTIES'
  ];
  const FINISHED_STATES = ['FINISHED', 'FT', 'AET', 'PEN', 'POSTMATCH', 'FINISHED_AET', 'FINISHED_PEN'];
  const PREMATCH_STATES = ['PREMATCH', 'NS', 'TBD', 'TIMED', 'SCHEDULED', 'NOT_STARTED'];

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
      code: m.competition?.code !== 'COMP' ? (m.competition?.code ?? m.competition?.country ?? undefined) : (m.competition?.country !== 'COMP' ? m.competition?.country : undefined),
      country: m.competition?.country !== 'COMP' ? (m.competition?.country ?? m.competition?.code ?? undefined) : undefined,
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
      code: fixture.competition?.code !== 'COMP' ? (fixture.competition?.code ?? fixture.competition?.country ?? undefined) : (fixture.competition?.country !== 'COMP' ? fixture.competition?.country : undefined),
      type: fixture.competition?.type,
      country: fixture.competition?.country !== 'COMP' ? (fixture.competition?.country ?? fixture.competition?.code ?? undefined) : undefined,
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
    events: (() => {
      const homeTeamId = fixture.homeTeam?.canonicalId || fixture.homeTeam?.id || fixture.homeTeamId;
      const awayTeamId = fixture.awayTeam?.canonicalId || fixture.awayTeam?.id || fixture.awayTeamId;
      const homeTeamName = fixture.homeTeam?.name;
      const awayTeamName = fixture.awayTeam?.name;
      const homeTeamLogoUrl = fixture.homeTeam?.logoUrl;
      const awayTeamLogoUrl = fixture.awayTeam?.logoUrl;

      return (fixture.events || []).map((e: any) => {
        const typeLower = String(e.type || '').toLowerCase();
        const evMin = typeof e.minute === 'number' ? e.minute : (parseInt(String(e.minute || e.time || '0'), 10) || 0);
        const isFt = typeLower.includes('fulltime') ||
          typeLower.includes('full_time') ||
          typeLower.includes('full-time') ||
          e.halfStrShort === 'FT' ||
          ((typeLower === 'half' || typeLower.includes('half')) && evMin >= 80);
        const isSecondHalf = !isFt && (typeLower.includes('secondhalf') || typeLower.includes('2ndhalf') || evMin === 46);
        const isHt = !isFt && !isSecondHalf && (typeLower.includes('half') || e.halfStrShort === 'HT');

        let sanitizedType = e.type;
        let sanitizedDetail = e.detail;
        let sanitizedDesc = e.description;

        if (isFt) {
          sanitizedType = 'FullTime';
          sanitizedDetail = 'Full Time';
          sanitizedDesc = String(sanitizedDesc || '').toLowerCase().includes('half time')
            ? 'Full Time whistle. The match concludes.'
            : (sanitizedDesc || 'Full Time whistle. The match concludes.');
        } else if (isSecondHalf) {
          sanitizedType = 'PeriodStart';
          sanitizedDetail = 'Second Half';
          sanitizedDesc = sanitizedDesc || 'Second half is underway.';
        } else if (isHt) {
          sanitizedType = 'HalfTime';
          sanitizedDetail = 'Half Time';
          sanitizedDesc = sanitizedDesc || 'Half Time whistle.';
        }

        const isNeutral = isFt || isSecondHalf || isHt ||
          typeLower.includes('period') ||
          typeLower.includes('kickoff') ||
          typeLower.includes('whistle');

        let teamId = e.teamId ?? null;
        let teamName = e.teamName ?? null;
        let teamLogoUrl = e.teamLogoUrl ?? null;

        if (!isNeutral) {
          if (!teamName || !teamLogoUrl) {
            if (teamId === homeTeamId || teamId === fixture.homeTeam?.id || teamId === fixture.homeTeamId) {
              teamName = homeTeamName ?? null;
              teamLogoUrl = homeTeamLogoUrl ?? null;
            } else if (teamId === awayTeamId || teamId === fixture.awayTeam?.id || teamId === fixture.awayTeamId) {
              teamName = awayTeamName ?? null;
              teamLogoUrl = awayTeamLogoUrl ?? null;
            }
          }
        } else {
          teamId = null;
          teamName = null;
          teamLogoUrl = null;
        }

        return {
          ...e,
          type: sanitizedType,
          detail: sanitizedDetail,
          description: sanitizedDesc,
          teamId,
          teamName,
          teamLogoUrl,
        };
      });
    })(),
    commentary: (() => {
      if (Array.isArray(fixture.commentary) && fixture.commentary.length > 0) {
        return fixture.commentary;
      }

      // Graceful fallback: synthesize reverse-chronological commentary from events + whistle markers
      const synthCommentary: any[] = [];
      const homeTeamId = fixture.homeTeam?.canonicalId || fixture.homeTeam?.id || fixture.homeTeamId;
      const awayTeamId = fixture.awayTeam?.canonicalId || fixture.awayTeam?.id || fixture.awayTeamId;
      const homeTeamName = fixture.homeTeam?.name;
      const awayTeamName = fixture.awayTeam?.name;
      const homeTeamLogoUrl = fixture.homeTeam?.logoUrl;
      const awayTeamLogoUrl = fixture.awayTeam?.logoUrl;

      const eventList = fixture.events || [];
      eventList.forEach((ev: any, idx: number) => {
        const typeLower = String(ev.type || '').toLowerCase();
        const evMin = typeof ev.minute === 'number' ? ev.minute : (parseInt(String(ev.minute || ev.time || '0'), 10) || 0);
        const isFt = typeLower.includes('fulltime') ||
          typeLower.includes('full_time') ||
          typeLower.includes('full-time') ||
          ev.halfStrShort === 'FT' ||
          ((typeLower === 'half' || typeLower.includes('half')) && evMin >= 80);
        const isSecondHalf = !isFt && (typeLower.includes('secondhalf') || typeLower.includes('2ndhalf') || evMin === 46);
        const isHt = !isFt && !isSecondHalf && (typeLower.includes('half') || ev.halfStrShort === 'HT');

        const isNeutral = isFt || isSecondHalf || isHt ||
          typeLower.includes('period') ||
          typeLower.includes('kickoff') ||
          typeLower.includes('whistle');

        let teamId = ev.teamId ?? null;
        let teamName = ev.teamName ?? null;
        let teamLogoUrl = ev.teamLogoUrl ?? null;

        if (!isNeutral) {
          if (!teamName || !teamLogoUrl) {
            if (teamId === homeTeamId || teamId === fixture.homeTeam?.id || teamId === fixture.homeTeamId) {
              teamName = homeTeamName ?? null;
              teamLogoUrl = homeTeamLogoUrl ?? null;
            } else if (teamId === awayTeamId || teamId === fixture.awayTeam?.id || teamId === fixture.awayTeamId) {
              teamName = awayTeamName ?? null;
              teamLogoUrl = awayTeamLogoUrl ?? null;
            }
          }
        } else {
          teamId = null;
          teamName = null;
          teamLogoUrl = null;
        }

        let cType = ev.type;
        let text = ev.description;
        if (isNeutral) {
          cType = 'whistle';
          if (typeLower.includes('kickoff') || typeLower.includes('periodstart')) {
            text = `First half kicks off! ${homeTeamName || 'Home'} vs ${awayTeamName || 'Away'} is underway.`;
          } else if (isSecondHalf) {
            text = 'Second half is underway!';
          } else if (isHt) {
            text = `Half-time whistle blows! Score at the break: ${homeTeamName || 'Home'} ${fixture.homeScore ?? 0}–${fixture.awayScore ?? 0} ${awayTeamName || 'Away'}.`;
          } else if (isFt) {
            text = `Full-time whistle! The referee brings the match to an end: ${homeTeamName || 'Home'} ${fixture.homeScore ?? 0}–${fixture.awayScore ?? 0} ${awayTeamName || 'Away'}.`;
          }
        } else if (!text || text === ev.type) {
          if (ev.type === 'Goal') {
            cType = 'goal';
            text = `GOAL! ${ev.playerName || 'Goal'} finds the back of the net for ${teamName || 'their team'}!${ev.assistPlayerName ? ` (Assist: ${ev.assistPlayerName})` : ''} (${fixture.homeScore ?? 0}–${fixture.awayScore ?? 0})`;
          } else if (ev.type === 'YellowCard') {
            cType = 'card';
            text = `Yellow card shown to ${ev.playerName || 'Player'} (${teamName || 'team'}) following a foul. Play briefly stops for the referee's booking.`;
          } else if (ev.type === 'RedCard') {
            cType = 'card';
            text = `RED CARD! ${ev.playerName || 'Player'} (${teamName || 'team'}) is sent off following a serious foul! Play halts as the player leaves the pitch.`;
          } else if (ev.type === 'Substitution') {
            cType = 'sub';
            text = `Tactical substitution for ${teamName || 'team'}: ${ev.assistPlayerName || 'Incoming player'} comes on to replace ${ev.playerName || 'outgoing player'}.`;
          } else {
            text = `${ev.type}${ev.playerName ? ` - ${ev.playerName}` : ''}`;
          }
        }

        synthCommentary.push({
          id: `comm_${ev.id || ev.eventUid || idx}`,
          minute: evMin,
          extraMinute: ev.extraMinute ?? 0,
          type: cType,
          text,
          teamId,
          teamName,
          teamLogoUrl,
        });
      });

      // Reverse-chronological order: most recent minute on top
      synthCommentary.sort((a, b) => {
        if (b.minute !== a.minute) return b.minute - a.minute;
        if (b.extraMinute !== a.extraMinute) return b.extraMinute - a.extraMinute;
        const revPriority = (item: any) => {
          const id = String(item.id || '');
          const t = String(item.text || '').toLowerCase();
          if (id.includes('fulltime') || t.includes('full-time') || t.includes('full time')) return 2;
          if (id.includes('halftime') || t.includes('half-time') || t.includes('half time')) return 2;
          if (id.includes('kickoff') || t.includes('kicks off') || id.includes('secondhalf') || t.includes('second half')) return 0;
          return 1;
        };
        return revPriority(b) - revPriority(a);
      });


      return synthCommentary;
    })(),
    lineups: fixture.lineups || [],
    statistics: (fixture.statistics || []).map((s: any) => ({
      ...s,
      expectedGoals: s.expectedGoals ?? null,
      xg: s.expectedGoals ?? null,
    })),
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

