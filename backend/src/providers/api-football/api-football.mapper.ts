import {
  ProviderCompetition,
  ProviderSeason,
  ProviderTeam,
  ProviderFixture,
  ProviderFixtureDetail,
  ProviderEvent,
  ProviderStanding,
  ProviderLineup,
  ProviderLineupPlayer,
  ProviderTeamStatistics,
  ProviderPlayerRating,
} from '../football-provider.js';
import {
  ApiFootballLeagueItem,
  ApiFootballFixtureItem,
  ApiFootballEventItem,
  ApiFootballStandingItem,
  ApiFootballLineupItem,
  ApiFootballLineupPlayerItem,
  ApiFootballFixtureStatisticItem,
  ApiFootballFixturePlayerItem,
} from './api-football.dto.js';

export class ApiFootballMapper {
  /**
   * Maps API-Football status short codes to canonical FootballTray MatchState.
   */
  static mapStatusToMatchState(shortStatus: string | null | undefined): string {
    const status = (shortStatus || '').toUpperCase().trim();

    switch (status) {
      case 'TBD':
      case 'NS':
        return 'PREMATCH';

      case '1H':
      case '2H':
      case 'ET':
      case 'P':
      case 'LIVE':
        return 'LIVE';

      case 'HT':
      case 'BT':
        return 'HALFTIME';

      case 'FT':
      case 'AET':
      case 'PEN':
        return 'FINISHED';

      case 'SUSP':
      case 'INT':
      case 'PST':
        return 'POSTPONED';

      case 'CANC':
      case 'ABD':
      case 'AWD':
      case 'WO':
        return 'CANCELLED';

      default:
        return 'PREMATCH';
    }
  }

  static mapLeagueToCompetition(item: ApiFootballLeagueItem): ProviderCompetition {
    return {
      id: `comp_${item.league.id}`,
      name: item.league.name,
      code: item.country.code || item.league.name.substring(0, 3).toUpperCase(),
      type: item.league.type === 'Cup' ? 'cup' : 'league',
      country: item.country.name,
      logoUrl: item.league.logo,
      provider: 'api_football',
      providerId: String(item.league.id),
    };
  }

  static mapLeagueSeasons(item: ApiFootballLeagueItem): ProviderSeason[] {
    const compId = `comp_${item.league.id}`;
    return item.seasons.map((s) => ({
      id: `season_${item.league.id}_${s.year}`,
      competitionId: compId,
      year: s.year,
      displayYear: `${s.year}/${s.year + 1}`,
      startDate: s.start,
      endDate: s.end,
      isCurrent: s.current,
    }));
  }

  static mapTeam(
    teamObj: { id: number; name: string; logo: string },
    countryHint?: string
  ): ProviderTeam {
    const isGenericRegion = countryHint && ['World', 'Europe', 'Africa', 'South-America', 'Asia', 'North-America'].includes(countryHint);
    const country = countryHint && !isGenericRegion ? countryHint : undefined;

    return {
      id: `team_${teamObj.id}`,
      name: teamObj.name,
      shortName: teamObj.name.length <= 4 ? teamObj.name : teamObj.name.substring(0, 3).toUpperCase(),
      code: teamObj.name.substring(0, 3).toUpperCase(),
      country,
      logoUrl: teamObj.logo,
      provider: 'api_football',
      providerId: String(teamObj.id),
    };
  }

