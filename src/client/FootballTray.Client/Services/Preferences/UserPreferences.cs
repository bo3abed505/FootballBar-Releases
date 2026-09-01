using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace FootballTray.Client.Services.Preferences;

/// <summary>
/// Root user preferences entity governing following lists, notification subscriptions, and display options.
/// </summary>
public sealed record UserPreferences
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("preferencesVersion")]
    public int PreferencesVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("followedTeams")]
    public List<FollowedTeam> FollowedTeams { get; init; } = new();

    [JsonPropertyName("followedCompetitions")]
    public List<FollowedCompetition> FollowedCompetitions { get; init; } = new();

    [JsonPropertyName("notifications")]
    public NotificationPreferences Notifications { get; init; } = new();

    [JsonExtensionData]
    public Dictionary<string, object>? ExtensionData { get; init; }

    /// <summary>
    /// Returns pre-computed lookup set containing canonical IDs, numeric forms, and mock aliases for O(1) matching.
    /// </summary>
    public HashSet<string> GetFollowedTeamIdSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (FollowedTeams == null) return set;

        foreach (var team in FollowedTeams)
        {
            if (string.IsNullOrWhiteSpace(team.Id)) continue;
            var id = team.Id.Trim();
            set.Add(id);

            if (id.Equals(CanonicalEntityIds.Barcelona, StringComparison.OrdinalIgnoreCase) ||
                id.Equals(CanonicalEntityIds.BarcelonaMockAlias, StringComparison.OrdinalIgnoreCase) ||
                id.Equals(CanonicalEntityIds.BarcelonaNumericId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CanonicalEntityIds.Barcelona);
                set.Add(CanonicalEntityIds.BarcelonaMockAlias);
                set.Add(CanonicalEntityIds.BarcelonaNumericId.ToString());
            }
            else if (id.Equals(CanonicalEntityIds.AlAhly, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.AlAhlyMockAlias, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.AlAhlyNumericId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CanonicalEntityIds.AlAhly);
                set.Add(CanonicalEntityIds.AlAhlyMockAlias);
                set.Add(CanonicalEntityIds.AlAhlyNumericId.ToString());
            }
            else if (id.StartsWith("team_", StringComparison.OrdinalIgnoreCase) && int.TryParse(id[5..], out int num))
            {
                set.Add(num.ToString());
            }
            else if (int.TryParse(id, out int parsedNum))
            {
                set.Add($"team_{parsedNum}");
            }
        }

        return set;
    }

    /// <summary>
    /// Returns pre-computed lookup set containing canonical IDs, numeric forms, and mock aliases for O(1) matching.
    /// </summary>
    public HashSet<string> GetFollowedCompetitionIdSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (FollowedCompetitions == null) return set;

        foreach (var comp in FollowedCompetitions)
        {
            if (string.IsNullOrWhiteSpace(comp.Id)) continue;
            var id = comp.Id.Trim();
            set.Add(id);

            if (id.Equals(CanonicalEntityIds.PremierLeague, StringComparison.OrdinalIgnoreCase) ||
                id.Equals(CanonicalEntityIds.PremierLeagueMockAlias, StringComparison.OrdinalIgnoreCase) ||
                id.Equals(CanonicalEntityIds.PremierLeagueNumericId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CanonicalEntityIds.PremierLeague);
                set.Add(CanonicalEntityIds.PremierLeagueMockAlias);
                set.Add(CanonicalEntityIds.PremierLeagueNumericId.ToString());
            }
            else if (id.Equals(CanonicalEntityIds.LaLiga, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.LaLigaMockAlias, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.LaLigaNumericId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CanonicalEntityIds.LaLiga);
                set.Add(CanonicalEntityIds.LaLigaMockAlias);
                set.Add(CanonicalEntityIds.LaLigaNumericId.ToString());
            }
            else if (id.Equals(CanonicalEntityIds.EgyptianPremierLeague, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.EgyptianPremierLeagueMockAlias, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.EgyptianPremierLeagueNumericId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CanonicalEntityIds.EgyptianPremierLeague);
                set.Add(CanonicalEntityIds.EgyptianPremierLeagueMockAlias);
                set.Add(CanonicalEntityIds.EgyptianPremierLeagueNumericId.ToString());
            }
            else if (id.Equals(CanonicalEntityIds.ChampionsLeague, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.ChampionsLeagueMockAlias, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.ChampionsLeagueNumericId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CanonicalEntityIds.ChampionsLeague);
                set.Add(CanonicalEntityIds.ChampionsLeagueMockAlias);
                set.Add(CanonicalEntityIds.ChampionsLeagueNumericId.ToString());
            }
            else if (id.Equals(CanonicalEntityIds.AfricaCupOfNations, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.AfricaCupOfNationsMockAlias, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.AfricaCupOfNationsNumericId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CanonicalEntityIds.AfricaCupOfNations);
                set.Add(CanonicalEntityIds.AfricaCupOfNationsMockAlias);
                set.Add(CanonicalEntityIds.AfricaCupOfNationsNumericId.ToString());
            }
            else if (id.Equals(CanonicalEntityIds.WorldCup, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.WorldCupMockAlias, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.WorldCupNumericId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CanonicalEntityIds.WorldCup);
                set.Add(CanonicalEntityIds.WorldCupMockAlias);
                set.Add(CanonicalEntityIds.WorldCupNumericId.ToString());
            }
            else if (id.StartsWith("comp_", StringComparison.OrdinalIgnoreCase) && int.TryParse(id[5..], out int num))
            {
                set.Add(num.ToString());
            }
            else if (int.TryParse(id, out int parsedNum))
            {
                set.Add($"comp_{parsedNum}");
            }
        }

        return set;
    }
}
