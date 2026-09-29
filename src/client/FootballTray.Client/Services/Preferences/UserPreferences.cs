using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FootballTray.Client.Services.Preferences;

/// <summary>
/// Root user preferences entity governing following lists, notification subscriptions, and display options.
/// </summary>
public sealed record UserPreferences
{
    public const int CurrentSchemaVersion = 2;

    [JsonPropertyName("preferencesVersion")]
    public int PreferencesVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("followedTeams")]
    public List<FollowedTeam> FollowedTeams { get; init; } = new();

    [JsonPropertyName("followedCompetitions")]
    public List<FollowedCompetition> FollowedCompetitions { get; init; } = new();

    [JsonPropertyName("notifications")]
    public NotificationPreferences Notifications { get; init; } = new();

    [JsonPropertyName("soundMode")]
    public NotificationSoundMode SoundMode { get; init; } = NotificationSoundMode.Custom;

    [JsonPropertyName("isAudioEnabled")]
    public bool IsAudioEnabled
    {
        get => SoundMode != NotificationSoundMode.Off;
        init => SoundMode = value ? (SoundMode == NotificationSoundMode.Off ? NotificationSoundMode.Custom : SoundMode) : NotificationSoundMode.Off;
    }

    [JsonPropertyName("audioVolume")]
    public double AudioVolume { get; init; } = 0.2;

    [JsonPropertyName("isTickerVisible")]
    public bool IsTickerVisible { get; init; } = true;

    [JsonPropertyName("tickerOffsetX")]
    public int TickerOffsetX { get; init; } = 0;

    [JsonPropertyName("targetScreenDeviceName")]
    public string? TargetScreenDeviceName { get; init; } = null;

    [JsonPropertyName("enableTickerCarousel")]
    public bool EnableTickerCarousel { get; init; } = true;

    [JsonPropertyName("use24HourFormat")]
    public bool Use24HourFormat { get; init; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// Returns pre-computed lookup set containing canonical IDs, numeric forms, mock aliases, and provisional IDs for O(1) matching.
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

            if (!string.IsNullOrWhiteSpace(team.Name))
            {
                set.Add(team.Name.Trim());
            }

            if (id.StartsWith("provisional:", StringComparison.OrdinalIgnoreCase))
            {
                var raw = id["provisional:".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    set.Add(raw);
                }
            }

            if (CanonicalEntityIds.TryTranslateLegacyId(id, out var canonicalId))
            {
                set.Add(canonicalId);
            }

            if (id.Equals(CanonicalEntityIds.Barcelona, StringComparison.OrdinalIgnoreCase) ||
                id.Equals(CanonicalEntityIds.LegacyBarcelona, StringComparison.OrdinalIgnoreCase) ||
                id.Equals(CanonicalEntityIds.BarcelonaMockAlias, StringComparison.OrdinalIgnoreCase) ||
                id.Equals(CanonicalEntityIds.BarcelonaNumericId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CanonicalEntityIds.Barcelona);
                set.Add(CanonicalEntityIds.LegacyBarcelona);
                set.Add(CanonicalEntityIds.BarcelonaMockAlias);
                set.Add(CanonicalEntityIds.BarcelonaNumericId.ToString());
            }
            else if (id.Equals(CanonicalEntityIds.AlAhly, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.LegacyAlAhly, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.AlAhlyMockAlias, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.AlAhlyNumericId.ToString(), StringComparison.OrdinalIgnoreCase) ||
                     id.Equals("1577", StringComparison.OrdinalIgnoreCase) ||
                     id.Equals("team_1577", StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CanonicalEntityIds.AlAhly);
                set.Add(CanonicalEntityIds.LegacyAlAhly);
                set.Add(CanonicalEntityIds.AlAhlyMockAlias);
                set.Add(CanonicalEntityIds.AlAhlyNumericId.ToString());
                set.Add("1577");
                set.Add("team_1577");
            }
            else if (id.Equals(CanonicalEntityIds.Egypt, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.LegacyEgypt, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.EgyptMockAlias, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.EgyptNumericId.ToString(), StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.LegacyMockEgyptCanonical, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals("Egypt", StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CanonicalEntityIds.Egypt);
                set.Add(CanonicalEntityIds.LegacyEgypt);
                set.Add(CanonicalEntityIds.EgyptMockAlias);
                set.Add(CanonicalEntityIds.EgyptNumericId.ToString());
                set.Add(CanonicalEntityIds.LegacyMockEgyptCanonical);
                set.Add("Egypt");
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
    /// Returns pre-computed lookup set containing canonical IDs, numeric forms, mock aliases, and provisional IDs for O(1) matching.
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

            if (id.StartsWith("provisional:", StringComparison.OrdinalIgnoreCase))
            {
                var raw = id["provisional:".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    set.Add(raw);
                }
            }

            if (CanonicalEntityIds.TryTranslateLegacyId(id, out var canonicalId))
            {
                set.Add(canonicalId);
            }

            if (id.Equals(CanonicalEntityIds.PremierLeague, StringComparison.OrdinalIgnoreCase) ||
                id.Equals(CanonicalEntityIds.LegacyPremierLeague, StringComparison.OrdinalIgnoreCase) ||
                id.Equals(CanonicalEntityIds.PremierLeagueMockAlias, StringComparison.OrdinalIgnoreCase) ||
                id.Equals(CanonicalEntityIds.PremierLeagueNumericId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CanonicalEntityIds.PremierLeague);
                set.Add(CanonicalEntityIds.LegacyPremierLeague);
                set.Add(CanonicalEntityIds.PremierLeagueMockAlias);
                set.Add(CanonicalEntityIds.PremierLeagueNumericId.ToString());
            }
            else if (id.Equals(CanonicalEntityIds.LaLiga, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.LegacyLaLiga, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.LaLigaMockAlias, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.LaLigaNumericId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CanonicalEntityIds.LaLiga);
                set.Add(CanonicalEntityIds.LegacyLaLiga);
                set.Add(CanonicalEntityIds.LaLigaMockAlias);
                set.Add(CanonicalEntityIds.LaLigaNumericId.ToString());
            }
            else if (id.Equals(CanonicalEntityIds.EgyptianPremierLeague, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.LegacyEgyptianPremierLeague, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.EgyptianPremierLeagueMockAlias, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.EgyptianPremierLeagueNumericId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CanonicalEntityIds.EgyptianPremierLeague);
                set.Add(CanonicalEntityIds.LegacyEgyptianPremierLeague);
                set.Add(CanonicalEntityIds.EgyptianPremierLeagueMockAlias);
                set.Add(CanonicalEntityIds.EgyptianPremierLeagueNumericId.ToString());
            }
            else if (id.Equals(CanonicalEntityIds.ChampionsLeague, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.LegacyChampionsLeague, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.ChampionsLeagueMockAlias, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.ChampionsLeagueNumericId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CanonicalEntityIds.ChampionsLeague);
                set.Add(CanonicalEntityIds.LegacyChampionsLeague);
                set.Add(CanonicalEntityIds.ChampionsLeagueMockAlias);
                set.Add(CanonicalEntityIds.ChampionsLeagueNumericId.ToString());
            }
            else if (id.Equals(CanonicalEntityIds.AfricaCupOfNations, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.LegacyAfricaCupOfNations, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.AfricaCupOfNationsMockAlias, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.AfricaCupOfNationsNumericId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CanonicalEntityIds.AfricaCupOfNations);
                set.Add(CanonicalEntityIds.LegacyAfricaCupOfNations);
                set.Add(CanonicalEntityIds.AfricaCupOfNationsMockAlias);
                set.Add(CanonicalEntityIds.AfricaCupOfNationsNumericId.ToString());
            }
            else if (id.Equals(CanonicalEntityIds.WorldCup, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.LegacyWorldCup, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.WorldCupMockAlias, StringComparison.OrdinalIgnoreCase) ||
                     id.Equals(CanonicalEntityIds.WorldCupNumericId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CanonicalEntityIds.WorldCup);
                set.Add(CanonicalEntityIds.LegacyWorldCup);
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
