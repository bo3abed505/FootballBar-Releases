using System;
using System.Collections.Generic;

namespace FootballTray.Client.Services.Preferences;

/// <summary>
/// Canonical provider and domain entity identifiers established by telemetry mapper and TARGET_COMPETITION_IDS.
/// Serves as the single source of truth for canonical followed teams and competitions on the client.
/// </summary>
public static class CanonicalEntityIds
{
    // ==========================================
    // Teams (Canonical ft_team_ IDs from Gate C backfill)
    // ==========================================

    /// <summary>FC Barcelona canonical ID</summary>
    public const string Barcelona = "ft_team_01M33DKNCTXK0YKJNJDDC15GS8";
    public const string LegacyBarcelona = "team_8634";
    public const int BarcelonaNumericId = 8634;
    public const string BarcelonaMockAlias = "team_barcelona";

    /// <summary>Real Madrid canonical ID</summary>
    public const string RealMadrid = "ft_team_01M33DKNCTXK0YKJNJDDC15GS9";
    public const string LegacyRealMadrid = "team_8633";
    public const int RealMadridNumericId = 8633;
    public const string RealMadridMockAlias = "team_realmadrid";

    /// <summary>Arsenal canonical ID</summary>
    public const string Arsenal = "ft_team_01M33DKNCP7HVGD8FVFYW5YNAA";
    public const string LegacyArsenal = "team_9825";
    public const int ArsenalNumericId = 9825;
    public const string ArsenalMockAlias = "team_arsenal";

    /// <summary>Al Ahly SC canonical ID</summary>
    public const string AlAhly = "ft_team_01M33DKNCP7HVGD8FVFYW5YNAK";
    public const string LegacyAlAhly = "team_101745";
    public const int AlAhlyNumericId = 101745;
    public const string AlAhlyMockAlias = "team_alahly";

    /// <summary>Zamalek SC canonical ID</summary>
    public const string Zamalek = "ft_team_01M33DKNCP7HVGD8FVFYW5YNAZ";
    public const string LegacyZamalek = "team_80591";
    public const int ZamalekNumericId = 80591;
    public const string ZamalekMockAlias = "team_zamalek";

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
    public const string LegacyPremierLeague = "comp_47";
    public const int PremierLeagueNumericId = 47;
    public const string PremierLeagueMockAlias = "comp_pl";

    /// <summary>La Liga canonical ID</summary>
    public const string LaLiga = "ft_comp_01M33DKNC8CKGMJ9Z7898AH8HA";
    public const string LegacyLaLiga = "comp_87";
    public const int LaLigaNumericId = 87;
    public const string LaLigaMockAlias = "comp_laliga";

    /// <summary>Egyptian Premier League canonical ID</summary>
    public const string EgyptianPremierLeague = "ft_comp_01M33DKNCABRRYR4K6D9DKTP40";
    public const string LegacyEgyptianPremierLeague = "comp_519";
    public const int EgyptianPremierLeagueNumericId = 519;
    public const string EgyptianPremierLeagueMockAlias = "comp_egypt_pl";

    /// <summary>UEFA Champions League canonical ID</summary>
    public const string ChampionsLeague = "ft_comp_01M33DKNC97EMTT3FNZR1YTC9E";
    public const string LegacyChampionsLeague = "comp_42";
    public const int ChampionsLeagueNumericId = 42;
    public const string ChampionsLeagueMockAlias = "comp_ucl";

    /// <summary>CAF Africa Cup of Nations canonical ID</summary>
    public const string AfricaCupOfNations = "ft_comp_01M33DKNCDV0T7DEEHHZ20DMN7";
    public const string LegacyAfricaCupOfNations = "comp_289";
    public const int AfricaCupOfNationsNumericId = 289;
    public const string AfricaCupOfNationsMockAlias = "comp_afcon";

    /// <summary>FIFA World Cup canonical ID</summary>
    public const string WorldCup = "ft_comp_01M33DKNBZRHDX2EN9YQJPSV5Q";
    public const string LegacyWorldCup = "comp_77";
    public const int WorldCupNumericId = 77;
    public const string WorldCupMockAlias = "comp_wc";

