using System;

namespace FootballTray.Client.Services.Preferences;

/// <summary>
/// Canonical provider and domain entity identifiers established by API-Football mapper and TARGET_COMPETITION_IDS.
/// Serves as the single source of truth for canonical followed teams and competitions on the client.
/// </summary>
public static class CanonicalEntityIds
{
    // ==========================================
    // Teams (API-Football team IDs)
    // ==========================================

    /// <summary>FC Barcelona (API-Football ID: 529)</summary>
    public const string Barcelona = "team_529";
    public const int BarcelonaNumericId = 529;
    public const string BarcelonaMockAlias = "team_barcelona";

    /// <summary>Al Ahly SC (API-Football ID: 1029)</summary>
    public const string AlAhly = "team_1029";
    public const int AlAhlyNumericId = 1029;
    public const string AlAhlyMockAlias = "team_alahly";

    // ==========================================
    // Competitions (API-Football league IDs matching TARGET_COMPETITION_IDS)
    // ==========================================

    /// <summary>Premier League (API-Football ID: 39)</summary>
    public const string PremierLeague = "comp_39";
    public const int PremierLeagueNumericId = 39;
    public const string PremierLeagueMockAlias = "comp_pl";

    /// <summary>La Liga (API-Football ID: 140)</summary>
    public const string LaLiga = "comp_140";
    public const int LaLigaNumericId = 140;
    public const string LaLigaMockAlias = "comp_laliga";

    /// <summary>Egyptian Premier League (API-Football ID: 233)</summary>
    public const string EgyptianPremierLeague = "comp_233";
    public const int EgyptianPremierLeagueNumericId = 233;
    public const string EgyptianPremierLeagueMockAlias = "comp_egypt_pl";

    /// <summary>UEFA Champions League (API-Football ID: 2)</summary>
    public const string ChampionsLeague = "comp_2";
    public const int ChampionsLeagueNumericId = 2;
    public const string ChampionsLeagueMockAlias = "comp_ucl";

    /// <summary>CAF Africa Cup of Nations (API-Football ID: 6)</summary>
    public const string AfricaCupOfNations = "comp_6";
    public const int AfricaCupOfNationsNumericId = 6;
    public const string AfricaCupOfNationsMockAlias = "comp_afcon";

    /// <summary>FIFA World Cup (API-Football ID: 1)</summary>
    public const string WorldCup = "comp_1";
    public const int WorldCupNumericId = 1;
    public const string WorldCupMockAlias = "comp_wc";
}