  static mapFixture(item: ApiFootballFixtureItem): ProviderFixture {
    const leagueCountry = item.league.country;
    const isGenericRegion = leagueCountry && ['World', 'Europe', 'Africa', 'South-America', 'Asia', 'North-America'].includes(leagueCountry);
    const homeCountry = (!isGenericRegion && leagueCountry) ? leagueCountry : item.teams.home.name;
    const awayCountry = (!isGenericRegion && leagueCountry) ? leagueCountry : item.teams.away.name;

    const homeTeam = this.mapTeam(item.teams.home, homeCountry);
    const awayTeam = this.mapTeam(item.teams.away, awayCountry);
    const comp: ProviderCompetition = {
      id: `comp_${item.league.id}`,
      name: item.league.name,
      code: item.league.name.substring(0, 3).toUpperCase(),
      country: item.league.country,
      logoUrl: item.league.logo,
      provider: 'api_football',
      providerId: String(item.league.id),
    };
    const season: ProviderSeason = {
      id: `season_${item.league.id}_${item.league.season}`,
      competitionId: comp.id,
      year: item.league.season,
      displayYear: `${item.league.season}/${item.league.season + 1}`,
      startDate: item.fixture.date,
      endDate: item.fixture.date,
      isCurrent: true,
    };

    const fixtureId = `fix_${item.fixture.id}`;
    const events = item.events && item.events.length > 0 ? this.mapEvents(fixtureId, item.events) : undefined;

    return {
      id: fixtureId,
      competitionId: comp.id,
      seasonId: season.id,
      homeTeamId: homeTeam.id,
      awayTeamId: awayTeam.id,
      competition: comp,
      season,
      homeTeam,
      awayTeam,
      round: item.league.round,
      kickoffTime: item.fixture.date,
      state: this.mapStatusToMatchState(item.fixture.status.short),
      elapsedMinute: item.fixture.status.elapsed ?? 0,
      extraMinute: item.fixture.status.extra ?? 0,
      score: {
        home: item.goals?.home ?? 0,
        away: item.goals?.away ?? 0,
        homeHt: item.score?.halftime?.home ?? undefined,
        awayHt: item.score?.halftime?.away ?? undefined,
        homeEt: item.score?.extratime?.home ?? undefined,
        awayEt: item.score?.extratime?.away ?? undefined,
        homePen: item.score?.penalty?.home ?? undefined,
        awayPen: item.score?.penalty?.away ?? undefined,
      },
      venue: item.fixture.venue?.name || undefined,
      referee: item.fixture.referee || undefined,
      events,
      provider: 'api_football',
      providerId: String(item.fixture.id),
    };
  }

  static mapFixtureDetail(item: ApiFootballFixtureItem): ProviderFixtureDetail {
    const base = this.mapFixture(item);
    const events = item.events ? this.mapEvents(base.id, item.events) : (base.events ?? []);
    const lineups = item.lineups && item.lineups.length > 0 ? this.mapLineups(item.lineups) : undefined;
    const statistics = item.statistics && item.statistics.length > 0 ? this.mapStatistics(item.statistics) : undefined;
    const playerRatings = item.players && item.players.length > 0 ? this.mapPlayerRatings(item.players) : undefined;

    return {
      ...base,
      events,
      lineups,
      statistics,
      playerRatings,
    };
  }

  static mapLineupPlayer(p: ApiFootballLineupPlayerItem, isStarter: boolean): ProviderLineupPlayer {
    return {
      id: `ply_${p.player.id}`,
      name: p.player.name,
      number: p.player.number ?? undefined,
      position: p.player.pos ?? undefined,
      grid: p.player.grid ?? undefined,
      isStarter,
    };
  }

  static mapLineups(lineups: ApiFootballLineupItem[]): ProviderLineup[] {
    return lineups.map((l) => ({
      teamId: `team_${l.team.id}`,
      formation: l.formation ?? undefined,
      coachName: l.coach?.name ?? undefined,
      coachId: l.coach?.id ? `coach_${l.coach.id}` : undefined,
      startXi: (l.startXI || []).map((p) => this.mapLineupPlayer(p, true)),
      substitutes: (l.substitutes || []).map((p) => this.mapLineupPlayer(p, false)),
    }));
  }

  static mapStatistics(statsList: ApiFootballFixtureStatisticItem[]): ProviderTeamStatistics[] {
    return statsList.map((item) => {
      const statsMap = new Map<string, any>();
      for (const s of item.statistics || []) {
        statsMap.set((s.type || '').trim().toLowerCase(), s.value);
      }

      const getInt = (type: string): number | undefined => {
        const val = statsMap.get(type.toLowerCase());
        if (val === null || val === undefined) return undefined;
        if (typeof val === 'number') return Math.round(val);
        const parsed = parseInt(String(val).replace(/[^0-9-]/g, ''), 10);
        return isNaN(parsed) ? undefined : parsed;
      };

      return {
        teamId: `team_${item.team.id}`,
        shotsOnGoal: getInt('shots on goal'),
        shotsOffGoal: getInt('shots off goal'),
        totalShots: getInt('total shots'),
        possessionPercent: getInt('ball possession'),
        fouls: getInt('fouls'),
        cornerKicks: getInt('corner kicks'),
        offsides: getInt('offsides'),
        yellowCards: getInt('yellow cards'),
        redCards: getInt('red cards'),
        goalkeeperSaves: getInt('goalkeeper saves'),
        totalPasses: getInt('total passes'),
        passesAccurate: getInt('passes accurate'),
      };
    });
  }

