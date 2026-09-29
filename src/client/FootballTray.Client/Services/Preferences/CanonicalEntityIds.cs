using System;
using System.Collections.Generic;

namespace FootballTray.Client.Services.Preferences;

/// <summary>
/// Canonical provider and domain entity identifiers established by API-Football mapper and TARGET_COMPETITION_IDS.
/// Serves as the single source of truth for canonical followed teams and competitions on the client.
/// </summary>
public static class CanonicalEntityIds
{
    // ==========================================
    // Teams (Canonical ft_team_ IDs from Gate C backfill)
    // ==========================================

    /// <summary>FC Barcelona canonical ID</summary>
    public const string Barcelona = "ft_team_01M33DKNCTXK0YKJNJDDC15GS8";
    public const string LegacyBarcelona = "team_529";
    public const int BarcelonaNumericId = 529;
    public const string BarcelonaMockAlias = "team_barcelona";

    /// <summary>Al Ahly SC canonical ID</summary>
    public const string AlAhly = "ft_team_01M33DKNCP7HVGD8FVFYW5YNAK";
    public const string LegacyAlAhly = "team_1029";
    public const int AlAhlyNumericId = 1029;
    public const string AlAhlyMockAlias = "team_alahly";

    /// <summary>Egypt National Team canonical ID</summary>
    public const string Egypt = "ft_team_01M3N490H4XSMWQA84ANXFYHB6";
    public const string LegacyEgypt = "team_32";
    public const int EgyptNumericId = 32;
    public const string EgyptMockAlias = "team_egypt";
    public const string LegacyMockEgyptCanonical = "ft_team_E09F776574F182A24F2540B66D";

    // ==========================================
    // Competitions (Canonical ft_comp_ IDs from Gate C backfill)
    // ==========================================

    /// <summary>Premier League canonical ID</summary>
    public const string PremierLeague = "ft_comp_01M33DKNCCNEJAG93CDAGJSR4A";
    public const string LegacyPremierLeague = "comp_39";
    public const int PremierLeagueNumericId = 39;
    public const string PremierLeagueMockAlias = "comp_pl";

    /// <summary>La Liga canonical ID</summary>
    public const string LaLiga = "ft_comp_01M33DKNC8CKGMJ9Z7898AH8HA";
    public const string LegacyLaLiga = "comp_140";
    public const int LaLigaNumericId = 140;
    public const string LaLigaMockAlias = "comp_laliga";

    /// <summary>Egyptian Premier League canonical ID</summary>
    public const string EgyptianPremierLeague = "ft_comp_01M33DKNCABRRYR4K6D9DKTP40";
    public const string LegacyEgyptianPremierLeague = "comp_233";
    public const int EgyptianPremierLeagueNumericId = 233;
    public const string EgyptianPremierLeagueMockAlias = "comp_egypt_pl";

    /// <summary>UEFA Champions League canonical ID</summary>
    public const string ChampionsLeague = "ft_comp_01M33DKNC97EMTT3FNZR1YTC9E";
    public const string LegacyChampionsLeague = "comp_2";
    public const int ChampionsLeagueNumericId = 2;
    public const string ChampionsLeagueMockAlias = "comp_ucl";

    /// <summary>CAF Africa Cup of Nations canonical ID</summary>
    public const string AfricaCupOfNations = "ft_comp_01M33DKNCDV0T7DEEHHZ20DMN7";
    public const string LegacyAfricaCupOfNations = "comp_6";
    public const int AfricaCupOfNationsNumericId = 6;
    public const string AfricaCupOfNationsMockAlias = "comp_afcon";

    /// <summary>FIFA World Cup canonical ID</summary>
    public const string WorldCup = "ft_comp_01M33DKNBZRHDX2EN9YQJPSV5Q";
    public const string LegacyWorldCup = "comp_1";
    public const int WorldCupNumericId = 1;
    public const string WorldCupMockAlias = "comp_wc";

    // ==========================================
    // Legacy to Canonical Migration Dictionary
    // ==========================================

    public static readonly IReadOnlyDictionary<string, string> LegacyToCanonicalMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // Teams
        { "team_529", Barcelona },
        { "529", Barcelona },
        { "team_barcelona", Barcelona },
        { "ft_team_18C2D60CA786219E2526652BC8", Barcelona },
        { "team_1029", AlAhly },
        { "1029", AlAhly },
        { "team_1577", AlAhly },
        { "1577", AlAhly },
        { "team_alahly", AlAhly },
        { "ft_team_6C60757CB48CCE0E897AF02B71", AlAhly },
        { "team_32", Egypt },
        { "32", Egypt },
        { "team_egypt", Egypt },
        { "ft_team_E09F776574F182A24F2540B66D", Egypt },

        // Competitions
        { "comp_39", PremierLeague },
        { "39", PremierLeague },
        { "comp_pl", PremierLeague },
        { "comp_140", LaLiga },
        { "140", LaLiga },
        { "comp_laliga", LaLiga },
        { "comp_233", EgyptianPremierLeague },
        { "233", EgyptianPremierLeague },
        { "comp_egypt_pl", EgyptianPremierLeague },
        { "comp_2", ChampionsLeague },
        { "2", ChampionsLeague },
        { "comp_ucl", ChampionsLeague },
        { "comp_6", AfricaCupOfNations },
        { "6", AfricaCupOfNations },
        { "comp_afcon", AfricaCupOfNations },
        { "comp_1", WorldCup },
        { "1", WorldCup },
        { "comp_wc", WorldCup },
    };

    /// <summary>
    /// Translates a legacy ID (numeric, prefixed, or mock alias) to its canonical ft_ form.
    /// Handles provisional prefixed IDs and already-canonical IDs.
    /// </summary>
    public static bool TryTranslateLegacyId(string? legacyId, out string canonicalId)
    {
        canonicalId = string.Empty;
        if (string.IsNullOrWhiteSpace(legacyId)) return false;

        var clean = legacyId.Trim();
        if (clean.StartsWith("provisional:", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["provisional:".Length..].Trim();
        }

        if (LegacyToCanonicalMap.TryGetValue(clean, out var found))
        {
            canonicalId = found;
            return true;
        }

        if (clean.StartsWith("ft_", StringComparison.OrdinalIgnoreCase))
        {
            canonicalId = clean;
            return true;
        }

        return false;
    }
}