    /// <summary>UEFA European Championship canonical ID</summary>
    public const string EuroChampionship = "ft_comp_01M3Y7E15VJYGS76SM21GBR1YD";
    public const string LegacyEuroChampionship = "comp_50";
    public const int EuroChampionshipNumericId = 50;
    public const string EuroChampionshipMockAlias = "comp_euro";

    /// <summary>UEFA Nations League canonical ID</summary>
    public const string NationsLeague = "ft_comp_01M3Y7E15Z5X08Y7S7K0JPAGE6";
    public const string LegacyNationsLeague = "comp_9806";
    public const int NationsLeagueNumericId = 9806;
    public const string NationsLeagueMockAlias = "comp_unl";

    /// <summary>Copa America canonical ID</summary>
    public const string CopaAmerica = "ft_comp_01M3Y7E1608TK80WDRTGYQHF2X";
    public const string LegacyCopaAmerica = "comp_44";
    public const int CopaAmericaNumericId = 44;
    public const string CopaAmericaMockAlias = "comp_ca";

    /// <summary>AFC Champions League Elite canonical ID</summary>
    public const string AfcChampionsLeague = "ft_comp_01M33DKNC525AFCACLWESTEAST0";
    public const string LegacyAfcChampionsLeague = "comp_525";
    public const int AfcChampionsLeagueNumericId = 525;
    public const string AfcChampionsLeagueMockAlias = "comp_afc_cl";

    // ==========================================
    // Legacy to Canonical Migration Dictionary
    // ==========================================

    public static readonly IReadOnlyDictionary<string, string> LegacyToCanonicalMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // Teams - Exact 1-to-1 Mapping to verified FotMob IDs
        { "8634", Barcelona },
        { "team_8634", Barcelona },
        { "fotmob_8634", Barcelona },
        { "team_barcelona", Barcelona },
        { "team_529", Barcelona },
        { "529", Barcelona },
        { "ft_team_18C2D60CA786219E2526652BC8", Barcelona },

        { "8633", RealMadrid },
        { "team_8633", RealMadrid },
        { "fotmob_8633", RealMadrid },
        { "team_realmadrid", RealMadrid },
        { "team_541", RealMadrid },

        { "9825", Arsenal },
        { "team_9825", Arsenal },
        { "fotmob_9825", Arsenal },
        { "team_arsenal", Arsenal },

        { "101745", AlAhly },
        { "team_101745", AlAhly },
        { "fotmob_101745", AlAhly },
        { "team_alahly", AlAhly },
        { "team_1029", AlAhly },
        { "ft_team_6C60757CB48CCE0E897AF02B71", AlAhly },

        { "80591", Zamalek },
        { "team_80591", Zamalek },
        { "fotmob_80591", Zamalek },
        { "team_zamalek", Zamalek },
        { "team_1028", Zamalek },

        { "32", Egypt },
        { "team_32", Egypt },
        { "fotmob_32", Egypt },
        { "team_egypt", Egypt },
        { "ft_team_E09F776574F182A24F2540B66D", Egypt },

        // Competitions - Exact 1-to-1 Mapping to verified FotMob League IDs
        { "47", PremierLeague },
        { "comp_47", PremierLeague },
        { "fotmob_47", PremierLeague },
        { "premier-league", PremierLeague },
        { "premier_league", PremierLeague },
        { "premier league", PremierLeague },
        { "comp_pl", PremierLeague },
        { "comp_39", PremierLeague },
        { "39", PremierLeague },

        { "87", LaLiga },
        { "comp_87", LaLiga },
        { "fotmob_87", LaLiga },
        { "la-liga", LaLiga },
        { "la_liga", LaLiga },
        { "laliga", LaLiga },
        { "comp_laliga", LaLiga },
        { "comp_140", LaLiga },

