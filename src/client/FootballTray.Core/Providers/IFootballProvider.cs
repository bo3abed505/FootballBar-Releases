using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FootballTray.Core.Models;

namespace FootballTray.Core.Providers;

/// <summary>
/// Provider-independent contract for football data sources (e.g. Mock, Ingestion Engine, Backend API).
/// </summary>
public interface IFootballProvider
{
    string ProviderName { get; }

    Task<IReadOnlyList<Competition>> GetCompetitionsAsync();
    Task<IReadOnlyList<Fixture>> GetFixturesAsync(string? competitionId = null, DateTime? date = null);
    Task<Fixture?> GetFixtureByIdAsync(string fixtureId);
    Task<IReadOnlyList<MatchEvent>> GetMatchEventsAsync(string fixtureId);
    Task<Standing?> GetStandingsAsync(string competitionId, string seasonId);

    event Action<Fixture>? FixtureUpdated;
    event Action<MatchEvent>? MatchEventOccurred;
}