  static mapPlayerRatings(playersList: ApiFootballFixturePlayerItem[]): ProviderPlayerRating[] {
    const ratings: ProviderPlayerRating[] = [];
    for (const teamItem of playersList || []) {
      const teamId = `team_${teamItem.team.id}`;
      for (const p of teamItem.players || []) {
        const rawRating = p.statistics?.[0]?.games?.rating;
        let rating: number | string | null = null;
        if (typeof rawRating === 'number') {
          rating = rawRating;
        } else if (typeof rawRating === 'string') {
          const parsed = parseFloat(rawRating);
          rating = isNaN(parsed) ? rawRating : parsed;
        }
        ratings.push({
          playerId: `ply_${p.player.id}`,
          teamId,
          name: p.player.name,
          rating,
        });
      }
    }
    return ratings;
  }

  static mapEvents(fixtureId: string, events: ApiFootballEventItem[]): ProviderEvent[] {
    return events.map((e, idx) => {
      let eventType = 'Goal';
      const typeLower = (e.type || '').toLowerCase();
      const detailLower = (e.detail || '').toLowerCase();

      if (typeLower === 'card') {
        eventType = detailLower.includes('yellow') ? 'YellowCard' : 'RedCard';
      } else if (typeLower === 'subst') {
        eventType = 'Substitution';
      } else if (typeLower === 'var') {
        eventType = 'VarDecision';
      } else if (typeLower === 'goal') {
        eventType = 'Goal';
      }

      const playerId = e.player?.id ? `ply_${e.player.id}` : undefined;
      const assistId = e.assist?.id ? `ply_${e.assist.id}` : undefined;
      const teamId = `team_${e.team?.id ?? 0}`;
      const minute = e.time?.elapsed ?? 0;
      const extraMinute = e.time?.extra ?? 0;

      // Stable discriminators: player, assist, detail, comments
      const playerSlug = (e.player?.name || 'noplayer').replace(/[^a-zA-Z0-9]/g, '_').toLowerCase();
      const assistSlug = (e.assist?.name || 'noassist').replace(/[^a-zA-Z0-9]/g, '_').toLowerCase();
      const detailSlug = (e.detail || 'nodetail').replace(/[^a-zA-Z0-9]/g, '_').toLowerCase();
      const commentSlug = (e.comments || '').replace(/[^a-zA-Z0-9]/g, '_').toLowerCase();

      const discriminator = `${playerId || playerSlug}_${assistId || assistSlug}_${detailSlug}${commentSlug ? `_${commentSlug}` : ''}`;
      const eventUid = `evt_${fixtureId}_${minute}_${extraMinute}_${eventType}_${teamId}_${discriminator}`;

      return {
        id: `evt_${fixtureId}_${idx + 1}`,
        eventUid,
        fixtureId,
        teamId,
        type: eventType,
        minute,
        extraMinute,
        playerName: e.player?.name || undefined,
        playerId,
        assistPlayerName: e.assist?.name || undefined,
        assistPlayerId: assistId,
        description: e.detail ? `[${e.type}] ${e.detail}` : undefined,
      };
    });
  }

  static mapStandings(item: ApiFootballStandingItem): ProviderStanding[] {
    const compId = `comp_${item.league.id}`;
    const seasonId = `season_${item.league.id}_${item.league.season}`;

    const results: ProviderStanding[] = [];

    for (const group of item.league.standings) {
      const rows = group.map((row) => ({
        position: row.rank,
        teamId: `team_${row.team.id}`,
        played: row.all.played,
        won: row.all.win,
        drawn: row.all.draw,
        lost: row.all.lose,
        goalsFor: row.all.goals.for,
        goalsAgainst: row.all.goals.against,
        points: row.points,
        form: row.form || undefined,
        description: row.description || undefined,
      }));

      results.push({
        competitionId: compId,
        seasonId,
        rows,
      });
    }

    return results;
  }
}