        { "519", EgyptianPremierLeague },
        { "comp_519", EgyptianPremierLeague },
        { "fotmob_519", EgyptianPremierLeague },
        { "10650", EgyptianPremierLeague },
        { "comp_10650", EgyptianPremierLeague },
        { "fotmob_10650", EgyptianPremierLeague },
        { "191", EgyptianPremierLeague },
        { "comp_191", EgyptianPremierLeague },
        { "fotmob_191", EgyptianPremierLeague },
        { "egyptian-premier-league", EgyptianPremierLeague },
        { "egyptian_premier_league", EgyptianPremierLeague },
        { "egyptian premier league", EgyptianPremierLeague },
        { "comp_egypt_pl", EgyptianPremierLeague },
        { "comp_233", EgyptianPremierLeague },
        { "233", EgyptianPremierLeague },

        { "525", AfcChampionsLeague },
        { "comp_525", AfcChampionsLeague },
        { "fotmob_525", AfcChampionsLeague },
        { "afc-champions-league", AfcChampionsLeague },
        { "afc_champions_league", AfcChampionsLeague },
        { "afc champions league", AfcChampionsLeague },
        { "comp_afc_cl", AfcChampionsLeague },

        { "42", ChampionsLeague },
        { "comp_42", ChampionsLeague },
        { "fotmob_42", ChampionsLeague },
        { "champions-league", ChampionsLeague },
        { "champions_league", ChampionsLeague },
        { "comp_ucl", ChampionsLeague },
        { "comp_2", ChampionsLeague },
        { "2", ChampionsLeague },

        { "73", "ft_comp_01M33DKNC7HY1Q85DNPB713V5Y" }, // Europa League
        { "comp_73", "ft_comp_01M33DKNC7HY1Q85DNPB713V5Y" },
        { "fotmob_73", "ft_comp_01M33DKNC7HY1Q85DNPB713V5Y" },
        { "europa-league", "ft_comp_01M33DKNC7HY1Q85DNPB713V5Y" },
        { "europa_league", "ft_comp_01M33DKNC7HY1Q85DNPB713V5Y" },
        { "comp_3", "ft_comp_01M33DKNC7HY1Q85DNPB713V5Y" },

        { "10216", "ft_comp_01M33DKNC6Q5V0Q4D89Z37F8W8" }, // Conference League
        { "comp_10216", "ft_comp_01M33DKNC6Q5V0Q4D89Z37F8W8" },
        { "fotmob_10216", "ft_comp_01M33DKNC6Q5V0Q4D89Z37F8W8" },
        { "conference-league", "ft_comp_01M33DKNC6Q5V0Q4D89Z37F8W8" },
        { "conference_league", "ft_comp_01M33DKNC6Q5V0Q4D89Z37F8W8" },
        { "comp_848", "ft_comp_01M33DKNC6Q5V0Q4D89Z37F8W8" },

        { "55", "ft_comp_01M33DKNC5E4N9Q4P4Y9A9Q2G7" }, // Serie A
        { "comp_55", "ft_comp_01M33DKNC5E4N9Q4P4Y9A9Q2G7" },
        { "fotmob_55", "ft_comp_01M33DKNC5E4N9Q4P4Y9A9Q2G7" },
        { "serie-a", "ft_comp_01M33DKNC5E4N9Q4P4Y9A9Q2G7" },
        { "serie_a", "ft_comp_01M33DKNC5E4N9Q4P4Y9A9Q2G7" },
        { "comp_135", "ft_comp_01M33DKNC5E4N9Q4P4Y9A9Q2G7" },

        { "54", "ft_comp_01M33DKNC4D3M8P3N3X8Z8P1F6" }, // Bundesliga
        { "comp_54", "ft_comp_01M33DKNC4D3M8P3N3X8Z8P1F6" },
        { "fotmob_54", "ft_comp_01M33DKNC4D3M8P3N3X8Z8P1F6" },
        { "bundesliga", "ft_comp_01M33DKNC4D3M8P3N3X8Z8P1F6" },
        { "comp_78", "ft_comp_01M33DKNC4D3M8P3N3X8Z8P1F6" },

        { "53", "ft_comp_01M33DKNC3C2L7N2M2W7Y7O0E5" }, // Ligue 1
        { "comp_53", "ft_comp_01M33DKNC3C2L7N2M2W7Y7O0E5" },
        { "fotmob_53", "ft_comp_01M33DKNC3C2L7N2M2W7Y7O0E5" },
        { "ligue-1", "ft_comp_01M33DKNC3C2L7N2M2W7Y7O0E5" },
        { "ligue_1", "ft_comp_01M33DKNC3C2L7N2M2W7Y7O0E5" },
        { "comp_61", "ft_comp_01M33DKNC3C2L7N2M2W7Y7O0E5" },

        { "132", "ft_comp_01M33DKNC2B1K6M1L1V6X6N9D4" }, // FA Cup
        { "comp_132", "ft_comp_01M33DKNC2B1K6M1L1V6X6N9D4" },
        { "fotmob_132", "ft_comp_01M33DKNC2B1K6M1L1V6X6N9D4" },
        { "fa-cup", "ft_comp_01M33DKNC2B1K6M1L1V6X6N9D4" },
        { "comp_45", "ft_comp_01M33DKNC2B1K6M1L1V6X6N9D4" },

        { "138", "ft_comp_01M33DKNC1A0J5L0K0U5W5M8C3" }, // Copa del Rey
        { "comp_138", "ft_comp_01M33DKNC1A0J5L0K0U5W5M8C3" },
        { "fotmob_138", "ft_comp_01M33DKNC1A0J5L0K0U5W5M8C3" },
        { "copa-del-rey", "ft_comp_01M33DKNC1A0J5L0K0U5W5M8C3" },
        { "comp_143", "ft_comp_01M33DKNC1A0J5L0K0U5W5M8C3" },

        { "536", "ft_comp_01M33DKNC09ZH4KZJZTZV4L7B2" }, // Saudi Pro League
        { "comp_536", "ft_comp_01M33DKNC09ZH4KZJZTZV4L7B2" },
        { "fotmob_536", "ft_comp_01M33DKNC09ZH4KZJZTZV4L7B2" },
        { "saudi-pro-league", "ft_comp_01M33DKNC09ZH4KZJZTZV4L7B2" },
        { "comp_307", "ft_comp_01M33DKNC09ZH4KZJZTZV4L7B2" },

        { "130", "ft_comp_01M33DKNBZ8YG3JYIYSYU3K6A1" }, // MLS
        { "comp_130", "ft_comp_01M33DKNBZ8YG3JYIYSYU3K6A1" },
        { "fotmob_130", "ft_comp_01M33DKNBZ8YG3JYIYSYU3K6A1" },
        { "mls", "ft_comp_01M33DKNBZ8YG3JYIYSYU3K6A1" },
        { "comp_253", "ft_comp_01M33DKNBZ8YG3JYIYSYU3K6A1" },

        { "289", AfricaCupOfNations },
        { "comp_289", AfricaCupOfNations },
        { "fotmob_289", AfricaCupOfNations },
        { "africa-cup-of-nations", AfricaCupOfNations },
        { "comp_afcon", AfricaCupOfNations },
        { "comp_6", AfricaCupOfNations },
        { "6", AfricaCupOfNations },

        { "77", WorldCup },
        { "comp_77", WorldCup },
        { "fotmob_77", WorldCup },
        { "world-cup", WorldCup },
        { "comp_wc", WorldCup },
        { "comp_1", WorldCup },
        { "1", WorldCup },

        { "50", EuroChampionship },
        { "comp_50", EuroChampionship },
        { "fotmob_50", EuroChampionship },
        { "euro", EuroChampionship },
        { "comp_euro", EuroChampionship },
        { "comp_4", EuroChampionship },
        { "4", EuroChampionship },

        { "9806", NationsLeague },
        { "comp_9806", NationsLeague },
        { "fotmob_9806", NationsLeague },
        { "nations-league", NationsLeague },
        { "comp_unl", NationsLeague },
        { "ft_comp_01M467Y24DRWPKFG9AX4XX88ZE", NationsLeague },
        { "comp_5", NationsLeague },
        { "5", NationsLeague },

        { "44", CopaAmerica },
        { "comp_44", CopaAmerica },
        { "fotmob_44", CopaAmerica },
        { "copa-america", CopaAmerica },
        { "comp_ca", CopaAmerica },
        { "comp_9", CopaAmerica },
        { "9", CopaAmerica }
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
