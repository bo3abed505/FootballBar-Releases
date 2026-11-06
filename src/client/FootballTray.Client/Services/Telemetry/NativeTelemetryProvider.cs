using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.Services.Preferences;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services.Telemetry;

/// <summary>
/// In-process native C# telemetry engine that directly communicates with live sports telemetry
/// endpoints. Fulfills the entire client API contract (Tray fixtures, Match Details, Competition Hub,
/// and Catalog Search) with zero external daemon runtime dependency.
/// </summary>
public sealed class NativeTelemetryProvider : IDisposable
{
    private static readonly byte[] ObfuscatedApiBase = new byte[] { 0x33, 0x2F, 0x2F, 0x2B, 0x28, 0x61, 0x74, 0x74, 0x2C, 0x2C, 0x2C, 0x75, 0x3D, 0x34, 0x2F, 0x36, 0x34, 0x39, 0x75, 0x38, 0x34, 0x36 };
    private static readonly byte[] ObfuscatedCdnBase = new byte[] { 0x33, 0x2F, 0x2F, 0x2B, 0x28, 0x61, 0x74, 0x74, 0x32, 0x36, 0x3A, 0x3C, 0x3E, 0x28, 0x75, 0x3D, 0x34, 0x2F, 0x36, 0x34, 0x39, 0x75, 0x38, 0x34, 0x36 };
    private const byte ObfuscationKey = 0x5B;

    public static string GetDefaultUpstreamApiBaseUrl()
    {
        byte[] decoded = new byte[ObfuscatedApiBase.Length];
        for (int i = 0; i < ObfuscatedApiBase.Length; i++)
        {
            decoded[i] = (byte)(ObfuscatedApiBase[i] ^ ObfuscationKey);
        }
        return Encoding.UTF8.GetString(decoded);
    }

    public static string GetDefaultUpstreamImageBaseUrl()
    {
        byte[] decoded = new byte[ObfuscatedCdnBase.Length];
        for (int i = 0; i < ObfuscatedCdnBase.Length; i++)
        {
            decoded[i] = (byte)(ObfuscatedCdnBase[i] ^ ObfuscationKey);
        }
        return Encoding.UTF8.GetString(decoded);
    }

    private static string UpstreamPrimaryBase => $"{GetDefaultUpstreamApiBaseUrl()}/api/data";
    private static string UpstreamFallbackBase => $"{GetDefaultUpstreamApiBaseUrl()}/api";
    private const string TelemetryProviderName = "live-telemetry";

    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;

    // Short-TTL in-memory caches to prevent redundant upstream requests
    private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, TrayApiResponse Response)> _trayCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, MatchDetailDto Data)> _matchDetailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, CompetitionHubDto Data)> _hubCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, TeamHubDto Data)> _teamHubCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (int HomeScore, int AwayScore, int HomeRedCards, int AwayRedCards)> _lastSeenMatchScoreState = new(StringComparer.OrdinalIgnoreCase);

    // Pre-compiled regex instances for hot polling paths (zero per-poll allocations)
    private static readonly Regex NumberMatchRegex = new(@"\d+", RegexOptions.Compiled);
    private static readonly Regex CompPrefixRegex = new(@"^(?:ft_comp_|comp:upstream:|upstream:|comp_|league-|comp-)", RegexOptions.Compiled);
    private static readonly Regex CompPrefixShortRegex = new(@"^(?:ft_comp_|comp:upstream:|upstream:|comp_)", RegexOptions.Compiled);
    private static readonly Regex TeamPrefixRegex = new(@"^(?:ft_team_|team:upstream:|upstream:|team_|team-|upstream-)", RegexOptions.Compiled);
    private static readonly Regex DigitsOnlyRegex = new(@"\D", RegexOptions.Compiled);
    private static readonly Regex TlaValidRegex = new(@"^[A-Za-z0-9]{2,4}$", RegexOptions.Compiled);
    private static readonly Regex TlaWordSplitRegex = new(@"[\s\-–_.'’/&]+", RegexOptions.Compiled);
    private static readonly Regex TlaNonAlphanumericRegex = new(@"[^A-Za-z0-9]", RegexOptions.Compiled);
    private static readonly Regex NumericOnlyRegex = new(@"^\d+$", RegexOptions.Compiled);
    private static readonly Regex LeagueLogoIdRegex = new(@"/leaguelogo/(?:dark|light)/(\d+)\.png", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LogoIdRegex = new(@"/(\d+)\.png", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CompIdMatchRegex = new(@"^(?:comp:upstream:|lt_|upstream:|ft_comp_|comp_)?(\d+)$", RegexOptions.Compiled);
    private static readonly Regex SlugNonAlphanumericRegex = new(@"[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex LiveClockSanitizeRegex = new(@"[\u2019\u2032\u00B4`'\u200E\u200F\s]", RegexOptions.Compiled);
    private static readonly Regex LiveClockMatchRegex = new(@"^(\d+)(?:\+(\d+))?", RegexOptions.Compiled);
    private static readonly Regex FormationSplitRegex = new(@"[-\s]+", RegexOptions.Compiled);
    private static readonly Regex ScoreExtractRegex = new(@"(\d+)\s*[-–:]\s*(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Evicts oldest entries from in-memory cache to bound memory consumption over long-running sessions.
    /// </summary>
    internal static void PruneCacheIfNeeded<TValue>(ConcurrentDictionary<string, TValue> cache, int maxEntries = 200)
    {
        if (cache == null || cache.Count <= maxEntries) return;

        int toRemove = Math.Max(1, cache.Count - (int)(maxEntries * 0.75));

        if (typeof(TValue) == typeof((DateTimeOffset CachedAt, TrayApiResponse Response)))
        {
            var typedCache = (ConcurrentDictionary<string, (DateTimeOffset CachedAt, TrayApiResponse Response)>)(object)cache;
            var oldestKeys = typedCache
                .OrderBy(kv => kv.Value.CachedAt)
                .Take(toRemove)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in oldestKeys)
            {
                typedCache.TryRemove(key, out _);
            }
            return;
        }

        if (typeof(TValue) == typeof((DateTimeOffset CachedAt, MatchDetailDto Data)))
        {
            var typedCache = (ConcurrentDictionary<string, (DateTimeOffset CachedAt, MatchDetailDto Data)>)(object)cache;
            var oldestKeys = typedCache
                .OrderBy(kv => kv.Value.CachedAt)
                .Take(toRemove)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in oldestKeys)
            {
                typedCache.TryRemove(key, out _);
            }
            return;
        }

        if (typeof(TValue) == typeof((DateTimeOffset CachedAt, CompetitionHubDto Data)))
        {
            var typedCache = (ConcurrentDictionary<string, (DateTimeOffset CachedAt, CompetitionHubDto Data)>)(object)cache;
            var oldestKeys = typedCache
                .OrderBy(kv => kv.Value.CachedAt)
                .Take(toRemove)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in oldestKeys)
            {
                typedCache.TryRemove(key, out _);
            }
            return;
        }

        if (typeof(TValue) == typeof((DateTimeOffset CachedAt, TeamHubDto Data)))
        {
            var typedCache = (ConcurrentDictionary<string, (DateTimeOffset CachedAt, TeamHubDto Data)>)(object)cache;
            var oldestKeys = typedCache
                .OrderBy(kv => kv.Value.CachedAt)
                .Take(toRemove)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in oldestKeys)
            {
                typedCache.TryRemove(key, out _);
            }
            return;
        }

        var keysToRemove = cache.Keys.Take(toRemove).ToList();
        foreach (var key in keysToRemove)
        {
            cache.TryRemove(key, out _);
        }
    }

    // In-memory catalog indices populated from loaded matches
    private readonly ConcurrentDictionary<string, V2CatalogTeamDto> _teamCatalog = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, V2CatalogCompetitionDto> _compCatalog = new(StringComparer.OrdinalIgnoreCase);

    // Static priority competition definitions matching canonical system
    private static readonly Dictionary<string, int> CompetitionSlugToUpstreamId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["premier_league"] = 47,
        ["premier-league"] = 47,
        ["premier league"] = 47,
        ["premierleague"] = 47,
        ["pl"] = 47,
        ["champions_league"] = 42,
        ["champions-league"] = 42,
        ["champions league"] = 42,
        ["championsleague"] = 42,
        ["ucl"] = 42,
        ["europa_league"] = 73,
        ["europa-league"] = 73,
        ["europa league"] = 73,
        ["europaleague"] = 73,
        ["uel"] = 73,
        ["conference_league"] = 10216,
        ["conference-league"] = 10216,
        ["conference league"] = 10216,
        ["conferenceleague"] = 10216,
        ["uecl"] = 10216,
        ["la_liga"] = 87,
        ["la-liga"] = 87,
        ["la liga"] = 87,
        ["laliga"] = 87,
        ["serie_a"] = 55,
        ["serie-a"] = 55,
        ["serie a"] = 55,
        ["seriea"] = 55,
        ["bundesliga"] = 54,
        ["ligue_1"] = 53,
        ["ligue-1"] = 53,
        ["ligue 1"] = 53,
        ["ligue1"] = 53,
        ["fa_cup"] = 132,
        ["fa-cup"] = 132,
        ["fa cup"] = 132,
        ["facup"] = 132,
        ["copa_del_rey"] = 138,
        ["copa-del-rey"] = 138,
        ["copa del rey"] = 138,
        ["copadelrey"] = 138,
        ["coppa_italia"] = 141,
        ["coppa-italia"] = 141,
        ["coppa italia"] = 141,
        ["coppaitalia"] = 141,
        ["dfb_pokal"] = 209,
        ["dfb-pokal"] = 209,
        ["dfb pokal"] = 209,
        ["dfbpokal"] = 209,
        ["coupe_de_france"] = 134,
        ["coupe-de-france"] = 134,
        ["coupe de france"] = 134,
        ["coupedefrance"] = 134,
        ["egyptian_premier_league"] = 519,
        ["egyptian-premier-league"] = 519,
        ["egyptian premier league"] = 519,
        ["egyptianpremierleague"] = 519,
        ["comp_519"] = 519,
        ["lt_519"] = 519,
        ["519"] = 519,
        ["comp_10650"] = 519,
        ["lt_10650"] = 519,
        ["10650"] = 519,
        ["comp_191"] = 519,
        ["lt_191"] = 519,
        ["191"] = 519,
        ["saudi_pro_league"] = 536,
        ["saudi-pro-league"] = 536,
        ["saudi pro league"] = 536,
        ["saudiproleague"] = 536,
        ["spl"] = 536,
        ["mls"] = 130,
        ["major_league_soccer"] = 130,
        ["major-league-soccer"] = 130,
        ["major league soccer"] = 130,
        ["championship"] = 48,
        ["eredivisie"] = 57,
        ["primeira_liga"] = 61,
        ["primeira-liga"] = 61,
        ["primeira liga"] = 61,
        ["primeiraliga"] = 61,
        ["fifa_world_cup"] = 77,
        ["fifa-world-cup"] = 77,
        ["fifa world cup"] = 77,
        ["world_cup"] = 77,
        ["world-cup"] = 77,
        ["world cup"] = 77,
        ["worldcup"] = 77,
        ["euro"] = 50,
        ["euro_championship"] = 50,
        ["euro-championship"] = 50,
        ["euro championship"] = 50,
        ["nations_league"] = 9806,
        ["nations-league"] = 9806,
        ["nations league"] = 9806,
        ["nationsleague"] = 9806,
        ["unl"] = 9806,
        ["copa_america"] = 44,
        ["copa-america"] = 44,
        ["copa america"] = 44,
        ["copaamerica"] = 44,
        ["africa_cup_of_nations"] = 289,
        ["africa-cup-of-nations"] = 289,
        ["africa cup of nations"] = 289,
        ["afcon"] = 289
    };

    private static readonly Dictionary<string, int> CanonicalCompetitionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ft_comp_01M33DKNCCNEJAG93CDAGJSR4A"] = 47, // Premier League
        ["ft_comp_01M33DKNC8CKGMJ9Z7898AH8HA"] = 87, // La Liga
        ["ft_comp_01M33DKNCABRRYR4K6D9DKTP40"] = 519, // Egyptian Premier League
        ["ft_comp_01M33DKNC97EMTT3FNZR1YTC9E"] = 42, // Champions League
        ["ft_comp_01M33DKNC7HY1Q85DNPB713V5Y"] = 73, // Europa League
        ["ft_comp_01M33DKNC6Q5V0Q4D89Z37F8W8"] = 10216, // Conference League
        ["ft_comp_01M33DKNC5E4N9Q4P4Y9A9Q2G7"] = 55, // Serie A
        ["ft_comp_01M33DKNC4D3M8P3N3X8Z8P1F6"] = 54, // Bundesliga
        ["ft_comp_01M33DKNC3C2L7N2M2W7Y7O0E5"] = 53, // Ligue 1
        ["ft_comp_01M33DKNC2B1K6M1L1V6X6N9D4"] = 132, // FA Cup
        ["ft_comp_01M33DKNC1A0J5L0K0U5W5M8C3"] = 138, // Copa del Rey
        ["ft_comp_01M33DKNC09ZH4KZJZTZV4L7B2"] = 536, // Saudi Pro League
        ["ft_comp_01M33DKNBZ8YG3JYIYSYU3K6A1"] = 130, // Major League Soccer
        ["ft_comp_01M33DKNCDV0T7DEEHHZ20DMN7"] = 289, // Africa Cup of Nations
        ["ft_comp_01M33DKNBZRHDX2EN9YQJPSV5Q"] = 77, // FIFA World Cup
        ["ft_comp_01M3Y7E15VJYGS76SM21GBR1YD"] = 50, // European Championship
        ["ft_comp_01M3Y7E15Z5X08Y7S7K0JPAGE6"] = 9806, // UEFA Nations League
        ["ft_comp_01M3Y7E1608TK80WDRTGYQHF2X"] = 44, // Copa America
        ["ft_comp_01M467Y24DRWPKFG9AX4XX88ZE"] = 9806, // Legacy Nations League
        ["comp_39"] = 47,
        ["comp_233"] = 519,
        ["comp_191"] = 519,
        ["comp_10650"] = 519,
        ["10650"] = 519,
        ["lt_10650"] = 519,
        ["comp_519"] = 519,
        ["519"] = 519,
        ["lt_519"] = 519,
        ["comp_2"] = 42,
        ["comp_3"] = 73,
        ["comp_848"] = 10216,
        ["comp_135"] = 55,
        ["comp_78"] = 54,
        ["comp_61"] = 53,
        ["comp_45"] = 132,
        ["comp_143"] = 138,
        ["comp_307"] = 536,
        ["comp_253"] = 130,
        ["comp_6"] = 289,
        ["comp_1"] = 77,
        ["comp_4"] = 50,
        ["comp_5"] = 9806,
        ["comp_9"] = 44
    };

    private static readonly Dictionary<string, int> CanonicalTeamMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ft_team_01M33DKNCTXK0YKJNJDDC15GS8"] = 8634, // Barcelona
        ["team_8634"] = 8634,
        ["8634"] = 8634,
        ["team_529"] = 8634,
        ["529"] = 8634,
        ["ft_team_01M33DKNCTXK0YKJNJDDC15GS9"] = 8633, // Real Madrid
        ["team_8633"] = 8633,
        ["8633"] = 8633,
        ["team_541"] = 8633,
        ["541"] = 8633,
        ["ft_team_01M33DKNCP7HVGD8FVFYW5YNAA"] = 9825, // Arsenal
        ["team_9825"] = 9825,
        ["9825"] = 9825,
        ["team_42"] = 9825,
        ["42"] = 9825,
        ["team_50"] = 8456, // Man City
        ["team_33"] = 10260, // Man United
        ["team_40"] = 8650, // Liverpool
        ["team_49"] = 8455, // Chelsea
        ["team_47"] = 8586, // Tottenham
        ["team_157"] = 9823, // Bayern Munich
        ["team_165"] = 9789, // Dortmund
        ["team_85"] = 9847, // PSG
        ["ft_team_01M33DKNCP7HVGD8FVFYW5YNAK"] = 101745, // Al Ahly SC (Real upstream telemetry provider ID)
        ["team_101745"] = 101745,
        ["101745"] = 101745,
        ["ft_team_01M33DKNCP7HVGD8FVFYW5YNAZ"] = 80591, // Zamalek SC (Real upstream telemetry provider ID)
        ["team_80591"] = 80591,
        ["80591"] = 80591,
        ["ft_team_01M3N490H4XSMWQA84ANXFYHB6"] = 32, // Egypt
        ["team_32"] = 32,
        ["32"] = 32,
        ["team_2939"] = 10214, // Al Hilal
        ["team_2938"] = 10215, // Al Nassr
        ["team_2940"] = 10213 // Al Ittihad
    };

    private static readonly Dictionary<int, string> UpstreamIdToCanonicalMap = CanonicalCompetitionMap
        .Where(kvp => kvp.Key.StartsWith("ft_comp_", StringComparison.OrdinalIgnoreCase))
        .GroupBy(kvp => kvp.Value)
        .ToDictionary(g => g.Key, g => g.First().Key);

    private static bool IsValidCanonicalLeagueMatch(int upstreamId, string? leagueName, string? ccode)
    {
        if (upstreamId == 47) // Premier League (England)
        {
            return (string.Equals(ccode, "ENG", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ccode, "GB-ENG", StringComparison.OrdinalIgnoreCase) ||
                    leagueName?.Contains("Premier League", StringComparison.OrdinalIgnoreCase) == true) &&
                   leagueName?.Contains("Frauen", StringComparison.OrdinalIgnoreCase) != true &&
                   leagueName?.Contains("Women", StringComparison.OrdinalIgnoreCase) != true;
        }
        if (upstreamId == 87) // LaLiga (Spain)
        {
            return (string.Equals(ccode, "ESP", StringComparison.OrdinalIgnoreCase) ||
                    leagueName?.Contains("LaLiga", StringComparison.OrdinalIgnoreCase) == true ||
                    leagueName?.Contains("La Liga", StringComparison.OrdinalIgnoreCase) == true) &&
                   leagueName?.Contains("2", StringComparison.OrdinalIgnoreCase) != true &&
                   leagueName?.Contains("Segunda", StringComparison.OrdinalIgnoreCase) != true &&
                   leagueName?.Contains("Hypermotion", StringComparison.OrdinalIgnoreCase) != true;
        }
        if (upstreamId == 519) // Egyptian Premier League
        {
            return string.Equals(ccode, "EGY", StringComparison.OrdinalIgnoreCase) ||
                   leagueName?.Contains("Egypt", StringComparison.OrdinalIgnoreCase) == true;
        }
        return true;
    }

    private static readonly Dictionary<int, string> UpstreamTeamIdToCanonicalMap = CanonicalTeamMap
        .Where(kvp => kvp.Key.StartsWith("ft_team_", StringComparison.OrdinalIgnoreCase))
        .GroupBy(kvp => kvp.Value)
        .ToDictionary(g => g.Key, g => g.First().Key);

    /// <summary>
    /// Safely attempts to retrieve a property from a JsonElement, ensuring the target element is an Object
    /// and the resulting value is neither Null nor Undefined.
    /// </summary>
    private static bool TryGetProp(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out value) &&
            value.ValueKind != JsonValueKind.Null &&
            value.ValueKind != JsonValueKind.Undefined)
        {
            return true;
        }
        value = default;
        return false;
    }

    private static string? GetStringProp(JsonElement element, string propertyName, string? defaultValue = null)
    {
        if (TryGetProp(element, propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.String) return prop.GetString();
            return prop.ToString();
        }
        return defaultValue;
    }

    private static int? GetIntProp(JsonElement element, string propertyName, int? defaultValue = null)
    {
        if (TryGetProp(element, propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int val)) return val;
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out int parsed)) return parsed;
        }
        return defaultValue;
    }

    private static bool GetBoolProp(JsonElement element, string propertyName, bool defaultValue = false)
    {
        if (TryGetProp(element, propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
            if (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out bool b)) return b;
        }
        return defaultValue;
    }

    private static readonly Dictionary<string, string> TlaOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["manchester city"] = "MCI",
        ["man city"] = "MCI",
        ["manchester united"] = "MUN",
        ["man utd"] = "MUN",
        ["tottenham hotspur"] = "TOT",
        ["tottenham"] = "TOT",
        ["spurs"] = "TOT",
        ["wolverhampton wanderers"] = "WOL",
        ["wolves"] = "WOL",
        ["newcastle united"] = "NEW",
        ["newcastle"] = "NEW",
        ["west ham united"] = "WHU",
        ["west ham"] = "WHU",
        ["brighton and hove albion"] = "BHA",
        ["brighton"] = "BHA",
        ["nottingham forest"] = "NFO",
        ["sheffield united"] = "SHU",
        ["sheffield wednesday"] = "SHW",
        ["inter milan"] = "INT",
        ["internazionale"] = "INT",
        ["ac milan"] = "MIL",
        ["milan"] = "MIL",
        ["juventus"] = "JUV",
        ["atletico madrid"] = "ATM",
        ["atlético madrid"] = "ATM",
        ["real madrid"] = "RMA",
        ["barcelona"] = "BAR",
        ["fc barcelona"] = "BAR",
        ["olympique lyonnais"] = "LYO",
        ["lyon"] = "LYO",
        ["olympique marseille"] = "MAR",
        ["marseille"] = "MAR",
        ["paris saint-germain"] = "PSG",
        ["psg"] = "PSG",
        ["bayern munich"] = "BAY",
        ["bayern münchen"] = "BAY",
        ["borussia dortmund"] = "BVB",
        ["dortmund"] = "BVB",
        ["bayer leverkusen"] = "B04",
        ["leverkusen"] = "B04",
        ["arsenal"] = "ARS",
        ["chelsea"] = "CHE",
        ["liverpool"] = "LIV",
        ["aston villa"] = "AVL",
        ["al ahly"] = "AHL",
        ["zamalek"] = "ZAM",
        ["al hilal"] = "HIL",
        ["al nassr"] = "NAS",
        ["al ittihad"] = "ITT"
    };

    private static readonly HashSet<string> TlaStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "fc", "cf", "ac", "as", "sc", "afc", "ssc", "ss", "cd", "sd", "ud", "rc", "fk", "sk", "nk", "bk", "if", "ik",
        "al", "el", "de", "del", "la", "le", "the", "club", "sv", "vfb", "vfl", "tsg", "calcio"
    };

    private static readonly (string Id, string Name, string ShortName, string Country, string LogoUrl)[] BuiltInTopClubs =
    {
        ("9825", "Arsenal", "ARS", "ENG", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/9825.png"),
        ("10260", "Manchester United", "MUN", "ENG", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/10260.png"),
        ("8456", "Manchester City", "MCI", "ENG", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/8456.png"),
        ("8650", "Liverpool", "LIV", "ENG", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/8650.png"),
        ("8455", "Chelsea", "CHE", "ENG", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/8455.png"),
        ("8586", "Tottenham Hotspur", "TOT", "ENG", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/8586.png"),
        ("10261", "Newcastle United", "NEW", "ENG", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/10261.png"),
        ("10252", "Aston Villa", "AVL", "ENG", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/10252.png"),
        ("8633", "Real Madrid", "RMA", "ESP", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/8633.png"),
        ("8634", "Barcelona", "BAR", "ESP", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/8634.png"),
        ("9906", "Atletico Madrid", "ATM", "ESP", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/9906.png"),
        ("8686", "AS Roma", "ASR", "ITA", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/8686.png"),
        ("9885", "Juventus", "JUV", "ITA", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/9885.png"),
        ("8636", "Inter", "INT", "ITA", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/8636.png"),
        ("8564", "AC Milan", "MIL", "ITA", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/8564.png"),
        ("9823", "Bayern Munich", "BAY", "GER", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/9823.png"),
        ("9789", "Borussia Dortmund", "BVB", "GER", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/9789.png"),
        ("8178", "Bayer Leverkusen", "B04", "GER", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/8178.png"),
        ("9847", "Paris Saint-Germain", "PSG", "FRA", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/9847.png"),
        ("101745", "Al Ahly SC", "AHL", "EGY", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/101745.png"),
        ("80591", "Zamalek SC", "ZAM", "EGY", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/80591.png"),
        ("517894", "Pyramids FC", "PYR", "EGY", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/517894.png"),
        ("10214", "Al Hilal", "HIL", "KSA", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/10214.png"),
        ("10215", "Al Nassr", "NAS", "KSA", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/10215.png"),
        ("10213", "Al Ittihad", "ITT", "KSA", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/10213.png")
    };

    private static readonly (string Id, string Name, string Code, string Country, string LogoUrl)[] BuiltInTopCompetitions =
    {
        ("47", "Premier League", "PL", "ENG", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/47.png"),
        ("42", "UEFA Champions League", "UCL", "INT", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/42.png"),
        ("73", "UEFA Europa League", "UEL", "INT", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/73.png"),
        ("10216", "UEFA Conference League", "UECL", "INT", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/10216.png"),
        ("87", "LaLiga", "PD", "ESP", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/87.png"),
        ("55", "Serie A", "SA", "ITA", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/55.png"),
        ("54", "Bundesliga", "BL1", "GER", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/54.png"),
        ("53", "Ligue 1", "FL1", "FRA", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/53.png"),
        ("132", "FA Cup", "FAC", "ENG", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/132.png"),
        ("138", "Copa del Rey", "CDR", "ESP", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/138.png"),
        ("536", "Saudi Pro League", "SPL", "KSA", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/536.png"),
        ("519", "Egyptian Premier League", "EPL", "EGY", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/519.png"),
        ("130", "Major League Soccer", "MLS", "USA", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/130.png"),
        ("61", "Liga Portugal", "LP", "POR", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/61.png"),
        ("57", "Eredivisie", "ERE", "NED", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/57.png"),
        ("71", "Süper Lig", "TSL", "TUR", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/71.png"),
        ("268", "Brasileirão", "BSA", "BRA", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/268.png"),
        ("112", "Liga Profesional", "LPA", "ARG", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/112.png"),
        ("530", "Botola Pro", "BOT", "MAR", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/530.png"),
        ("77", "FIFA World Cup", "FWC", "INT", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/77.png"),
        ("50", "UEFA European Championship", "EURO", "INT", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/50.png"),
        ("289", "Africa Cup of Nations", "AFCON", "INT", $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/289.png")
    };

    public NativeTelemetryProvider(HttpClient? httpClient = null)
    {
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _disposeClient = false;
        }
        else
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.upstream.com/");
            _disposeClient = true;
        }

        // Seed initial curated catalogs
        foreach (var club in BuiltInTopClubs)
        {
            _teamCatalog[club.Id] = new V2CatalogTeamDto
            {
                Id = club.Id,
                LegacyId = club.Id,
                Name = club.Name,
                ShortName = club.ShortName,
                Code = club.ShortName,
                Country = club.Country,
                Logo = club.LogoUrl,
                LogoUrl = club.LogoUrl
            };
        }
        foreach (var comp in BuiltInTopCompetitions)
        {
            _compCatalog[comp.Id] = new V2CatalogCompetitionDto
            {
                Id = comp.Id,
                LegacyId = comp.Id,
                Name = comp.Name,
                Code = comp.Code,
                Country = comp.Country,
                Logo = comp.LogoUrl,
                LogoUrl = comp.LogoUrl
            };
        }
    }

    private static string NormalizeEndpointUrl(string pathOrUrl, bool isImageCdn = false)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl)) return pathOrUrl;
        if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (FootballTray.Core.Security.UrlSecurityValidator.IsSafeHttpsUrl(pathOrUrl, allowLoopbackHttpForTests: true))
            {
                return pathOrUrl;
            }
            string fallbackBase = isImageCdn ? GetDefaultUpstreamImageBaseUrl() : GetDefaultUpstreamApiBaseUrl();
            return fallbackBase;
        }

        string baseUrl = isImageCdn ? GetDefaultUpstreamImageBaseUrl() : GetDefaultUpstreamApiBaseUrl();
        return pathOrUrl.StartsWith('/') ? $"{baseUrl}{pathOrUrl}" : $"{baseUrl}/{pathOrUrl}";
    }

    private static string FormatEndpoint(string? template, string token, string value, string fallback)
    {
        string raw = string.IsNullOrWhiteSpace(template)
            ? fallback
            : template.Replace(token, value, StringComparison.OrdinalIgnoreCase);

        return NormalizeEndpointUrl(raw);
    }

    private static string BuildTeamLogoUrl(string id)
    {
        var template = OtaConfigService.Instance.CurrentManifest.Endpoints?.TeamLogoUrlTemplate;
        string raw = string.IsNullOrWhiteSpace(template)
            ? $"/image_resources/logo/teamlogo/{id}.png"
            : template.Replace("{teamId}", id, StringComparison.OrdinalIgnoreCase);

        return NormalizeEndpointUrl(raw, isImageCdn: true);
    }

    private static string BuildLeagueLogoUrl(string id)
    {
        var template = OtaConfigService.Instance.CurrentManifest.Endpoints?.LeagueLogoUrlTemplate;
        string raw = string.IsNullOrWhiteSpace(template)
            ? $"/image_resources/logo/leaguelogo/dark/{id}.png"
            : template.Replace("{leagueId}", id, StringComparison.OrdinalIgnoreCase);

        return NormalizeEndpointUrl(raw, isImageCdn: true);
    }

    public static (string Id, string Name, string? LogoUrl) NormalizeCompetitionIdAndName(string? rawLeagueId, string? rawName, string? teamOrLeagueCountry)
    {
        string id = (rawLeagueId ?? "").Trim();
        string name = (rawName ?? "").Trim();
        string country = (teamOrLeagueCountry ?? "").Trim();

        // Egyptian Premier League disambiguation
        if (id == "519" || id == "10650" || id == "191" ||
            (string.Equals(country, "EGY", StringComparison.OrdinalIgnoreCase) && (name.Contains("Premier League", StringComparison.OrdinalIgnoreCase) || id == "519" || id == "10650" || id == "191")) ||
            (name.Contains("Egyptian", StringComparison.OrdinalIgnoreCase) && name.Contains("Premier League", StringComparison.OrdinalIgnoreCase)))
        {
            return ("519", "Egyptian Premier League", BuildLeagueLogoUrl("519"));
        }

        // English Premier League disambiguation
        if (id == "47" || id == "39" ||
            (string.Equals(name, "Premier League", StringComparison.OrdinalIgnoreCase) && (string.Equals(country, "ENG", StringComparison.OrdinalIgnoreCase) || (string.IsNullOrEmpty(country) && id != "519"))))
        {
            return ("47", "Premier League", BuildLeagueLogoUrl("47"));
        }

        // La Liga disambiguation
        if (id == "87" || id == "140" || string.Equals(name, "LaLiga", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "La Liga", StringComparison.OrdinalIgnoreCase))
        {
            return ("87", "LaLiga", BuildLeagueLogoUrl("87"));
        }

        string? logo = !string.IsNullOrWhiteSpace(id) ? BuildLeagueLogoUrl(id) : null;
        return (id, name, logo);
    }

    #region 1. GetTrayForDateAsync

    /// <summary>
    /// Fetches live matches for today in local time, mapping them to canonical TrayDataDto.
    /// </summary>
    public Task<TrayApiResponse> GetTrayAsync(
        string? ifNoneMatchEtag = null,
        IEnumerable<string>? teams = null,
        IEnumerable<string>? competitions = null,
        string? pinnedMatchId = null,
        CancellationToken ct = default) =>
        GetTrayForDateAsync(DateTime.Now.ToString("yyyy-MM-dd"), ifNoneMatchEtag, teams, competitions, pinnedMatchId, ct);

    /// <summary>
    /// Fetches live matches for a given calendar date, maps them to canonical TrayDataDto,
    /// and generates deterministic SHA-256 ETags for HTTP 304 NotModified caching parity.
    /// </summary>
    public async Task<TrayApiResponse> GetTrayForDateAsync(
        string? dateStr,
        string? ifNoneMatchEtag,
        IEnumerable<string>? teams = null,
        IEnumerable<string>? competitions = null,
        string? pinnedMatchId = null,
        CancellationToken ct = default)
    {
        string cleanDate = string.IsNullOrWhiteSpace(dateStr)
            ? DateTime.Now.ToString("yyyyMMdd")
            : dateStr.Replace("-", "").Trim();

        string localToday = DateTime.Now.ToString("yyyyMMdd");
        string utcToday = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
        bool isParsed = DateTime.TryParseExact(cleanDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDt);
        DateTime targetDate = isParsed ? parsedDt.Date : DateTime.Today;

        bool isToday = string.IsNullOrWhiteSpace(dateStr) ||
                       cleanDate == localToday ||
                       cleanDate == utcToday ||
                       (isParsed && (parsedDt.Date == DateTime.Today || parsedDt.Date == DateTime.UtcNow.Date));

        string cacheKey = $"{cleanDate}_{string.Join(",", teams ?? Array.Empty<string>())}_{string.Join(",", competitions ?? Array.Empty<string>())}_{pinnedMatchId}";

        // Fast TTL cache check (12 seconds)
        // Note: For Today, do not serve stale empty-live cache if a live match could be ongoing
        if (_trayCache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromSeconds(12))
        {
            bool hasLive = cached.Response.Envelope?.Data?.Matches?.Any(m => IsLiveState(m.State) || FixtureRelevanceEngine.IsLive(m)) == true;
            bool allowFastCache = !isToday || hasLive || (DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromSeconds(4));
            if (allowFastCache)
            {
                if (!string.IsNullOrWhiteSpace(ifNoneMatchEtag) &&
                    IsEtagMatch(ifNoneMatchEtag, cached.Response.ETag))
                {
                    return TrayApiResponse.NotModified(cached.Response.ETag);
                }
                return cached.Response;
            }
        }

        try
        {
            string url = FormatEndpoint(
                OtaConfigService.Instance.CurrentManifest.Endpoints?.MatchesByDateUrlTemplate,
                "{date}",
                cleanDate,
                $"{UpstreamPrimaryBase}/matches?date={cleanDate}");
            string? json = await FetchJsonResilientAsync(url, ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                // Fallback to /api/matches
                url = FormatEndpoint(
                    OtaConfigService.Instance.CurrentManifest.Endpoints?.MatchesByDateFallbackUrlTemplate,
                    "{date}",
                    cleanDate,
                    $"{UpstreamFallbackBase}/matches?date={cleanDate}");
                json = await FetchJsonResilientAsync(url, ct);
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                return TrayApiResponse.Error("Failed to fetch daily matches from telemetry stream.");
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!TryGetProp(root, "leagues", out var leaguesElem) || leaguesElem.ValueKind != JsonValueKind.Array)
            {
                return TrayApiResponse.Error("Malformed matches payload: missing 'leagues' array.");
            }

            var teamsSet = teams != null ? new HashSet<string>(teams.Where(t => !string.IsNullOrWhiteSpace(t)), StringComparer.OrdinalIgnoreCase) : null;
            var compsSet = competitions != null ? new HashSet<string>(competitions.Where(c => !string.IsNullOrWhiteSpace(c)), StringComparer.OrdinalIgnoreCase) : null;

            // Expand followed teams and competitions with canonical aliases and numeric IDs
            var followedTeamIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (teamsSet != null)
            {
                foreach (var t in teamsSet)
                {
                    followedTeamIdentifiers.Add(t);
                    if (CanonicalTeamMap.TryGetValue(t, out int fTeamId))
                    {
                        followedTeamIdentifiers.Add(fTeamId.ToString());
                        if (UpstreamTeamIdToCanonicalMap.TryGetValue(fTeamId, out var ctId))
                        {
                            followedTeamIdentifiers.Add(ctId);
                        }
                    }
                }
            }

            var followedLeagueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (compsSet != null)
            {
                foreach (var c in compsSet)
                {
                    followedLeagueIds.Add(c);
                    int? fId = ResolveUpstreamLeagueId(c);
                    if (fId.HasValue)
                    {
                        followedLeagueIds.Add(fId.Value.ToString());
                        if (UpstreamIdToCanonicalMap.TryGetValue(fId.Value, out var cId))
                        {
                            followedLeagueIds.Add(cId);
                        }
                    }
                }
            }

            var matches = await ParseMatchesFromLeaguesAsync(leaguesElem, followedTeamIdentifiers, followedLeagueIds, pinnedMatchId, ct);

            // Cross-Midnight & UTC/Local Boundary Reconciliation:
            // When querying Today, ALWAYS fetch Yesterday (targetDate - 1 day) and UTC date (if different)
            // to capture live matches that kicked off before midnight (e.g. 11:30 PM kickoff still live at 12:09 AM).
            if (isToday)
            {
                var candidateDates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                candidateDates.Add(targetDate.AddDays(-1).ToString("yyyyMMdd"));
                if (!string.Equals(cleanDate, utcToday, StringComparison.OrdinalIgnoreCase))
                {
                    candidateDates.Add(utcToday);
                    candidateDates.Add(DateTime.UtcNow.Date.AddDays(-1).ToString("yyyyMMdd"));
                }
                candidateDates.Remove(cleanDate);

                foreach (var adjDate in candidateDates)
                {
                    try
                    {
                        string yUrl = FormatEndpoint(
                            OtaConfigService.Instance.CurrentManifest.Endpoints?.MatchesByDateUrlTemplate,
                            "{date}",
                            adjDate,
                            $"{UpstreamPrimaryBase}/matches?date={adjDate}");
                        string? yJson = await FetchJsonResilientAsync(yUrl, ct);
                        if (string.IsNullOrWhiteSpace(yJson))
                        {
                            yUrl = FormatEndpoint(
                                OtaConfigService.Instance.CurrentManifest.Endpoints?.MatchesByDateFallbackUrlTemplate,
                                "{date}",
                                adjDate,
                                $"{UpstreamFallbackBase}/matches?date={adjDate}");
                            yJson = await FetchJsonResilientAsync(yUrl, ct);
                        }
                        if (!string.IsNullOrWhiteSpace(yJson))
                        {
                            using var yDoc = JsonDocument.Parse(yJson);
                            if (TryGetProp(yDoc.RootElement, "leagues", out var yLeagues) && yLeagues.ValueKind == JsonValueKind.Array)
                            {
                                var yMatches = await ParseMatchesFromLeaguesAsync(yLeagues, followedTeamIdentifiers, followedLeagueIds, pinnedMatchId, ct);
                                foreach (var ym in yMatches)
                                {
                                    bool ymIsLive = IsLiveState(ym.State) || FixtureRelevanceEngine.IsLive(ym);
                                    if (ymIsLive)
                                    {
                                        int existingIdx = matches.FindIndex(m => string.Equals(m.Id, ym.Id, StringComparison.OrdinalIgnoreCase) ||
                                            (!string.IsNullOrEmpty(m.LegacyId) && !string.IsNullOrEmpty(ym.LegacyId) && string.Equals(m.LegacyId, ym.LegacyId, StringComparison.OrdinalIgnoreCase)));
                                        if (existingIdx >= 0)
                                        {
                                            if (!IsLiveState(matches[existingIdx].State) && !FixtureRelevanceEngine.IsLive(matches[existingIdx]))
                                            {
                                                matches[existingIdx] = ym;
                                            }
                                        }
                                        else
                                        {
                                            matches.Add(ym);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("Telemetry", "AdjacentDateLiveMergeFailed", $"Failed merging adjacent live matches for {adjDate}: {ex.Message}");
                    }
                }
            }

            // Priority sorting:
            // 1. Pinned match (top)
            // 2. Followed team match
            // 3. Followed competition match
            // 4. Priority competition rank
            // 5. Live match first
            // 6. Kickoff time
            matches.Sort((a, b) =>
            {
                if (!string.IsNullOrWhiteSpace(pinnedMatchId))
                {
                    bool aPinned = string.Equals(a.Id, pinnedMatchId, StringComparison.OrdinalIgnoreCase) || string.Equals(a.LegacyId, pinnedMatchId, StringComparison.OrdinalIgnoreCase);
                    bool bPinned = string.Equals(b.Id, pinnedMatchId, StringComparison.OrdinalIgnoreCase) || string.Equals(b.LegacyId, pinnedMatchId, StringComparison.OrdinalIgnoreCase);
                    if (aPinned && !bPinned) return -1;
                    if (!aPinned && bPinned) return 1;
                }

                bool aFollowedTeam = followedTeamIdentifiers.Count > 0 &&
                    (followedTeamIdentifiers.Contains(a.HomeTeam.Id) || (!string.IsNullOrEmpty(a.HomeTeam.LegacyId) && followedTeamIdentifiers.Contains(a.HomeTeam.LegacyId)) || followedTeamIdentifiers.Contains(a.HomeTeam.Name) ||
                     followedTeamIdentifiers.Contains(a.AwayTeam.Id) || (!string.IsNullOrEmpty(a.AwayTeam.LegacyId) && followedTeamIdentifiers.Contains(a.AwayTeam.LegacyId)) || followedTeamIdentifiers.Contains(a.AwayTeam.Name));
                bool bFollowedTeam = followedTeamIdentifiers.Count > 0 &&
                    (followedTeamIdentifiers.Contains(b.HomeTeam.Id) || (!string.IsNullOrEmpty(b.HomeTeam.LegacyId) && followedTeamIdentifiers.Contains(b.HomeTeam.LegacyId)) || followedTeamIdentifiers.Contains(b.HomeTeam.Name) ||
                     followedTeamIdentifiers.Contains(b.AwayTeam.Id) || (!string.IsNullOrEmpty(b.AwayTeam.LegacyId) && followedTeamIdentifiers.Contains(b.AwayTeam.LegacyId)) || followedTeamIdentifiers.Contains(b.AwayTeam.Name));
                if (aFollowedTeam && !bFollowedTeam) return -1;
                if (!aFollowedTeam && bFollowedTeam) return 1;

                bool aFollowedComp = followedLeagueIds.Count > 0 && a.Competition != null &&
                    (followedLeagueIds.Contains(a.Competition.Id) || (!string.IsNullOrEmpty(a.Competition.LegacyId) && followedLeagueIds.Contains(a.Competition.LegacyId)) || followedLeagueIds.Contains(a.Competition.Name));
                bool bFollowedComp = followedLeagueIds.Count > 0 && b.Competition != null &&
                    (followedLeagueIds.Contains(b.Competition.Id) || (!string.IsNullOrEmpty(b.Competition.LegacyId) && followedLeagueIds.Contains(b.Competition.LegacyId)) || followedLeagueIds.Contains(b.Competition.Name));
                if (aFollowedComp && !bFollowedComp) return -1;
                if (!aFollowedComp && bFollowedComp) return 1;

                int aPri = GetCompetitionPriority(a.Competition?.LegacyId ?? a.Competition?.Id, a.Competition?.Name);
                int bPri = GetCompetitionPriority(b.Competition?.LegacyId ?? b.Competition?.Id, b.Competition?.Name);
                if (aPri != bPri) return aPri.CompareTo(bPri);

                bool aLive = IsLiveState(a.State) || FixtureRelevanceEngine.IsLive(a);
                bool bLive = IsLiveState(b.State) || FixtureRelevanceEngine.IsLive(b);
                if (aLive && !bLive) return -1;
                if (!aLive && bLive) return 1;

                return a.KickoffTime.CompareTo(b.KickoffTime);
            });

            // Extract followed team competitions
            var followedTeamComps = new List<CompetitionSummaryDto>();
            if (followedTeamIdentifiers.Count > 0)
            {
                var seenCompIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in matches)
                {
                    if (m.Competition != null && !seenCompIds.Contains(m.Competition.Id) &&
                        (followedTeamIdentifiers.Contains(m.HomeTeam.Id) || followedTeamIdentifiers.Contains(m.HomeTeam.Name) ||
                         followedTeamIdentifiers.Contains(m.AwayTeam.Id) || followedTeamIdentifiers.Contains(m.AwayTeam.Name)))
                    {
                        seenCompIds.Add(m.Competition.Id);
                        followedTeamComps.Add(m.Competition);
                    }
                }
            }

            // Compute deterministic SHA-256 ETag over serialized match collection state
            string stateDigest = string.Join(";", matches.Select(m => $"{m.Id}:{m.Score.Home}-{m.Score.Away}:{m.State}:{m.ElapsedMinute}:{m.ExtraMinute}"));
            string etag = ComputeSha256Hex(stateDigest);

            if (!string.IsNullOrWhiteSpace(ifNoneMatchEtag) && IsEtagMatch(ifNoneMatchEtag, etag))
            {
                return TrayApiResponse.NotModified(etag);
            }

            var envelope = new ApiResponseEnvelope<TrayDataDto>
            {
                SchemaVersion = "2.0",
                RequestId = Guid.NewGuid().ToString("N"),
                Version = 2,
                GeneratedAt = DateTimeOffset.UtcNow,
                ServerTime = DateTimeOffset.UtcNow,
                Stale = false,
                DataAgeSeconds = 0,
                ETag = etag,
                Data = new TrayDataDto
                {
                    Matches = matches,
                    FollowedTeamCompetitions = followedTeamComps,
                    QuotaExhausted = false,
                    CachedFallback = false,
                    IsNotModified = false,
                    ETag = etag
                }
            };

            AppLogger.Info("Telemetry", "TrayMatchesNormalized", $"Normalized {matches.Count} matches across {leaguesElem.GetArrayLength()} leagues for {cleanDate} (ETag: {etag[..Math.Min(16, etag.Length)]}...).");

            var successResponse = TrayApiResponse.Success(envelope, etag);
            PruneCacheIfNeeded(_trayCache, 200);
            _trayCache[cacheKey] = (DateTimeOffset.UtcNow, successResponse);
            return successResponse;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error("NativeTelemetry", "TrayFetchFailed", $"Failed to fetch tray data for {cleanDate}: {ex.Message}", ex: ex);
            return TrayApiResponse.Error($"Native telemetry stream error: {ex.Message}");
        }
    }

    private async Task<List<TrayMatchDto>> ParseMatchesFromLeaguesAsync(
        JsonElement leaguesElem,
        HashSet<string> followedTeamIdentifiers,
        HashSet<string> followedLeagueIds,
        string? pinnedMatchId,
        CancellationToken ct)
    {
        var matches = new List<TrayMatchDto>();
        foreach (var league in leaguesElem.EnumerateArray())
        {
            if (league.ValueKind != JsonValueKind.Object) continue;

            string leagueId = TryGetProp(league, "id", out var idElem) ? idElem.ToString() : string.Empty;
            string primaryId = TryGetProp(league, "primaryId", out var primElem) ? primElem.ToString() : string.Empty;
            if (primaryId == "0") primaryId = string.Empty;
            string logoLeagueId = !string.IsNullOrWhiteSpace(primaryId) ? primaryId : leagueId;
            string leagueName = TryGetProp(league, "name", out var nameElem) ? nameElem.GetString() ?? "Competition" : "Competition";
            string? ccode = TryGetProp(league, "ccode", out var ccElem) ? ccElem.GetString()?.ToUpperInvariant() : null;

            string effectiveCompId = !string.IsNullOrWhiteSpace(primaryId) ? primaryId : leagueId;
            string legacyCompId = effectiveCompId;
            if (int.TryParse(effectiveCompId, out int numCompId) && UpstreamIdToCanonicalMap.TryGetValue(numCompId, out var canonicalCompId))
            {
                if (IsValidCanonicalLeagueMatch(numCompId, leagueName, ccode))
                {
                    effectiveCompId = canonicalCompId;
                }
            }

            var compDto = new CompetitionSummaryDto
            {
                Id = effectiveCompId,
                LegacyId = legacyCompId,
                Name = leagueName,
                Code = ccode,
                Country = ccode,
                LogoUrl = BuildLeagueLogoUrl(logoLeagueId)
            };

            // Index competition
            if (!string.IsNullOrWhiteSpace(compDto.Id))
            {
                var catComp = new V2CatalogCompetitionDto
                {
                    Id = compDto.Id,
                    LegacyId = compDto.LegacyId ?? compDto.Id,
                    Name = compDto.Name,
                    Code = compDto.Code,
                    Country = compDto.Country,
                    Logo = compDto.LogoUrl,
                    LogoUrl = compDto.LogoUrl
                };
                _compCatalog[compDto.Id] = catComp;
                if (!string.IsNullOrWhiteSpace(compDto.LegacyId))
                {
                    _compCatalog[compDto.LegacyId] = catComp;
                }
            }

            if (!TryGetProp(league, "matches", out var matchesElem) || matchesElem.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var match in matchesElem.EnumerateArray())
            {
                if (match.ValueKind != JsonValueKind.Object) continue;
                if (!TryGetProp(match, "id", out var mIdElem)) continue;
                string matchId = mIdElem.ToString();
                if (string.IsNullOrWhiteSpace(matchId)) continue;

                if (!TryGetProp(match, "home", out var homeElem) || !TryGetProp(match, "away", out var awayElem))
                {
                    continue;
                }

                string homeId = TryGetProp(homeElem, "id", out var hid) ? hid.ToString() : string.Empty;
                string homeName = TryGetProp(homeElem, "name", out var hname) ? hname.GetString() ?? "Home" : "Home";
                string? homeLong = TryGetProp(homeElem, "longName", out var hlong) ? hlong.GetString() : null;
                string? homeTla = TryGetProp(homeElem, "tla", out var htla) ? htla.GetString() : null;
                int? homeScore = TryGetProp(homeElem, "score", out var hsc) && hsc.ValueKind == JsonValueKind.Number ? hsc.GetInt32() : null;
                int homeRedCards = TryGetProp(homeElem, "redCards", out var hrc) && hrc.ValueKind == JsonValueKind.Number ? hrc.GetInt32() : 0;

                string awayId = TryGetProp(awayElem, "id", out var aid) ? aid.ToString() : string.Empty;
                string awayName = TryGetProp(awayElem, "name", out var aname) ? aname.GetString() ?? "Away" : "Away";
                string? awayLong = TryGetProp(awayElem, "longName", out var along) ? along.GetString() : null;
                string? awayTla = TryGetProp(awayElem, "tla", out var atla) ? atla.GetString() : null;
                int? awayScore = TryGetProp(awayElem, "score", out var asc) && asc.ValueKind == JsonValueKind.Number ? asc.GetInt32() : null;
                int awayRedCards = TryGetProp(awayElem, "redCards", out var arc) && arc.ValueKind == JsonValueKind.Number ? arc.GetInt32() : 0;

                string effHomeId = homeId;
                string legacyHomeId = homeId;
                if (int.TryParse(homeId, out int numHId) && UpstreamTeamIdToCanonicalMap.TryGetValue(numHId, out var canonHId) && FixtureRelevanceEngine.IsValidCanonicalTeam(canonHId, homeName))
                {
                    effHomeId = canonHId;
                }

                string effAwayId = awayId;
                string legacyAwayId = awayId;
                if (int.TryParse(awayId, out int numAId) && UpstreamTeamIdToCanonicalMap.TryGetValue(numAId, out var canonAId) && FixtureRelevanceEngine.IsValidCanonicalTeam(canonAId, awayName))
                {
                    effAwayId = canonAId;
                }

                var homeTeamDto = new TeamSummaryDto
                {
                    Id = effHomeId,
                    LegacyId = legacyHomeId,
                    Name = homeName,
                    ShortName = DeriveTla(homeLong ?? homeName, homeTla),
                    LogoUrl = BuildTeamLogoUrl(homeId),
                    RedCards = homeRedCards
                };

                var awayTeamDto = new TeamSummaryDto
                {
                    Id = effAwayId,
                    LegacyId = legacyAwayId,
                    Name = awayName,
                    ShortName = DeriveTla(awayLong ?? awayName, awayTla),
                    LogoUrl = BuildTeamLogoUrl(awayId),
                    RedCards = awayRedCards
                };

                // Index teams
                if (!string.IsNullOrWhiteSpace(homeId))
                {
                    var catHome = new V2CatalogTeamDto
                    {
                        Id = homeTeamDto.Id,
                        LegacyId = homeTeamDto.LegacyId ?? homeId,
                        Name = homeTeamDto.Name,
                        ShortName = homeTeamDto.ShortName,
                        Logo = homeTeamDto.LogoUrl,
                        LogoUrl = homeTeamDto.LogoUrl
                    };
                    _teamCatalog[homeId] = catHome;
                    _teamCatalog[homeTeamDto.Id] = catHome;
                }
                if (!string.IsNullOrWhiteSpace(awayId))
                {
                    var catAway = new V2CatalogTeamDto
                    {
                        Id = awayTeamDto.Id,
                        LegacyId = awayTeamDto.LegacyId ?? awayId,
                        Name = awayTeamDto.Name,
                        ShortName = awayTeamDto.ShortName,
                        Logo = awayTeamDto.LogoUrl,
                        LogoUrl = awayTeamDto.LogoUrl
                    };
                    _teamCatalog[awayId] = catAway;
                    _teamCatalog[awayTeamDto.Id] = catAway;
                }

                // Status parsing
                JsonElement statusElem = default;
                TryGetProp(match, "status", out statusElem);
                var (matchStatus, minute, addedTime, displayClock) = ParseLiveTime(statusElem);

                string? utcTimeStr = statusElem.ValueKind == JsonValueKind.Object && TryGetProp(statusElem, "utcTime", out var utcTimeElem)
                    ? utcTimeElem.GetString()
                    : null;
                DateTimeOffset kickoffTime = DateTimeOffset.TryParse(utcTimeStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedKickoff)
                    ? parsedKickoff
                    : DateTimeOffset.UtcNow;

                int hSc = homeScore ?? 0;
                int aSc = awayScore ?? 0;

                var matchDto = new TrayMatchDto
                {
                    Id = $"lt_{matchId}",
                    LegacyId = matchId,
                    Competition = compDto,
                    HomeTeam = homeTeamDto,
                    AwayTeam = awayTeamDto,
                    Score = new ScoreDto
                    {
                        Home = hSc,
                        Away = aSc,
                        Display = $"{hSc} - {aSc}"
                    },
                    State = matchStatus,
                    ElapsedMinute = minute ?? 0,
                    ExtraMinute = addedTime ?? 0,
                    KickoffTime = kickoffTime,
                    Trust = new V2TrustMetadataDto
                    {
                        Provider = TelemetryProviderName,
                        FreshnessState = "FRESH",
                        DataAgeSeconds = 0,
                        SourceObservedAt = DateTimeOffset.UtcNow
                    }
                };

                matches.Add(matchDto);

                // Real-time live match enrichment for goals and red cards
                bool isLive = IsLiveState(matchStatus);
                bool isFollowedOrPinned = false;
                if (!string.IsNullOrWhiteSpace(pinnedMatchId) && (string.Equals(matchId, pinnedMatchId, StringComparison.OrdinalIgnoreCase) || string.Equals($"lt_{matchId}", pinnedMatchId, StringComparison.OrdinalIgnoreCase)))
                {
                    isFollowedOrPinned = true;
                }
                else if (followedTeamIdentifiers.Count > 0 && (followedTeamIdentifiers.Contains(homeId) || followedTeamIdentifiers.Contains(effHomeId) || followedTeamIdentifiers.Contains(homeName) || followedTeamIdentifiers.Contains(awayId) || followedTeamIdentifiers.Contains(effAwayId) || followedTeamIdentifiers.Contains(awayName)))
                {
                    isFollowedOrPinned = true;
                }
                else if (followedLeagueIds.Count > 0 && (followedLeagueIds.Contains(effectiveCompId) || (int.TryParse(legacyCompId, out int legInt) && followedLeagueIds.Contains(legInt.ToString()))))
                {
                    isFollowedOrPinned = true;
                }

                bool hasPrevScore = _lastSeenMatchScoreState.TryGetValue(matchId, out var prevScoreState);
                bool scoreChanged = hasPrevScore && (hSc != prevScoreState.HomeScore || aSc != prevScoreState.AwayScore);
                bool redCardChanged = hasPrevScore && (homeRedCards != prevScoreState.HomeRedCards || awayRedCards != prevScoreState.AwayRedCards);
                bool hasGoalsOrCards = (hSc > 0 || aSc > 0 || homeRedCards > 0 || awayRedCards > 0);

                bool shouldEnrich = (isLive && (scoreChanged || redCardChanged)) ||
                                    (isFollowedOrPinned && isLive && hasGoalsOrCards);

                if (shouldEnrich)
                {
                    bool needFetch = true;
                    if (_matchDetailCache.TryGetValue(matchId, out var cachedDetail))
                    {
                        if (DateTimeOffset.UtcNow - cachedDetail.CachedAt < TimeSpan.FromSeconds(20) && !scoreChanged && !redCardChanged)
                        {
                            needFetch = false;
                            if (cachedDetail.Data.Events != null && cachedDetail.Data.Events.Count > 0)
                            {
                                matchDto.Events = cachedDetail.Data.Events;
                                matchDto.RecentEvents = cachedDetail.Data.Events.Select(e => new TrayEventDto
                                {
                                    Id = e.Id,
                                    Type = e.Type,
                                    Minute = e.Minute,
                                    ExtraMinute = e.ExtraMinute,
                                    TeamId = e.TeamId,
                                    PlayerId = e.PlayerId,
                                    PlayerName = e.PlayerName,
                                    AssistPlayerId = e.AssistPlayerId,
                                    AssistPlayerName = e.AssistPlayerName,
                                    Description = e.Description,
                                    Detail = e.Detail
                                }).ToList();
                            }
                        }
                    }

                    if (needFetch)
                    {
                        try
                        {
                            using var detailCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            detailCts.CancelAfter(TimeSpan.FromMilliseconds(2500));
                            var detail = await GetMatchDetailsAsync(matchId, detailCts.Token);
                            if (detail != null && detail.Events != null && detail.Events.Count > 0)
                            {
                                matchDto.Events = detail.Events;
                                matchDto.RecentEvents = detail.Events.Select(e => new TrayEventDto
                                {
                                    Id = e.Id,
                                    Type = e.Type,
                                    Minute = e.Minute,
                                    ExtraMinute = e.ExtraMinute,
                                    TeamId = e.TeamId,
                                    PlayerId = e.PlayerId,
                                    PlayerName = e.PlayerName,
                                    AssistPlayerId = e.AssistPlayerId,
                                    AssistPlayerName = e.AssistPlayerName,
                                    Description = e.Description,
                                    Detail = e.Detail
                                }).ToList();
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Debug("Telemetry", "LiveMatchEnrichmentFailed", $"Enriching live match {matchId} failed: {ex.Message}");
                        }
                    }
                }

                PruneCacheIfNeeded(_lastSeenMatchScoreState, 200);
                _lastSeenMatchScoreState[matchId] = (hSc, aSc, homeRedCards, awayRedCards);
            }
        }

        return matches;
    }

    #endregion

    #region 2. GetMatchDetailsAsync

    /// <summary>
    /// Fetches comprehensive match center details (header, events, commentary, statistics,
    /// lineups with 1000x600 5-anchor pitch grid, and embedded standings/leaders).
    /// </summary>
    public async Task<MatchDetailDto?> GetMatchDetailsAsync(string matchId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(matchId)) return null;

        string numericId = NumberMatchRegex.Match(matchId).Value;
        if (string.IsNullOrWhiteSpace(numericId)) return null;

        // In-memory cache check (10s for active, 60s for finished)
        if (_matchDetailCache.TryGetValue(numericId, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromSeconds(10))
        {
            return cached.Data;
        }

        try
        {
            string url = FormatEndpoint(
                OtaConfigService.Instance.CurrentManifest.Endpoints?.MatchDetailsUrlTemplate,
                "{matchId}",
                numericId,
                $"{UpstreamPrimaryBase}/matchDetails?matchId={numericId}");
            string? json = await FetchJsonResilientAsync(url, ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                url = FormatEndpoint(
                    OtaConfigService.Instance.CurrentManifest.Endpoints?.MatchDetailsFallbackUrlTemplate,
                    "{matchId}",
                    numericId,
                    $"{UpstreamFallbackBase}/matchDetails?matchId={numericId}");
                json = await FetchJsonResilientAsync(url, ct);
            }

            if (string.IsNullOrWhiteSpace(json)) return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!TryGetProp(root, "general", out var generalElem)) return null;

            string mId = TryGetProp(generalElem, "matchId", out var idProp) ? idProp.ToString() : numericId;
            string parentLeagueId = TryGetProp(generalElem, "parentLeagueId", out var plId) ? plId.ToString() : string.Empty;
            string leagueId = TryGetProp(generalElem, "leagueId", out var lId) ? lId.ToString() : string.Empty;
            string effectiveLeagueId = !string.IsNullOrWhiteSpace(parentLeagueId) && parentLeagueId != "0" ? parentLeagueId : leagueId;
            string compName = TryGetProp(generalElem, "leagueName", out var ln) ? ln.GetString() ?? "Competition" : "Competition";
            string? matchTimeUtc = TryGetProp(generalElem, "matchTimeUTCDate", out var mTime) ? mTime.GetString() : null;

            DateTimeOffset kickoffTime = DateTimeOffset.TryParse(matchTimeUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedKo)
                ? parsedKo
                : DateTimeOffset.UtcNow;

            // Header scoreboard & teams
            TryGetProp(root, "header", out var headerElem);
            var (homeTeam, awayTeam, homeScore, awayScore) = ParseHeaderTeams(headerElem, generalElem);

            if (!string.IsNullOrWhiteSpace(homeTeam.Name) && compName.EndsWith(homeTeam.Name, StringComparison.OrdinalIgnoreCase))
            {
                compName = compName[..^homeTeam.Name.Length].TrimEnd();
            }
            if (!string.IsNullOrWhiteSpace(awayTeam.Name) && compName.EndsWith(awayTeam.Name, StringComparison.OrdinalIgnoreCase))
            {
                compName = compName[..^awayTeam.Name.Length].TrimEnd();
            }

            JsonElement statusElem = default;
            if (headerElem.ValueKind == JsonValueKind.Object)
            {
                TryGetProp(headerElem, "status", out statusElem);
            }
            var (matchStatus, minute, addedTime, displayClock) = ParseLiveTime(statusElem);

            string effectiveCompId = effectiveLeagueId;
            string legacyCompId = effectiveLeagueId;
            if (int.TryParse(effectiveLeagueId, out int numEffLId) && UpstreamIdToCanonicalMap.TryGetValue(numEffLId, out var canonEffCompId))
            {
                if (IsValidCanonicalLeagueMatch(numEffLId, compName, null))
                {
                    effectiveCompId = canonEffCompId;
                }
            }

            var competitionSummary = new CompetitionSummaryDto
            {
                Id = effectiveCompId,
                LegacyId = legacyCompId,
                Name = compName,
                LogoUrl = $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/{effectiveLeagueId}.png"
            };

            // Venue and Referee
            string? venue = null;
            string? referee = null;
            TryGetProp(root, "content", out var contentElem);
            if (contentElem.ValueKind == JsonValueKind.Object)
            {
                if (TryGetProp(contentElem, "matchFacts", out var mfElem) &&
                    TryGetProp(mfElem, "infoBox", out var infoBoxElem))
                {
                    if (TryGetProp(infoBoxElem, "Stadium", out var stadiumElem) &&
                        TryGetProp(stadiumElem, "name", out var sName))
                    {
                        venue = sName.GetString();
                    }
                    if (TryGetProp(infoBoxElem, "Referee", out var refElem) &&
                        TryGetProp(refElem, "name", out var rName))
                    {
                        referee = rName.GetString();
                    }
                }
            }

            // Events (Goals, Cards, Subs, VAR)
            var events = ExtractEvents(contentElem, headerElem, homeTeam, awayTeam, mId);

            // Prematch detection: if match has not started or status is prematch/upcoming, suppress statistics & heatmaps
            bool started = statusElem.ValueKind == JsonValueKind.Object && TryGetProp(statusElem, "started", out var stElem) && stElem.ValueKind is JsonValueKind.True or JsonValueKind.False ? stElem.GetBoolean() : false;
            bool isPrematch = !started || matchStatus is "PREMATCH" or "UPCOMING" or "NOT_STARTED" or "SCHEDULED" or "TIMED";

            // Commentary & Whistle Markers
            var commentary = ExtractCommentary(contentElem, headerElem, events, homeTeam, awayTeam, mId, matchStatus, minute, homeScore, awayScore);

            // Statistics (Possession bar + comparison groups)
            var statistics = ExtractStatistics(contentElem, homeTeam.Id, awayTeam.Id, mId, homeScore, awayScore, isPrematch);

            // Lineups (5-anchor pitch grid assignment)
            var lineups = ExtractLineups(contentElem, homeTeam.Id, awayTeam.Id, mId);

            // Embedded Standings & Season Leaders
            var (standings, topScorers, topAssists, fixtures, knockoutStages) = await ResolveStandingsAndLeadersAsync(contentElem, effectiveLeagueId, homeTeam.Id, awayTeam.Id, ct);

            // Tactical Pitch Heatmaps
            var heatmaps = ExtractHeatmaps(contentElem, homeTeam.Id, awayTeam.Id, isPrematch);

            // Head-to-Head & Predictions
            var h2h = ExtractHeadToHead(contentElem, homeTeam, awayTeam, standings);
            if (h2h != null)
            {
                if (h2h.HomeFormMatches.Count == 0 && !string.IsNullOrWhiteSpace(homeTeam.LegacyId) && int.TryParse(homeTeam.LegacyId, out int hNumId))
                {
                    var fetchedHomeForm = await FetchTeamRecentFormMatchesAsync(hNumId, homeTeam.Name, ct);
                    if (fetchedHomeForm.Count > 0)
                    {
                        h2h.HomeFormMatches = fetchedHomeForm;
                        h2h.HomeForm = fetchedHomeForm.Select(m => new FormMatchDto { Result = m.Result, OpponentName = m.OpponentName, Score = m.ScoreText, DateUtc = m.DateText }).ToList();
                    }
                }

                if (h2h.AwayFormMatches.Count == 0 && !string.IsNullOrWhiteSpace(awayTeam.LegacyId) && int.TryParse(awayTeam.LegacyId, out int aNumId))
                {
                    var fetchedAwayForm = await FetchTeamRecentFormMatchesAsync(aNumId, awayTeam.Name, ct);
                    if (fetchedAwayForm.Count > 0)
                    {
                        h2h.AwayFormMatches = fetchedAwayForm;
                        h2h.AwayForm = fetchedAwayForm.Select(m => new FormMatchDto { Result = m.Result, OpponentName = m.OpponentName, Score = m.ScoreText, DateUtc = m.DateText }).ToList();
                    }
                }
            }

            // Upstream Insights & Player of the Match
            var upstreamInsights = ExtractUpstreamInsights(contentElem);
            var playerOfTheMatch = ExtractPlayerOfTheMatch(contentElem, lineups, homeTeam, awayTeam, matchStatus);

            var detailDto = new MatchDetailDto
            {
                Id = $"lt_{mId}",
                LegacyId = mId,
                Competition = competitionSummary,
                HomeTeam = homeTeam,
                HomeTeamId = homeTeam.Id,
                AwayTeam = awayTeam,
                AwayTeamId = awayTeam.Id,
                State = matchStatus,
                ElapsedMinute = minute ?? 0,
                ExtraMinute = addedTime ?? 0,
                Score = new ScoreDto
                {
                    Home = homeScore ?? 0,
                    Away = awayScore ?? 0,
                    Display = $"{homeScore ?? 0} - {awayScore ?? 0}"
                },
                Venue = venue,
                Referee = referee,
                KickoffTime = kickoffTime,
                Events = events,
                Commentary = commentary,
                Lineups = lineups,
                Statistics = statistics,
                Heatmaps = heatmaps,
                H2HAndPredictions = h2h,
                Standings = standings,
                TopScorers = topScorers,
                TopAssists = topAssists,
                Fixtures = fixtures,
                KnockoutStages = knockoutStages,
                PlayerOfTheMatch = playerOfTheMatch,
                Trust = new V2TrustMetadataDto
                {
                    Provider = TelemetryProviderName,
                    FreshnessState = "FRESH",
                    DataAgeSeconds = 0,
                    SourceObservedAt = DateTimeOffset.UtcNow
                }
            };

            // Synthesize broadcast-grade tactical recap narrative & key insights
            TacticalNarrativeSynthesizer.Synthesize(detailDto, upstreamInsights);

            PruneCacheIfNeeded(_matchDetailCache, 200);
            _matchDetailCache[numericId] = (DateTimeOffset.UtcNow, detailDto);
            return detailDto;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error("NativeTelemetry", "MatchDetailFetchFailed", $"Failed to fetch match details for {numericId}: {ex.Message}", ex: ex);
            return null;
        }
    }

    #endregion

    #region 3. GetCompetitionHubAsync & GetTeamHubAsync

    /// <summary>
    /// Resolves the league ID and fetches full standings, top scorers, top assists, fixtures, and knockout brackets.
    /// Results are cached for 15 minutes.
    /// </summary>
    public async Task<CompetitionHubDto?> GetCompetitionHubAsync(
        string competitionId,
        string? name = null,
        string? logoUrl = null,
        CancellationToken ct = default)
    {
        int? upstreamLeagueId = ResolveUpstreamLeagueId(competitionId, name, logoUrl);
        if (!upstreamLeagueId.HasValue)
        {
            upstreamLeagueId = await ResolveLeagueIdAsync(competitionId, ct);
        }
        if (!upstreamLeagueId.HasValue) return null;

        string cacheKey = upstreamLeagueId.Value.ToString();
        if (_hubCache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromMinutes(15))
        {
            return cached.Data;
        }

        try
        {
            int upstreamId = (upstreamLeagueId.Value == 10650 || upstreamLeagueId.Value == 191) ? 519 : upstreamLeagueId.Value;
            string url = FormatEndpoint(
                OtaConfigService.Instance.CurrentManifest.Endpoints?.LeagueHubUrlTemplate,
                "{leagueId}",
                upstreamId.ToString(),
                $"{UpstreamPrimaryBase}/leagues?id={upstreamId}");
            string? json = await FetchJsonResilientAsync(url, ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                url = FormatEndpoint(
                    OtaConfigService.Instance.CurrentManifest.Endpoints?.LeagueHubFallbackUrlTemplate,
                    "{leagueId}",
                    upstreamId.ToString(),
                    $"{UpstreamFallbackBase}/leagues?id={upstreamId}");
                json = await FetchJsonResilientAsync(url, ct);
            }
            if (string.IsNullOrWhiteSpace(json) && upstreamId != upstreamLeagueId.Value)
            {
                url = $"{UpstreamPrimaryBase}/leagues?id={upstreamLeagueId.Value}";
                json = await FetchJsonResilientAsync(url, ct);
            }

            if (string.IsNullOrWhiteSpace(json)) return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string compName = name ?? "Competition";
            string? countryCode = null;
            string? season = null;

            if (TryGetProp(root, "details", out var detailsElem))
            {
                if (TryGetProp(detailsElem, "name", out var nElem))
                {
                    var s = GetStringOrToString(nElem);
                    if (!string.IsNullOrWhiteSpace(s)) compName = s;
                }
                if (TryGetProp(detailsElem, "country", out var cElem))
                {
                    countryCode = GetStringOrToString(cElem);
                }
                if (TryGetProp(detailsElem, "selectedSeason", out var sElem))
                {
                    season = GetStringOrToString(sElem);
                }
            }

            if (upstreamLeagueId.Value == 519 || upstreamLeagueId.Value == 10650 || upstreamLeagueId.Value == 191 || (compName.Contains("Premier League", StringComparison.OrdinalIgnoreCase) && string.Equals(countryCode, "EGY", StringComparison.OrdinalIgnoreCase)))
            {
                compName = "Egyptian Premier League";
                countryCode = "EGY";
            }

            var standings = ParseStandingsFromLeagueDoc(root);
            var (topScorers, topAssists) = await ParseLeadersFromLeagueDocAsync(root, ct);
            var fixtures = ParseFixturesFromLeagueDoc(root);
            var knockoutStages = ParseKnockoutStagesFromLeagueDoc(root);

            int finalLeagueId = (upstreamLeagueId.Value == 10650 || upstreamLeagueId.Value == 191) ? 519 : upstreamLeagueId.Value;
            var hubDto = new CompetitionHubDto
            {
                CompetitionId = finalLeagueId.ToString(),
                CompetitionName = compName,
                CompetitionCode = countryCode,
                CompetitionLogoUrl = $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/leaguelogo/dark/{finalLeagueId}.png",
                CountryCode = countryCode,
                Season = season,
                Standings = standings,
                TopScorers = topScorers,
                TopAssists = topAssists,
                Fixtures = fixtures,
                KnockoutStages = knockoutStages
            };

            PruneCacheIfNeeded(_hubCache, 200);
            _hubCache[cacheKey] = (DateTimeOffset.UtcNow, hubDto);
            _hubCache[competitionId] = (DateTimeOffset.UtcNow, hubDto);
            _hubCache[finalLeagueId.ToString()] = (DateTimeOffset.UtcNow, hubDto);
            if (finalLeagueId == 519)
            {
                _hubCache["10650"] = (DateTimeOffset.UtcNow, hubDto);
                _hubCache["191"] = (DateTimeOffset.UtcNow, hubDto);
            }

            return hubDto;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error("NativeTelemetry", "HubFetchFailed", $"Failed to fetch competition hub for {competitionId}: {ex.Message}", ex: ex);
            return null;
        }
    }

    /// <summary>
    /// Resolves team identifier to upstream telemetry provider numeric team ID and fetches full details, stadium, coach,
    /// form, upcoming/past fixtures, squad players with stats, and primary league standings.
    /// Results are cached for 5 minutes.
    /// </summary>
    public async Task<TeamHubDto?> GetTeamHubAsync(
        string teamId,
        string? targetName = null,
        string? targetLogoUrl = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return null;

        int? upstreamTeamId = await ResolveTeamIdAsync(teamId, ct);
        if (!upstreamTeamId.HasValue) return null;

        string cacheKey = upstreamTeamId.Value.ToString();
        if (_teamHubCache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromMinutes(5))
        {
            return cached.Data;
        }

        try
        {
            string url = FormatEndpoint(
                OtaConfigService.Instance.CurrentManifest.Endpoints?.TeamHubUrlTemplate,
                "{teamId}",
                upstreamTeamId.Value.ToString(),
                $"{UpstreamPrimaryBase}/teams?id={upstreamTeamId.Value}");
            string? json = await FetchJsonResilientAsync(url, ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                url = FormatEndpoint(
                    OtaConfigService.Instance.CurrentManifest.Endpoints?.TeamHubFallbackUrlTemplate,
                    "{teamId}",
                    upstreamTeamId.Value.ToString(),
                    $"{UpstreamFallbackBase}/teams?id={upstreamTeamId.Value}");
                json = await FetchJsonResilientAsync(url, ct);
            }

            if (string.IsNullOrWhiteSpace(json)) return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string teamName = targetName ?? "Team";
            string shortName = teamName;
            string countryCode = "";
            string primaryLeagueId = "";
            string primaryLeagueName = "";
            string stadiumName = "";
            string coachName = "";

            if (TryGetProp(root, "details", out var detailsElem))
            {
                if (TryGetProp(detailsElem, "name", out var n) && !string.IsNullOrWhiteSpace(n.GetString())) teamName = n.GetString()!;
                if (TryGetProp(detailsElem, "shortName", out var sn) && !string.IsNullOrWhiteSpace(sn.GetString())) shortName = sn.GetString()!;
                if (TryGetProp(detailsElem, "country", out var cc) && !string.IsNullOrWhiteSpace(cc.GetString())) countryCode = cc.GetString()!;
                if (TryGetProp(detailsElem, "primaryLeagueId", out var plId)) primaryLeagueId = plId.ToString();
                if (TryGetProp(detailsElem, "primaryLeagueName", out var pln) && !string.IsNullOrWhiteSpace(pln.GetString())) primaryLeagueName = pln.GetString()!;

                var (nPlId, nPlName, _) = NormalizeCompetitionIdAndName(primaryLeagueId, primaryLeagueName, countryCode);
                primaryLeagueId = nPlId;
                primaryLeagueName = nPlName;

                if (TryGetProp(detailsElem, "sportsTeamJSONLD", out var stJld) &&
                    TryGetProp(stJld, "location", out var locElem) &&
                    TryGetProp(locElem, "name", out var stName) &&
                    !string.IsNullOrWhiteSpace(stName.GetString()))
                {
                    stadiumName = stName.GetString()!;
                }
            }

            // Overview for venue / stadium fallback
            if (string.IsNullOrWhiteSpace(stadiumName) && TryGetProp(root, "overview", out var ovElem))
            {
                if (TryGetProp(ovElem, "venue", out var vElem) &&
                    TryGetProp(vElem, "widget", out var wElem) &&
                    TryGetProp(wElem, "name", out var wn) &&
                    !string.IsNullOrWhiteSpace(wn.GetString()))
                {
                    stadiumName = wn.GetString()!;
                }
            }

            // Recent Form (last 5 W/D/L)
            var recentForm = new List<string>();
            if (TryGetProp(root, "overview", out var ovForForm) &&
                TryGetProp(ovForForm, "teamForm", out var formArr) &&
                formArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in formArr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (TryGetProp(item, "resultString", out var rs) && !string.IsNullOrWhiteSpace(rs.GetString()))
                    {
                        recentForm.Add(rs.GetString()!.ToUpperInvariant());
                    }
                    else if (TryGetProp(item, "result", out var rVal) && rVal.ValueKind == JsonValueKind.Number)
                    {
                        int r = rVal.GetInt32();
                        recentForm.Add(r > 0 ? "W" : (r == 0 ? "D" : "L"));
                    }
                }
            }

            // Squad Players & Coach
            var squadPlayers = new List<TeamSquadPlayerDto>();
            if (TryGetProp(root, "squad", out var squadElem))
            {
                JsonElement squadGroupsArr = default;
                if (squadElem.ValueKind == JsonValueKind.Array)
                {
                    squadGroupsArr = squadElem;
                }
                else if (squadElem.ValueKind == JsonValueKind.Object &&
                         TryGetProp(squadElem, "squad", out var innerArr) &&
                         innerArr.ValueKind == JsonValueKind.Array)
                {
                    squadGroupsArr = innerArr;
                }

                if (squadGroupsArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var group in squadGroupsArr.EnumerateArray())
                    {
                        if (group.ValueKind != JsonValueKind.Object) continue;
                        string title = TryGetProp(group, "title", out var titleProp) ? titleProp.GetString()?.ToLowerInvariant() ?? "" : "";
                        if (!TryGetProp(group, "members", out var membersArr) || membersArr.ValueKind != JsonValueKind.Array) continue;

                        if (title == "coach")
                        {
                            var firstCoach = membersArr.EnumerateArray().FirstOrDefault();
                            if (firstCoach.ValueKind == JsonValueKind.Object &&
                                TryGetProp(firstCoach, "name", out var cName) &&
                                !string.IsNullOrWhiteSpace(cName.GetString()))
                            {
                                coachName = cName.GetString()!;
                            }
                            continue;
                        }

                        string positionGroup = title switch
                        {
                            "keepers" => "Goalkeepers",
                            "defenders" => "Defenders",
                            "midfielders" => "Midfielders",
                            "attackers" => "Forwards",
                            _ => title.Length > 0 ? char.ToUpperInvariant(title[0]) + (title.Length > 1 ? title[1..] : "") : "Squad"
                        };

                        foreach (var member in membersArr.EnumerateArray())
                        {
                            if (member.ValueKind != JsonValueKind.Object) continue;
                            string mId = TryGetProp(member, "id", out var idElem) ? idElem.ToString() : "";
                            string mName = TryGetProp(member, "name", out var mnElem) ? mnElem.GetString() ?? "" : "";
                            if (string.IsNullOrWhiteSpace(mName)) continue;

                            int? shirtNum = TryGetProp(member, "shirtNumber", out var shElem) && shElem.ValueKind == JsonValueKind.Number ? shElem.GetInt32() : null;
                            string? mCountry = TryGetProp(member, "cname", out var cnameElem) ? cnameElem.GetString() :
                                              (TryGetProp(member, "ccode", out var ccElem) ? ccElem.GetString() : null);

                            double? rating = null;
                            if (TryGetProp(member, "rating", out var ratElem))
                            {
                                if (ratElem.ValueKind == JsonValueKind.Number && ratElem.TryGetDouble(out double rVal)) rating = rVal;
                                else if (ratElem.ValueKind == JsonValueKind.String && double.TryParse(ratElem.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double rParsed)) rating = rParsed;
                            }

                            int? goals = TryGetProp(member, "goals", out var gElem) && gElem.ValueKind == JsonValueKind.Number ? gElem.GetInt32() : null;
                            int? assists = TryGetProp(member, "assists", out var aElem) && aElem.ValueKind == JsonValueKind.Number ? aElem.GetInt32() : null;
                            int? ycards = TryGetProp(member, "ycards", out var ycElem) && ycElem.ValueKind == JsonValueKind.Number ? ycElem.GetInt32() : null;
                            int? rcards = TryGetProp(member, "rcards", out var rcElem) && rcElem.ValueKind == JsonValueKind.Number ? rcElem.GetInt32() : null;

                            squadPlayers.Add(new TeamSquadPlayerDto
                            {
                                Id = mId,
                                Name = mName,
                                PositionGroup = positionGroup,
                                ShirtNumber = shirtNum,
                                CountryCode = mCountry,
                                Rating = rating,
                                Goals = goals,
                                Assists = assists,
                                YellowCards = ycards,
                                RedCards = rcards
                            });
                        }
                    }
                }
            }

            // Fixtures & Results
            var upcomingFixtures = new List<CompetitionFixtureDto>();
            var recentResults = new List<CompetitionFixtureDto>();
            if (TryGetProp(root, "fixtures", out var fixElem) &&
                TryGetProp(fixElem, "allFixtures", out var allFixElem) &&
                TryGetProp(allFixElem, "fixtures", out var fixturesArr) &&
                fixturesArr.ValueKind == JsonValueKind.Array)
            {
                var allFixtures = new List<CompetitionFixtureDto>();
                foreach (var fix in fixturesArr.EnumerateArray())
                {
                    if (fix.ValueKind != JsonValueKind.Object) continue;
                    string fId = TryGetProp(fix, "id", out var fidElem) ? fidElem.ToString() : "";
                    if (string.IsNullOrWhiteSpace(fId)) continue;

                    string roundName = "";
                    string compId = "";
                    string compName = "";
                    if (TryGetProp(fix, "tournament", out var tElem))
                    {
                        if (TryGetProp(tElem, "leagueId", out var lidElem) && !string.IsNullOrWhiteSpace(lidElem.ToString()))
                        {
                            compId = lidElem.ToString();
                        }
                        else if (TryGetProp(tElem, "id", out var tidElem))
                        {
                            compId = tidElem.ToString();
                        }

                        if (TryGetProp(tElem, "name", out var tnElem) &&
                            !string.IsNullOrWhiteSpace(tnElem.GetString()))
                        {
                            roundName = tnElem.GetString()!;
                            compName = roundName;
                        }
                    }

                    var (normCompId, normCompName, normCompLogo) = NormalizeCompetitionIdAndName(compId, compName, countryCode);
                    compId = normCompId;
                    compName = normCompName;

                    string kickoffUtc = "";
                    bool isFinished = false;
                    bool isStarted = false;
                    string statusText = "";

                    if (TryGetProp(fix, "status", out var statElem))
                    {
                        if (TryGetProp(statElem, "utcTime", out var utcProp)) kickoffUtc = utcProp.GetString() ?? "";
                        if (TryGetProp(statElem, "finished", out var finProp)) isFinished = finProp.GetBoolean();
                        if (TryGetProp(statElem, "started", out var startProp)) isStarted = startProp.GetBoolean();
                        if (TryGetProp(statElem, "scoreStr", out var ssProp)) statusText = ssProp.GetString() ?? "";
                        else if (TryGetProp(statElem, "reason", out var reasProp) && TryGetProp(reasProp, "short", out var shProp)) statusText = shProp.GetString() ?? "";
                    }

                    string hId = "", hName = "";
                    int? hScore = null;
                    if (TryGetProp(fix, "home", out var hElem))
                    {
                        if (TryGetProp(hElem, "id", out var hid)) hId = hid.ToString();
                        if (TryGetProp(hElem, "name", out var hn)) hName = hn.GetString() ?? "";
                        if (TryGetProp(hElem, "score", out var hs) && hs.ValueKind == JsonValueKind.Number) hScore = hs.GetInt32();
                    }

                    string aId = "", aName = "";
                    int? aScore = null;
                    if (TryGetProp(fix, "away", out var aElem))
                    {
                        if (TryGetProp(aElem, "id", out var aid)) aId = aid.ToString();
                        if (TryGetProp(aElem, "name", out var an)) aName = an.GetString() ?? "";
                        if (TryGetProp(aElem, "score", out var ascore) && ascore.ValueKind == JsonValueKind.Number) aScore = ascore.GetInt32();
                    }

                    var fixtureDto = new CompetitionFixtureDto
                    {
                        Id = fId,
                        RoundName = roundName,
                        CompetitionId = compId,
                        CompetitionName = compName,
                        CompetitionLogo = normCompLogo ?? (!string.IsNullOrWhiteSpace(compId) ? BuildLeagueLogoUrl(compId) : null),
                        KickoffUtc = kickoffUtc,
                        HomeTeamId = hId,
                        HomeTeamName = hName,
                        HomeTeamShortName = hName,
                        HomeTeamLogo = !string.IsNullOrWhiteSpace(hId) ? BuildTeamLogoUrl(hId) : null,
                        AwayTeamId = aId,
                        AwayTeamName = aName,
                        AwayTeamShortName = aName,
                        AwayTeamLogo = !string.IsNullOrWhiteSpace(aId) ? BuildTeamLogoUrl(aId) : null,
                        HomeScore = hScore,
                        AwayScore = aScore,
                        StatusText = statusText,
                        IsFinished = isFinished,
                        IsStarted = isStarted,
                        IsLive = isStarted && !isFinished
                    };

                    allFixtures.Add(fixtureDto);
                }

                var past = allFixtures.Where(f => f.IsFinished).ToList();
                recentResults = past.AsEnumerable().Reverse().Take(8).ToList();
                upcomingFixtures = allFixtures.Where(f => !f.IsFinished).Take(8).ToList();
            }

            // Standings
            var standings = ParseStandingsFromLeagueDoc(root);

            var teamHubDto = new TeamHubDto
            {
                TeamId = teamId,
                TeamName = teamName,
                ShortName = shortName,
                TeamLogo = BuildTeamLogoUrl(upstreamTeamId.Value.ToString()),
                CountryCode = countryCode,
                PrimaryLeagueId = primaryLeagueId,
                PrimaryLeagueName = primaryLeagueName,
                PrimaryLeagueLogo = !string.IsNullOrWhiteSpace(primaryLeagueId) ? BuildLeagueLogoUrl(primaryLeagueId) : "",
                StadiumName = stadiumName,
                CoachName = coachName,
                RecentForm = recentForm,
                UpcomingFixtures = upcomingFixtures,
                RecentResults = recentResults,
                SquadPlayers = squadPlayers,
                Standings = standings
            };

            PruneCacheIfNeeded(_teamHubCache, 200);
            _teamHubCache[cacheKey] = (DateTimeOffset.UtcNow, teamHubDto);
            _teamHubCache[teamId] = (DateTimeOffset.UtcNow, teamHubDto);

            return teamHubDto;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error("NativeTelemetry", "TeamHubFetchFailed", $"Failed to fetch team hub for {teamId}: {ex.Message}", ex: ex);
            return null;
        }
    }

    /// <summary>
    /// Resolves competition/league slug or identifier to upstream telemetry provider numeric league ID,
    /// querying upstream search suggestions if not found locally.
    /// </summary>
    public async Task<int?> ResolveLeagueIdAsync(string leagueId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(leagueId)) return null;

        // 1. Direct local resolution
        int? resolved = ResolveUpstreamLeagueId(leagueId);
        if (resolved.HasValue && resolved.Value > 0) return resolved.Value;

        // 2. Strip common prefixes
        string stripped = CompPrefixRegex.Replace(leagueId.Trim().ToLowerInvariant(), "");
        if (int.TryParse(stripped, out int directNum) && directNum > 0) return directNum;

        if (CompetitionSlugToUpstreamId.TryGetValue(stripped, out int slugId)) return slugId;
        if (CanonicalCompetitionMap.TryGetValue(stripped, out int canonId)) return canonId;

        // 3. Search in-memory catalog
        foreach (var comp in _compCatalog.Values)
        {
            if (string.Equals(comp.Id, stripped, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(comp.Name, stripped, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(comp.Code) && string.Equals(comp.Code, stripped, StringComparison.OrdinalIgnoreCase)))
            {
                if (int.TryParse(comp.Id, out int compNum) && compNum > 0) return compNum;
                int? catResolve = ResolveUpstreamLeagueId(comp.Id, comp.Name, comp.LogoUrl);
                if (catResolve.HasValue) return catResolve.Value;
            }
        }

        // 4. Fallback upstream suggestion search
        try
        {
            var searchResults = await SearchCompetitionsAsync(stripped, limit: 5, ct: ct);
            if (searchResults != null && searchResults.Count > 0)
            {
                foreach (var match in searchResults)
                {
                    if (int.TryParse(match.Id, out int sId) && sId > 0) return sId;
                    int? sResolve = ResolveUpstreamLeagueId(match.Id, match.Name, match.LogoUrl);
                    if (sResolve.HasValue) return sResolve.Value;
                }
            }
        }
        catch
        {
            // Non-blocking
        }

        return null;
    }

    /// <summary>
    /// Resolves team slug or identifier to upstream telemetry provider numeric team ID,
    /// querying upstream search suggestions if not found locally.
    /// </summary>
    public async Task<int?> ResolveTeamIdAsync(string teamId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return null;

        string raw = teamId.Trim();
        string lower = raw.ToLowerInvariant();

        // 0. Direct numeric check
        if (int.TryParse(raw, out int directNum) && directNum > 0) return directNum;

        // 1. Strip common prefixes
        string stripped = TeamPrefixRegex.Replace(lower, "");
        if (int.TryParse(stripped, out int strippedNum) && strippedNum > 0) return strippedNum;

        // 2. Check canonical mappings
        if (CanonicalTeamMap.TryGetValue(lower, out int canonLower)) return canonLower;
        if (CanonicalTeamMap.TryGetValue(raw, out int canonRaw)) return canonRaw;
        if (CanonicalTeamMap.TryGetValue(stripped, out int canonStripped)) return canonStripped;

        // 3. Check BuiltInTopClubs
        foreach (var club in BuiltInTopClubs)
        {
            if (string.Equals(club.Id, raw, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(club.Name, raw, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(club.ShortName, raw, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(club.Name.Replace(" ", "-"), raw, StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(club.Id, out int clubNum) && clubNum > 0) return clubNum;
            }
        }

        // 4. Check in-memory catalog
        if (_teamCatalog.TryGetValue(raw, out var catalogTeam) || _teamCatalog.TryGetValue(stripped, out catalogTeam))
        {
            if (int.TryParse(catalogTeam.Id, out int catId) && catId > 0) return catId;
        }

        foreach (var team in _teamCatalog.Values)
        {
            if (string.Equals(team.Name, raw, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(team.Name.Replace(" ", "-"), raw, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(team.ShortName) && string.Equals(team.ShortName, raw, StringComparison.OrdinalIgnoreCase)))
            {
                if (int.TryParse(team.Id, out int tId) && tId > 0) return tId;
            }
        }

        // 5. Fallback upstream search suggestion
        try
        {
            string cleanSearch = stripped.Replace("-", " ").Replace("_", " ");
            var searchResults = await SearchTeamsAsync(cleanSearch, limit: 5, ct: ct);
            if (searchResults != null && searchResults.Count > 0)
            {
                foreach (var match in searchResults)
                {
                    if (int.TryParse(match.Id, out int sId) && sId > 0) return sId;
                }
            }
        }
        catch
        {
            // Non-blocking
        }

        // 6. Final fallback: extract trailing digits if present
        if (!raw.StartsWith("ft_", StringComparison.OrdinalIgnoreCase))
        {
            string digitsOnly = DigitsOnlyRegex.Replace(raw, "");
            if (digitsOnly.Length > 0 && int.TryParse(digitsOnly, out int trailingNum) && trailingNum > 0)
            {
                return trailingNum;
            }
        }

        return null;
    }

    #endregion

    #region 4. SearchTeamsAsync & SearchCompetitionsAsync

    public sealed class AllLeaguesItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Ccode { get; set; }
        public string? Country { get; set; }
    }

    public sealed class AllLeaguesCountry
    {
        public string Ccode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<AllLeaguesItem> Leagues { get; set; } = new();
    }

    public sealed class AllLeaguesCatalog
    {
        public List<AllLeaguesItem> International { get; set; } = new();
        public List<AllLeaguesCountry> Countries { get; set; } = new();
        public Dictionary<string, AllLeaguesCountry> ByCountryCode { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, AllLeaguesCountry> ByCountryName { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, AllLeaguesItem> AllLeaguesById { get; set; } = new();
    }

    private static AllLeaguesCatalog? _allLeaguesCatalog;
    private static readonly SemaphoreSlim _allLeaguesLock = new(1, 1);

    private static readonly Dictionary<string, string> CountryAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["uk"] = "ENG", ["great britain"] = "ENG", ["britain"] = "ENG", ["united kingdom"] = "ENG", ["england"] = "ENG", ["eng"] = "ENG", ["gb"] = "ENG",
        ["usa"] = "USA", ["us"] = "USA", ["united states"] = "USA", ["america"] = "USA",
        ["ksa"] = "KSA", ["saudi"] = "KSA", ["saudi arabia"] = "KSA",
        ["uae"] = "UAE", ["united arab emirates"] = "UAE",
        ["morocco"] = "MAR", ["maroc"] = "MAR", ["mar"] = "MAR",
        ["turkey"] = "TUR", ["türkiye"] = "TUR", ["tur"] = "TUR",
        ["egy"] = "EGY", ["egypt"] = "EGY", ["eg"] = "EGY",
        ["bra"] = "BRA", ["brazil"] = "BRA", ["brasil"] = "BRA", ["br"] = "BRA",
        ["arg"] = "ARG", ["argentina"] = "ARG", ["ar"] = "ARG",
        ["ger"] = "GER", ["germany"] = "GER", ["deu"] = "GER", ["de"] = "GER",
        ["fra"] = "FRA", ["france"] = "FRA", ["fr"] = "FRA",
        ["esp"] = "ESP", ["spain"] = "ESP", ["es"] = "ESP",
        ["ita"] = "ITA", ["italy"] = "ITA", ["it"] = "ITA",
        ["ned"] = "NED", ["netherlands"] = "NED", ["holland"] = "NED", ["nld"] = "NED", ["nl"] = "NED",
        ["por"] = "POR", ["portugal"] = "POR", ["pt"] = "POR",
        ["irn"] = "IRN", ["iran"] = "IRN", ["ir"] = "IRN",
        ["tun"] = "TUN", ["tunisia"] = "TUN",
        ["alg"] = "ALG", ["algeria"] = "ALG",
        ["qat"] = "QAT", ["qatar"] = "QAT",
        ["bel"] = "BEL", ["belgium"] = "BEL"
    };

    private static readonly Dictionary<string, (string LeagueId, string CountryName, string CountryCode)> CountryToLeagueMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "egy", ("519", "Egypt", "EGY") },
        { "egypt", ("519", "Egypt", "EGY") },
        { "esp", ("87", "Spain", "ESP") },
        { "spain", ("87", "Spain", "ESP") },
        { "eng", ("47", "England", "ENG") },
        { "england", ("47", "England", "ENG") },
        { "ger", ("54", "Germany", "GER") },
        { "germany", ("54", "Germany", "GER") },
        { "deu", ("54", "Germany", "GER") },
        { "ita", ("55", "Italy", "ITA") },
        { "italy", ("55", "Italy", "ITA") },
        { "fra", ("53", "France", "FRA") },
        { "france", ("53", "France", "FRA") },
        { "ksa", ("536", "Saudi Arabia", "KSA") },
        { "saudi", ("536", "Saudi Arabia", "KSA") },
        { "saudi arabia", ("536", "Saudi Arabia", "KSA") },
        { "por", ("61", "Portugal", "POR") },
        { "portugal", ("61", "Portugal", "POR") },
        { "ned", ("57", "Netherlands", "NED") },
        { "nld", ("57", "Netherlands", "NED") },
        { "netherlands", ("57", "Netherlands", "NED") },
        { "bra", ("268", "Brazil", "BRA") },
        { "brazil", ("268", "Brazil", "BRA") }
    };

    public static bool TryResolveCountry(string query, AllLeaguesCatalog catalog, out AllLeaguesCountry? country)
    {
        string q = query.Trim();
        if (catalog.ByCountryCode.TryGetValue(q, out country)) return true;
        if (catalog.ByCountryName.TryGetValue(q, out country)) return true;

        if (CountryAliases.TryGetValue(q, out var mappedCode))
        {
            if (catalog.ByCountryCode.TryGetValue(mappedCode, out country)) return true;
            if (catalog.ByCountryName.TryGetValue(mappedCode, out country)) return true;
        }

        country = null;
        return false;
    }

    private static AllLeaguesItem? ParseAllLeaguesItem(JsonElement elem, string defaultCcode, string defaultCountry)
    {
        if (elem.ValueKind != JsonValueKind.Object) return null;
        int? id = GetIntProp(elem, "id");
        if (!id.HasValue || id.Value <= 0) return null;

        string name = GetStringProp(elem, "name", "") ?? "";
        if (string.IsNullOrWhiteSpace(name)) return null;

        string? ccode = GetStringProp(elem, "ccode", defaultCcode);
        if (string.IsNullOrWhiteSpace(ccode)) ccode = defaultCcode;

        if (id.Value == 519 || (string.Equals(ccode, "EGY", StringComparison.OrdinalIgnoreCase) && name.Contains("Premier League", StringComparison.OrdinalIgnoreCase)))
        {
            name = "Egyptian Premier League";
            ccode = "EGY";
        }

        return new AllLeaguesItem
        {
            Id = id.Value,
            Name = name,
            Ccode = ccode,
            Country = defaultCountry
        };
    }

    private static void PopulateFallbackAllLeagues(AllLeaguesCatalog catalog)
    {
        var intLeagues = new (int Id, string Name)[]
        {
            (77, "FIFA World Cup"),
            (42, "UEFA Champions League"),
            (73, "UEFA Europa League"),
            (10216, "UEFA Conference League"),
            (50, "UEFA European Championship"),
            (289, "Africa Cup of Nations"),
            (44, "Copa América"),
            (78, "FIFA Club World Cup"),
            (526, "CAF Champions League"),
            (527, "CAF Confederation Cup"),
            (525, "AFC Champions League Elite"),
            (45, "Copa Libertadores"),
            (9806, "UEFA Nations League")
        };

        foreach (var (id, name) in intLeagues)
        {
            var item = new AllLeaguesItem { Id = id, Name = name, Ccode = "INT", Country = "International" };
            catalog.International.Add(item);
            catalog.AllLeaguesById[id] = item;
        }

        var fallbackCountries = new (string Ccode, string Name, (int Id, string Name)[] Leagues)[]
        {
            ("ENG", "England", new[] { (47, "Premier League"), (132, "FA Cup"), (133, "EFL Cup"), (48, "Championship") }),
            ("ESP", "Spain", new[] { (87, "LaLiga"), (138, "Copa del Rey") }),
            ("ITA", "Italy", new[] { (55, "Serie A"), (141, "Coppa Italia") }),
            ("GER", "Germany", new[] { (54, "Bundesliga"), (209, "DFB-Pokal") }),
            ("FRA", "France", new[] { (53, "Ligue 1"), (134, "Coupe de France") }),
            ("EGY", "Egypt", new[] { (519, "Egyptian Premier League"), (9941, "Egypt Cup"), (10270, "League Cup") }),
            ("KSA", "Saudi Arabia", new[] { (536, "Saudi Pro League"), (687, "King's Cup") }),
            ("MAR", "Morocco", new[] { (530, "Botola Pro"), (11840, "Throne Cup") }),
            ("TUR", "Turkey", new[] { (71, "Süper Lig") }),
            ("BRA", "Brazil", new[] { (268, "Brasileirão") }),
            ("ARG", "Argentina", new[] { (112, "Liga Profesional") }),
            ("POR", "Portugal", new[] { (61, "Liga Portugal") }),
            ("NED", "Netherlands", new[] { (57, "Eredivisie") }),
            ("USA", "United States", new[] { (130, "Major League Soccer") }),
            ("TUN", "Tunisia", new[] { (535, "Tunisian Ligue 1") }),
            ("ALG", "Algeria", new[] { (516, "Algerian Ligue 1") }),
            ("UAE", "United Arab Emirates", new[] { (538, "UAE Pro League") }),
            ("QAT", "Qatar", new[] { (533, "Qatar Stars League") }),
            ("IRN", "Iran", new[] { (295, "Persian Gulf Pro League") }),
            ("BEL", "Belgium", new[] { (40, "Belgian Pro League") })
        };

        foreach (var fc in fallbackCountries)
        {
            var country = new AllLeaguesCountry { Ccode = fc.Ccode, Name = fc.Name };
            foreach (var lg in fc.Leagues)
            {
                var item = new AllLeaguesItem { Id = lg.Id, Name = lg.Name, Ccode = fc.Ccode, Country = fc.Name };
                country.Leagues.Add(item);
                catalog.AllLeaguesById[lg.Id] = item;
            }
            catalog.Countries.Add(country);
            catalog.ByCountryCode[fc.Ccode] = country;
            catalog.ByCountryName[fc.Name] = country;
        }
    }

    public async Task<AllLeaguesCatalog> GetAllLeaguesCatalogAsync(CancellationToken ct = default)
    {
        if (_allLeaguesCatalog != null) return _allLeaguesCatalog;

        await _allLeaguesLock.WaitAsync(ct);
        try
        {
            if (_allLeaguesCatalog != null) return _allLeaguesCatalog;

            var catalog = new AllLeaguesCatalog();
            bool loaded = false;

            try
            {
                string url = $"{UpstreamPrimaryBase}/allLeagues";
                string? json = await FetchJsonResilientAsync(url, ct, timeoutSeconds: 5);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (TryGetProp(root, "international", out var intElem))
                    {
                        if (intElem.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var group in intElem.EnumerateArray())
                            {
                                if (TryGetProp(group, "leagues", out var leaguesArr) && leaguesArr.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var lg in leaguesArr.EnumerateArray())
                                    {
                                        var item = ParseAllLeaguesItem(lg, "INT", "International");
                                        if (item != null)
                                        {
                                            catalog.International.Add(item);
                                            catalog.AllLeaguesById[item.Id] = item;
                                        }
                                    }
                                }
                            }
                        }
                        else if (intElem.ValueKind == JsonValueKind.Object)
                        {
                            if (TryGetProp(intElem, "leagues", out var leaguesArr) && leaguesArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var lg in leaguesArr.EnumerateArray())
                                {
                                    var item = ParseAllLeaguesItem(lg, "INT", "International");
                                    if (item != null)
                                    {
                                        catalog.International.Add(item);
                                        catalog.AllLeaguesById[item.Id] = item;
                                    }
                                }
                            }
                        }
                    }

                    if (TryGetProp(root, "countries", out var countriesElem) && countriesElem.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var cElem in countriesElem.EnumerateArray())
                        {
                            string ccode = GetStringProp(cElem, "ccode", "") ?? "";
                            string cname = GetStringProp(cElem, "name", "") ?? "";
                            if (string.IsNullOrWhiteSpace(ccode) && string.IsNullOrWhiteSpace(cname)) continue;

                            var country = new AllLeaguesCountry
                            {
                                Ccode = ccode,
                                Name = cname
                            };

                            if (TryGetProp(cElem, "leagues", out var leaguesArr) && leaguesArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var lg in leaguesArr.EnumerateArray())
                                {
                                    var item = ParseAllLeaguesItem(lg, ccode, cname);
                                    if (item != null)
                                    {
                                        country.Leagues.Add(item);
                                        catalog.AllLeaguesById[item.Id] = item;
                                    }
                                }
                            }

                            catalog.Countries.Add(country);
                            if (!string.IsNullOrWhiteSpace(ccode))
                            {
                                catalog.ByCountryCode[ccode] = country;
                            }
                            if (!string.IsNullOrWhiteSpace(cname))
                            {
                                catalog.ByCountryName[cname] = country;
                            }
                        }
                    }

                    loaded = catalog.International.Count > 0 || catalog.Countries.Count > 0;
                }
            }
            catch
            {
                // Fallback to curated catalog
            }

            if (!loaded)
            {
                PopulateFallbackAllLeagues(catalog);
            }

            _allLeaguesCatalog = catalog;
            return catalog;
        }
        finally
        {
            _allLeaguesLock.Release();
        }
    }

    /// <summary>
    /// Searches teams across memory index, curated dictionary, country leagues, and upstream suggestion API.
    /// Strictly deduplicated by team ID and normalized name.
    /// </summary>
    public async Task<List<V2CatalogTeamDto>> SearchTeamsAsync(string query, int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<V2CatalogTeamDto>();

        string q = query.Trim();
        var catalog = await GetAllLeaguesCatalogAsync(ct);
        var candidates = new List<V2CatalogTeamDto>();

        // 1. Country-specific league table clubs lookup via allLeagues
        bool isCountrySearch = TryResolveCountry(q, catalog, out var countryInfo) && countryInfo != null;
        if (isCountrySearch && countryInfo != null)
        {
            int? topLeagueId = null;
            if (countryInfo.Leagues.Count > 0)
            {
                topLeagueId = countryInfo.Leagues[0].Id;
            }
            else if (CountryToLeagueMap.TryGetValue(q, out var cInfo))
            {
                if (int.TryParse(cInfo.LeagueId, out int parsedLg)) topLeagueId = parsedLg;
            }

            if (topLeagueId.HasValue)
            {
                try
                {
                    var hub = await GetCompetitionHubAsync(topLeagueId.Value.ToString(), ct: ct);
                    if (hub?.Standings != null && hub.Standings.Count > 0)
                    {
                        foreach (var row in hub.Standings)
                        {
                            if (!string.IsNullOrWhiteSpace(row.TeamId) && !string.IsNullOrWhiteSpace(row.TeamName))
                            {
                                candidates.Add(new V2CatalogTeamDto
                                {
                                    Id = row.TeamId,
                                    LegacyId = row.TeamId,
                                    Name = row.TeamName,
                                    ShortName = row.TeamShortName ?? DeriveTla(row.TeamName),
                                    Country = countryInfo.Name,
                                    Logo = row.TeamLogoUrl ?? BuildTeamLogoUrl(row.TeamId),
                                    LogoUrl = row.TeamLogoUrl ?? BuildTeamLogoUrl(row.TeamId)
                                });
                            }
                        }
                    }
                }
                catch
                {
                    // Non-blocking
                }
            }

            // Also include any BuiltInTopClubs matching this country
            foreach (var club in BuiltInTopClubs)
            {
                if (string.Equals(club.Country, countryInfo.Ccode, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(club.Country, countryInfo.Name, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(new V2CatalogTeamDto
                    {
                        Id = club.Id,
                        LegacyId = club.Id,
                        Name = club.Name,
                        ShortName = club.ShortName,
                        Country = countryInfo.Name,
                        Logo = club.LogoUrl,
                        LogoUrl = club.LogoUrl
                    });
                }
            }
        }
        else if (string.Equals(q, "IRN", StringComparison.OrdinalIgnoreCase) || string.Equals(q, "Iran", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(new V2CatalogTeamDto { Id = "5871", LegacyId = "5871", Name = "Persepolis", ShortName = "PER", Country = "Iran", Logo = BuildTeamLogoUrl("5871"), LogoUrl = BuildTeamLogoUrl("5871") });
            candidates.Add(new V2CatalogTeamDto { Id = "5870", LegacyId = "5870", Name = "Esteghlal", ShortName = "EST", Country = "Iran", Logo = BuildTeamLogoUrl("5870"), LogoUrl = BuildTeamLogoUrl("5870") });
        }

        // 2. Search in-memory catalog
        foreach (var team in _teamCatalog.Values)
        {
            if (team.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(team.ShortName) && team.ShortName.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(team.Country) && (string.Equals(team.Country, q, StringComparison.OrdinalIgnoreCase) || team.Country.Contains(q, StringComparison.OrdinalIgnoreCase))))
            {
                candidates.Add(team);
            }
        }

        // 3. Upstream suggestion fallback if not reached limit
        if (candidates.Count < limit)
        {
            try
            {
                string url = FormatEndpoint(
                    OtaConfigService.Instance.CurrentManifest.Endpoints?.SearchSuggestUrlTemplate,
                    "{term}",
                    Uri.EscapeDataString(q),
                    $"{UpstreamPrimaryBase}/search/suggest?term={Uri.EscapeDataString(q)}");
                string? json = await FetchJsonResilientAsync(url, ct, timeoutSeconds: 3);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    var suggestTeams = new List<JsonElement>();

                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var section in doc.RootElement.EnumerateArray())
                        {
                            if (TryGetProp(section, "suggestions", out var sArr) && sArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in sArr.EnumerateArray())
                                {
                                    if (item.ValueKind == JsonValueKind.Object &&
                                        TryGetProp(item, "type", out var tProp) &&
                                        string.Equals(tProp.GetString(), "team", StringComparison.OrdinalIgnoreCase))
                                    {
                                        suggestTeams.Add(item);
                                    }
                                }
                            }
                        }
                    }
                    else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (TryGetProp(doc.RootElement, "team", out var teamArr) && teamArr.ValueKind == JsonValueKind.Array)
                        {
                            suggestTeams.AddRange(teamArr.EnumerateArray());
                        }
                    }

                    foreach (var item in suggestTeams)
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        if (!TryGetProp(item, "id", out var idElem)) continue;
                        string id = idElem.ToString();
                        string name = TryGetProp(item, "name", out var n) ? n.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        string country = TryGetProp(item, "country", out var cProp) ? cProp.GetString() ?? "" :
                                         (TryGetProp(item, "ccode", out var ccProp) ? ccProp.GetString() ?? "" : "");
                        if (string.IsNullOrWhiteSpace(country))
                        {
                            if (name.Contains("Egypt", StringComparison.OrdinalIgnoreCase) || string.Equals(q, "EGY", StringComparison.OrdinalIgnoreCase)) country = "Egypt";
                            else if (name.Contains("Iran", StringComparison.OrdinalIgnoreCase) || string.Equals(q, "IRN", StringComparison.OrdinalIgnoreCase)) country = "Iran";
                        }

                        string logo = BuildTeamLogoUrl(id);
                        candidates.Add(new V2CatalogTeamDto
                        {
                            Id = id,
                            LegacyId = id,
                            Name = name,
                            ShortName = DeriveTla(name),
                            Country = country,
                            Logo = logo,
                            LogoUrl = logo
                        });
                        if (candidates.Count >= limit * 2) break;
                    }
                }
            }
            catch
            {
                // Non-blocking fallback
            }
        }

        // 4. Strict Deduplication by numeric team ID and normalized name
        var results = new List<V2CatalogTeamDto>();
        var seenTeamIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTeamNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in candidates)
        {
            string id = (t.Id ?? "").Trim();
            string name = (t.Name ?? "").Trim();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;

            if (!seenTeamIds.Add(id)) continue;
            if (!seenTeamNames.Add(name)) continue;

            results.Add(t);
            if (!isCountrySearch && results.Count >= limit) break;
        }

        return results;
    }

    /// <summary>
    /// Searches competitions across memory index, INT/allLeagues catalog, country codes/names, and upstream suggestion API.
    /// Strictly deduplicated by numeric upstream telemetry provider League ID and normalized (Name, Country) key.
    /// </summary>
    public async Task<List<V2CatalogCompetitionDto>> SearchCompetitionsAsync(string query, int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<V2CatalogCompetitionDto>();

        string q = query.Trim();
        var catalog = await GetAllLeaguesCatalogAsync(ct);
        var candidates = new List<V2CatalogCompetitionDto>();

        // 1. Check INT / International search
        if (string.Equals(q, "INT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(q, "International", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var item in catalog.International)
            {
                string idStr = item.Id.ToString();
                candidates.Add(new V2CatalogCompetitionDto
                {
                    Id = idStr,
                    LegacyId = idStr,
                    Name = item.Name,
                    Country = "INT",
                    Code = "INT",
                    Logo = BuildLeagueLogoUrl(idStr),
                    LogoUrl = BuildLeagueLogoUrl(idStr)
                });
            }
        }

        // 2. Check Country search (e.g. "EGY", "Egypt", "Spain", "ESP", "MAR", "Morocco", "TUR", "Turkey", "BRA", "Brazil")
        if (TryResolveCountry(q, catalog, out var countryMatch) && countryMatch != null)
        {
            foreach (var league in countryMatch.Leagues)
            {
                string idStr = league.Id.ToString();
                string name = league.Name;
                if (league.Id == 519 || (countryMatch.Ccode == "EGY" && name.Contains("Premier League", StringComparison.OrdinalIgnoreCase)))
                {
                    name = "Egyptian Premier League";
                }

                candidates.Add(new V2CatalogCompetitionDto
                {
                    Id = idStr,
                    LegacyId = idStr,
                    Name = name,
                    Country = countryMatch.Ccode,
                    Code = countryMatch.Ccode,
                    Logo = BuildLeagueLogoUrl(idStr),
                    LogoUrl = BuildLeagueLogoUrl(idStr)
                });
            }
        }

        // 3. Built-in top competitions & catalog text search
        foreach (var comp in _compCatalog.Values)
        {
            if (comp.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(comp.Code) && comp.Code.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(comp.Country) && (string.Equals(comp.Country, q, StringComparison.OrdinalIgnoreCase) || comp.Country.Contains(q, StringComparison.OrdinalIgnoreCase))))
            {
                candidates.Add(comp);
            }
        }

        foreach (var league in catalog.AllLeaguesById.Values)
        {
            if (league.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                string idStr = league.Id.ToString();
                string ccode = league.Ccode ?? "";
                string name = league.Name;
                if (league.Id == 519) name = "Egyptian Premier League";

                candidates.Add(new V2CatalogCompetitionDto
                {
                    Id = idStr,
                    LegacyId = idStr,
                    Name = name,
                    Country = ccode,
                    Code = ccode,
                    Logo = BuildLeagueLogoUrl(idStr),
                    LogoUrl = BuildLeagueLogoUrl(idStr)
                });
            }
        }

        // 4. Upstream suggestion fallback if not enough candidates
        if (candidates.Count < limit * 2)
        {
            try
            {
                string url = FormatEndpoint(
                    OtaConfigService.Instance.CurrentManifest.Endpoints?.SearchSuggestUrlTemplate,
                    "{term}",
                    Uri.EscapeDataString(q),
                    $"{UpstreamPrimaryBase}/search/suggest?term={Uri.EscapeDataString(q)}");
                string? json = await FetchJsonResilientAsync(url, ct, timeoutSeconds: 3);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    var suggestLeagues = new List<JsonElement>();

                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var section in doc.RootElement.EnumerateArray())
                        {
                            if (TryGetProp(section, "suggestions", out var sArr) && sArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in sArr.EnumerateArray())
                                {
                                    if (item.ValueKind == JsonValueKind.Object &&
                                        TryGetProp(item, "type", out var tProp) &&
                                        string.Equals(tProp.GetString(), "league", StringComparison.OrdinalIgnoreCase))
                                    {
                                        suggestLeagues.Add(item);
                                    }
                                }
                            }
                        }
                    }
                    else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (TryGetProp(doc.RootElement, "league", out var leagueArr) && leagueArr.ValueKind == JsonValueKind.Array)
                        {
                            suggestLeagues.AddRange(leagueArr.EnumerateArray());
                        }
                    }

                    foreach (var item in suggestLeagues)
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        if (!TryGetProp(item, "id", out var idElem)) continue;
                        string id = idElem.ToString();
                        string name = TryGetProp(item, "name", out var n) ? n.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        string country = TryGetProp(item, "ccode", out var ccProp) ? ccProp.GetString() ?? "" :
                                         (TryGetProp(item, "country", out var cProp) ? cProp.GetString() ?? "" : "");

                        candidates.Add(new V2CatalogCompetitionDto
                        {
                            Id = id,
                            LegacyId = id,
                            Name = name,
                            Country = country,
                            Code = country,
                            Logo = BuildLeagueLogoUrl(id),
                            LogoUrl = BuildLeagueLogoUrl(id)
                        });
                    }
                }
            }
            catch
            {
                // Non-blocking
            }
        }

        // 5. Strict Deduplication by numeric upstream telemetry provider ID & normalized (Name, Country)
        var results = new List<V2CatalogCompetitionDto>();
        var seenUpstreamIds = new HashSet<int>();
        var seenNameCountry = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in candidates)
        {
            string rawId = c.Id ?? "";
            string name = c.Name ?? "";
            string country = c.Country ?? "";

            int? upstreamId = ResolveUpstreamLeagueId(rawId, name, c.LogoUrl, country);
            if (upstreamId.HasValue)
            {
                if (!seenUpstreamIds.Add(upstreamId.Value)) continue;

                string canonicalId = upstreamId.Value.ToString();
                string resolvedName = name;
                string resolvedCountry = country;

                if (upstreamId.Value == 519)
                {
                    resolvedName = "Egyptian Premier League";
                    resolvedCountry = "EGY";
                }
                else if (upstreamId.Value == 47)
                {
                    resolvedName = "Premier League";
                    resolvedCountry = "ENG";
                }
                else if (upstreamId.Value == 53)
                {
                    resolvedName = "Ligue 1";
                    resolvedCountry = "FRA";
                }
                else if (upstreamId.Value == 87)
                {
                    resolvedName = "LaLiga";
                    resolvedCountry = "ESP";
                }

                string nameCountryKey = $"{resolvedName.Trim()}|{resolvedCountry.Trim()}";
                if (!seenNameCountry.Add(nameCountryKey)) continue;

                results.Add(new V2CatalogCompetitionDto
                {
                    Id = canonicalId,
                    LegacyId = canonicalId,
                    Name = resolvedName,
                    Country = resolvedCountry,
                    Code = resolvedCountry,
                    Logo = BuildLeagueLogoUrl(canonicalId),
                    LogoUrl = BuildLeagueLogoUrl(canonicalId)
                });
            }
            else
            {
                string normKey = $"{rawId}|{name.Trim()}|{country.Trim()}";
                if (!seenNameCountry.Add(normKey)) continue;

                results.Add(c);
            }

            if (results.Count >= limit) break;
        }

        return results;
    }

    #endregion

    #region Helper Parsers & Normalizers

    /// <summary>
    /// Derives 3-character uppercase team TLA abbreviation from name or explicit value.
    /// </summary>
    public static string DeriveTla(string? name, string? explicitTla = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitTla))
        {
            string cleanExp = explicitTla.Trim();
            if (TlaValidRegex.IsMatch(cleanExp))
            {
                return cleanExp.ToUpperInvariant()[..Math.Min(3, cleanExp.Length)];
            }
        }

        string raw = (name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(raw)) return "TBD";

        if (TlaOverrides.TryGetValue(raw, out var overrideCode))
        {
            return overrideCode;
        }

        string ascii = RemoveDiacritics(raw);
        var allWords = TlaWordSplitRegex.Split(ascii)
            .Select(w => TlaNonAlphanumericRegex.Replace(w, ""))
            .Where(w => w.Length > 0)
            .ToList();

        var words = allWords.Where(w => !TlaStopwords.Contains(w) && !NumericOnlyRegex.IsMatch(w)).ToList();
        if (words.Count == 0) words = allWords;
        if (words.Count == 0) return raw.PadRight(3, 'X')[..3].ToUpperInvariant();

        string code;
        if (words.Count == 1)
        {
            code = words[0][..Math.Min(3, words[0].Length)];
        }
        else if (words.Count == 2)
        {
            code = words[0][..1] + words[1][..Math.Min(2, words[1].Length)];
        }
        else
        {
            code = string.Concat(words.Take(3).Select(w => w[0]));
        }

        return code.PadRight(3, 'X')[..3].ToUpperInvariant();
    }

    /// <summary>
    /// Resolves upstream telemetry provider numeric competition ID using priority rules (LogoUrl -> Direct numeric -> Slug/alias -> Trailing digits).
    /// </summary>
    public static int? ResolveUpstreamLeagueId(string? competitionId, string? competitionName = null, string? logoUrl = null, string? country = null)
    {
        // 0. Extract numeric ID from logoUrl if present
        if (!string.IsNullOrWhiteSpace(logoUrl))
        {
            var logoMatch = LeagueLogoIdRegex.Match(logoUrl);
            if (!logoMatch.Success) logoMatch = LogoIdRegex.Match(logoUrl);
            if (logoMatch.Success && int.TryParse(logoMatch.Groups[1].Value, out int logoNum) && logoNum > 0)
            {
                if (logoNum == 10650 || logoNum == 191) return 519;
                return logoNum;
            }
        }

        string rawId = (competitionId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(rawId) && string.IsNullOrEmpty(competitionName)) return null;

        string lower = rawId.ToLowerInvariant();
        string nameLower = (competitionName ?? string.Empty).ToLowerInvariant().Trim();
        string countryLower = (country ?? string.Empty).ToLowerInvariant().Trim();
        string combined = $"{lower} {nameLower} {countryLower}";

        // Priority 0: Egyptian qualification check before generic premier-league slug resolution
        if (combined.Contains("egypt") || combined.Contains("519") || combined.Contains("10650") || combined.Contains("191") ||
            lower == "comp_519" || lower == "lt_519" || lower == "519" ||
            lower == "comp_10650" || lower == "lt_10650" || lower == "10650" ||
            lower == "comp_191" || lower == "lt_191" || lower == "191" ||
            nameLower.Contains("egy") || lower.Contains("egy") || countryLower == "egy" || countryLower == "egypt")
        {
            return 519;
        }

        // 0.5 Check OTA competition overrides first
        var otaOverrides = OtaConfigService.Instance.CurrentManifest?.CompetitionOverrides;
        if (otaOverrides != null)
        {
            if (otaOverrides.TryGetValue(lower, out int otaSlugId)) return otaSlugId;
            if (otaOverrides.TryGetValue(rawId, out int otaRawId)) return otaRawId;
            string otaStripped = CompPrefixShortRegex.Replace(lower, "");
            if (otaOverrides.TryGetValue(otaStripped, out int otaStrippedId)) return otaStrippedId;
        }

        // 0.8 Check canonical competition mapping
        if (CanonicalCompetitionMap.TryGetValue(lower, out int canonLowerId)) return canonLowerId;
        if (CanonicalCompetitionMap.TryGetValue(rawId, out int canonRawId)) return canonRawId;

        // 1. Direct slug match
        if (CompetitionSlugToUpstreamId.TryGetValue(lower, out int slugId)) return slugId;

        string stripped = CompPrefixShortRegex.Replace(lower, "");
        if (CompetitionSlugToUpstreamId.TryGetValue(stripped, out int strippedId)) return strippedId;

        string normalizedSlug = stripped.Replace("-", "_").Replace(" ", "_");
        if (CompetitionSlugToUpstreamId.TryGetValue(normalizedSlug, out int normSlugId)) return normSlugId;

        // 2. Direct numeric check
        var numMatch = CompIdMatchRegex.Match(lower);
        if (numMatch.Success && int.TryParse(numMatch.Groups[1].Value, out int directNum) && directNum > 0)
        {
            if (directNum == 10650 || directNum == 191) return 519;
            return directNum;
        }

        // 3. Name check
        if (!string.IsNullOrEmpty(nameLower))
        {
            if (otaOverrides != null)
            {
                if (otaOverrides.TryGetValue(nameLower, out int otaNameId)) return otaNameId;
                string otaNameSlug = SlugNonAlphanumericRegex.Replace(nameLower, "_").Trim('_');
                if (otaOverrides.TryGetValue(otaNameSlug, out int otaNameSlugId)) return otaNameSlugId;
            }

            if (nameLower.Contains("egypt") || nameLower == "egyptian premier league") return 519;
            if (CompetitionSlugToUpstreamId.TryGetValue(nameLower, out int nameId)) return nameId;
            string nameSlug = SlugNonAlphanumericRegex.Replace(nameLower, "_").Trim('_');
            if (CompetitionSlugToUpstreamId.TryGetValue(nameSlug, out int nameSlugId)) return nameSlugId;
        }

        if (combined.Contains("conference") || combined.Contains("uecl")) return 10216;
        if (combined.Contains("europa") || combined.Contains("uel")) return 73;
        if (combined.Contains("champions") || combined.Contains("ucl")) return 42;
        if (combined.Contains("egypt") || combined.Contains("519") || combined.Contains("10650") || combined.Contains("191") || (combined.Contains("premier") && combined.Contains("league") && (combined.Contains("egy") || combined.Contains("519") || combined.Contains("10650") || combined.Contains("191")))) return 519;
        if (combined.Contains("laliga 2") || combined.Contains("laliga2") || combined.Contains("segunda") || combined.Contains("hypermotion")) return 140;
        if ((combined.Contains("laliga") || combined.Contains("la liga") || combined.Contains("la_liga")) && !combined.Contains("2") && !combined.Contains("segunda") && !combined.Contains("hypermotion")) return 87;
        if ((combined.Contains("english premier league") || combined.Contains("premier league") || (combined.Contains("premier") && combined.Contains("league"))) && !combined.Contains("egy") && !combined.Contains("519") && !combined.Contains("10650") && !combined.Contains("191") && !combined.Contains("frauen") && !combined.Contains("women") && (combined.Contains("eng") || combined.Contains("gb-eng") || combined.Contains("england") || (!combined.Contains("ger") && !combined.Contains("deu") && !combined.Contains("fra") && !combined.Contains("ita") && !combined.Contains("esp") && !combined.Contains("rus") && !combined.Contains("scot") && !combined.Contains("welsh") && !combined.Contains("irish")))) return 47;
        if (combined.Contains("serie a") || combined.Contains("serie_a")) return 55;
        if (combined.Contains("bundesliga")) return 54;
        if (combined.Contains("ligue 1") || combined.Contains("ligue_1")) return 53;
        if (combined.Contains("saudi") || combined.Contains("spl")) return 536;
        if (combined.Contains("mls") || combined.Contains("major league")) return 130;
        if (combined.Contains("eredivisie")) return 57;
        if (combined.Contains("portugal") || combined.Contains("primeira")) return 61;
        if (combined.Contains("championship")) return 48;
        if (combined.Contains("fa cup") || combined.Contains("fa_cup")) return 132;
        if (combined.Contains("copa del rey") || combined.Contains("copa_del_rey")) return 138;
        if (combined.Contains("coppa italia") || combined.Contains("coppa_italia")) return 141;
        if (combined.Contains("dfb")) return 209;
        if (combined.Contains("coupe de france")) return 134;
        if (combined.Contains("afcon") || combined.Contains("africa cup")) return 289;
        if (combined.Contains("world cup")) return 77;

        // 4. Trailing digits (only for non-canonical hash IDs)
        if (!rawId.StartsWith("ft_", StringComparison.OrdinalIgnoreCase))
        {
            string digitsOnly = DigitsOnlyRegex.Replace(rawId, "");
            if (digitsOnly.Length > 0 && int.TryParse(digitsOnly, out int trailingNum) && trailingNum > 0)
            {
                if (trailingNum == 10650 || trailingNum == 191) return 519;
                return trailingNum;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses match timing and state into canonical codes (PREMATCH, FIRST_HALF, HALFTIME, SECOND_HALF, etc.).
    /// </summary>
    public static (string MatchStatus, int? Minute, int? AddedTime, string DisplayClock) ParseLiveTime(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object)
        {
            return ("PREMATCH", null, null, "");
        }

        bool isCancelled = TryGetProp(status, "cancelled", out var cProp) && cProp.ValueKind == JsonValueKind.True;
        if (isCancelled) return ("CANCELLED", null, null, "CAN");

        string reasonShort = "";
        if (TryGetProp(status, "reason", out var rProp) && TryGetProp(rProp, "short", out var rsProp))
        {
            reasonShort = (rsProp.GetString() ?? "").ToUpperInvariant().Trim();
        }

        string liveShort = "";
        if (TryGetProp(status, "liveTime", out var ltProp) && TryGetProp(ltProp, "short", out var ltsProp))
        {
            liveShort = LiveClockSanitizeRegex.Replace(ltsProp.GetString() ?? "", "").ToUpperInvariant().Trim();
        }

        bool isFinished = TryGetProp(status, "finished", out var finProp) && finProp.ValueKind == JsonValueKind.True;
        if (isFinished)
        {
            string finState = "FINISHED";
            string disp = "FT";
            if (reasonShort.Contains("AET")) { finState = "FINISHED_AET"; disp = "AET"; }
            else if (reasonShort.Contains("PEN")) { finState = "FINISHED_PEN"; disp = "PEN"; }
            return (finState, 90, null, disp);
        }

        bool isStarted = (TryGetProp(status, "started", out var sProp) && sProp.ValueKind == JsonValueKind.True) ||
                         (TryGetProp(status, "ongoing", out var onProp) && onProp.ValueKind == JsonValueKind.True);

        if (isStarted)
        {
            if (reasonShort == "HT" || liveShort == "HT") return ("HALFTIME", 45, null, "HT");
            if (reasonShort == "ETHT" || liveShort == "ETHT") return ("EXTRA_TIME_HALFTIME", 105, null, "ETHT");
            if (reasonShort == "PEN" || liveShort == "PEN") return ("PENALTIES", 120, null, "PEN");
            if (reasonShort == "SUSP" || liveShort == "SUSP") return ("SUSPENDED", null, null, "SUSP");
            if (reasonShort == "INT" || liveShort == "INT") return ("INTERRUPTED", null, null, "INT");

            var clockMatch = LiveClockMatchRegex.Match(liveShort);
            if (clockMatch.Success)
            {
                int baseMin = int.Parse(clockMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                int? extraMin = clockMatch.Groups[2].Success ? int.Parse(clockMatch.Groups[2].Value, CultureInfo.InvariantCulture) : null;
                string display = extraMin.HasValue ? $"{baseMin}+{extraMin.Value}'" : $"{baseMin}'";

                string state = "FIRST_HALF";
                if (reasonShort == "ET" || baseMin > 90) state = "EXTRA_TIME";
                else if (reasonShort == "2H" || baseMin > 45) state = "SECOND_HALF";

                return (state, baseMin, extraMin, display);
            }

            return ("LIVE", null, null, "LIVE");
        }

        return ("PREMATCH", null, null, "");
    }

    private static (TeamSummaryDto HomeTeam, TeamSummaryDto AwayTeam, int? HomeScore, int? AwayScore) ParseHeaderTeams(JsonElement header, JsonElement general)
    {
        string hId = "0", aId = "0", hName = "Home", aName = "Away";
        int? hScore = null, aScore = null;

        if (header.ValueKind == JsonValueKind.Object && TryGetProp(header, "teams", out var teamsArr) && teamsArr.ValueKind == JsonValueKind.Array)
        {
            int idx = 0;
            foreach (var t in teamsArr.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object) continue;
                if (idx == 0)
                {
                    hId = TryGetProp(t, "id", out var idElem) ? idElem.ToString() : "0";
                    hName = TryGetProp(t, "name", out var nElem) ? nElem.GetString() ?? "Home" : "Home";
                    if (TryGetProp(t, "score", out var scElem) && scElem.ValueKind == JsonValueKind.Number) hScore = scElem.GetInt32();
                }
                else if (idx == 1)
                {
                    aId = TryGetProp(t, "id", out var idElem) ? idElem.ToString() : "0";
                    aName = TryGetProp(t, "name", out var nElem) ? nElem.GetString() ?? "Away" : "Away";
                    if (TryGetProp(t, "score", out var scElem) && scElem.ValueKind == JsonValueKind.Number) aScore = scElem.GetInt32();
                }
                idx++;
            }
        }
        else if (general.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProp(general, "homeTeam", out var gh) && gh.ValueKind == JsonValueKind.Object)
            {
                hId = TryGetProp(gh, "id", out var idElem) ? idElem.ToString() : "0";
                hName = TryGetProp(gh, "name", out var nElem) ? nElem.GetString() ?? "Home" : "Home";
            }
            if (TryGetProp(general, "awayTeam", out var ga) && ga.ValueKind == JsonValueKind.Object)
            {
                aId = TryGetProp(ga, "id", out var idElem) ? idElem.ToString() : "0";
                aName = TryGetProp(ga, "name", out var nElem) ? nElem.GetString() ?? "Away" : "Away";
            }
        }

        string? canonicalHomeId = null;
        if (int.TryParse(hId, out int hNum) && UpstreamTeamIdToCanonicalMap.TryGetValue(hNum, out var chId) && FixtureRelevanceEngine.IsValidCanonicalTeam(chId, hName))
        {
            canonicalHomeId = chId;
        }

        string? canonicalAwayId = null;
        if (int.TryParse(aId, out int aNum) && UpstreamTeamIdToCanonicalMap.TryGetValue(aNum, out var caId) && FixtureRelevanceEngine.IsValidCanonicalTeam(caId, aName))
        {
            canonicalAwayId = caId;
        }

        var home = new TeamSummaryDto
        {
            Id = canonicalHomeId ?? hId,
            LegacyId = hId,
            Name = hName,
            ShortName = DeriveTla(hName),
            LogoUrl = BuildTeamLogoUrl(hId)
        };

        var away = new TeamSummaryDto
        {
            Id = canonicalAwayId ?? aId,
            LegacyId = aId,
            Name = aName,
            ShortName = DeriveTla(aName),
            LogoUrl = BuildTeamLogoUrl(aId)
        };

        return (home, away, hScore, aScore);
    }

    private static List<TrayEventDto> ExtractEvents(JsonElement content, JsonElement header, TeamSummaryDto homeTeam, TeamSummaryDto awayTeam, string matchId)
    {
        var results = new List<TrayEventDto>();
        var seenEventKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddEvent(TrayEventDto ev)
        {
            string key = $"{ev.Type}_{ev.Minute}_{ev.ExtraMinute}_{ev.TeamId}_{ev.PlayerName}";
            if (seenEventKeys.Add(key))
            {
                results.Add(ev);
            }
        }

        JsonElement rawEvents = default;
        if (content.ValueKind == JsonValueKind.Object && TryGetProp(content, "matchFacts", out var mf) && mf.ValueKind == JsonValueKind.Object &&
            TryGetProp(mf, "events", out var evRoot))
        {
            if (evRoot.ValueKind == JsonValueKind.Object && TryGetProp(evRoot, "events", out var nestedEv) && nestedEv.ValueKind == JsonValueKind.Array) rawEvents = nestedEv;
            else if (evRoot.ValueKind == JsonValueKind.Array) rawEvents = evRoot;
        }
        else if (header.ValueKind == JsonValueKind.Object && TryGetProp(header, "events", out var hdrEv) && hdrEv.ValueKind == JsonValueKind.Array)
        {
            rawEvents = hdrEv;
        }

        if (rawEvents.ValueKind == JsonValueKind.Array)
        {
            foreach (var ev in rawEvents.EnumerateArray())
            {
                if (ev.ValueKind != JsonValueKind.Object) continue;

                string rawType = TryGetProp(ev, "type", out var tProp) ? tProp.GetString() ?? "Event" : "Event";
                string typeLower = rawType.ToLowerInvariant().Replace(" ", "").Replace("_", "");
                if (typeLower.Contains("addedtime") || typeLower.Contains("half") || typeLower.Contains("period") || typeLower.Contains("whistle")) continue;

                int minute = TryGetProp(ev, "time", out var timeProp) && timeProp.ValueKind == JsonValueKind.Number ? timeProp.GetInt32() : 0;
                int extraMinute = TryGetProp(ev, "overloadTime", out var olProp) && olProp.ValueKind == JsonValueKind.Number ? olProp.GetInt32() : 0;

                bool isHome = TryGetProp(ev, "isHome", out var ihProp) && ihProp.ValueKind == JsonValueKind.True;
                bool isOwnGoal = (TryGetProp(ev, "ownGoal", out var ogProp) && ogProp.ValueKind == JsonValueKind.True) ||
                                 typeLower.Contains("owngoal") ||
                                 (TryGetProp(ev, "suffix", out var sfx) && string.Equals(sfx.GetString(), "OG", StringComparison.OrdinalIgnoreCase)) ||
                                 (TryGetProp(ev, "goalDescription", out var gd) && gd.GetString()?.Contains("own", StringComparison.OrdinalIgnoreCase) == true);

                // In upstream telemetry provider, isHome on a goal/own-goal event is ALREADY the beneficiary team credited with the goal!
                var beneficiaryTeam = isHome ? homeTeam : awayTeam;

                string type = "Event";
                string? detail = null;
                string? playerName = TryGetProp(ev, "nameStr", out var nsProp) ? nsProp.GetString() : null;
                string? playerId = TryGetProp(ev, "playerId", out var pidProp) ? pidProp.ToString() : null;
                string? assistName = TryGetProp(ev, "assistStr", out var asProp) ? asProp.GetString() : null;

                if (TryGetProp(ev, "player", out var pElem) && pElem.ValueKind == JsonValueKind.Object)
                {
                    if (string.IsNullOrEmpty(playerName) && TryGetProp(pElem, "name", out var pn)) playerName = pn.GetString();
                    if (string.IsNullOrEmpty(playerId) && TryGetProp(pElem, "id", out var pi)) playerId = pi.ToString();
                }

                if (typeLower.Contains("goal"))
                {
                    type = "Goal";
                    detail = isOwnGoal ? "Own Goal" : (typeLower.Contains("pen") ? "Penalty" : "Normal Goal");
                }
                else if (typeLower.Contains("card"))
                {
                    string cardColor = TryGetProp(ev, "card", out var cCol) ? cCol.GetString()?.ToLowerInvariant() ?? "" : "";
                    if (cardColor.Contains("red") || typeLower.Contains("red"))
                    {
                        type = "RedCard";
                        detail = "Red Card";
                    }
                    else
                    {
                        type = "YellowCard";
                        detail = "Yellow Card";
                    }
                }
                else if (typeLower.Contains("sub"))
                {
                    type = "Substitution";
                    detail = "Substitution";
                    if (TryGetProp(ev, "swap", out var swapElem) && swapElem.ValueKind == JsonValueKind.Array)
                    {
                        var swapList = swapElem.EnumerateArray().ToList();
                        if (swapList.Count >= 2)
                        {
                            if (swapList[0].ValueKind == JsonValueKind.Object && TryGetProp(swapList[0], "name", out var s0)) assistName = s0.GetString() ?? assistName;
                            if (swapList[1].ValueKind == JsonValueKind.Object && TryGetProp(swapList[1], "name", out var s1)) playerName = s1.GetString() ?? playerName;
                        }
                    }
                }
                else if (typeLower.Contains("var"))
                {
                    type = "VarDecision";
                    detail = "VAR";
                }

                AddEvent(new TrayEventDto
                {
                    Id = $"ev_{matchId}_{minute}_{extraMinute}_{type}_{playerId ?? playerName}",
                    Type = type,
                    Detail = detail,
                    Minute = minute,
                    ExtraMinute = extraMinute,
                    TeamId = beneficiaryTeam.Id,
                    TeamName = beneficiaryTeam.Name,
                    TeamLogoUrl = beneficiaryTeam.LogoUrl,
                    PlayerName = playerName,
                    PlayerId = playerId,
                    AssistPlayerName = assistName,
                    Description = isOwnGoal ? $"{playerName ?? "Player"} (OG)" : (detail ?? type)
                });
            }
        }

        if (results.Count == 0 && header.ValueKind == JsonValueKind.Object &&
            TryGetProp(header, "events", out var hdrEventsObj) && hdrEventsObj.ValueKind == JsonValueKind.Object)
        {
            void ParseHeaderGoalMap(JsonElement mapElem, bool isHomeTeam)
            {
                if (mapElem.ValueKind != JsonValueKind.Object) return;
                var bTeam = isHomeTeam ? homeTeam : awayTeam;
                foreach (var playerProp in mapElem.EnumerateObject())
                {
                    if (playerProp.Value.ValueKind != JsonValueKind.Array) continue;
                    foreach (var goalItem in playerProp.Value.EnumerateArray())
                    {
                        if (goalItem.ValueKind != JsonValueKind.Object) continue;
                        int min = TryGetProp(goalItem, "time", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
                        int extra = TryGetProp(goalItem, "overloadTime", out var ot) && ot.ValueKind == JsonValueKind.Number ? ot.GetInt32() : 0;
                        bool isOg = (TryGetProp(goalItem, "ownGoal", out var og) && og.ValueKind == JsonValueKind.True) ||
                                    (TryGetProp(goalItem, "suffix", out var sfx) && string.Equals(sfx.GetString(), "OG", StringComparison.OrdinalIgnoreCase));
                        string pName = TryGetProp(goalItem, "nameStr", out var ns) ? ns.GetString() ?? playerProp.Name : playerProp.Name;
                        string? pId = TryGetProp(goalItem, "playerId", out var pid) ? pid.ToString() : null;

                        AddEvent(new TrayEventDto
                        {
                            Id = $"ev_{matchId}_{min}_{extra}_Goal_{pId ?? pName}",
                            Type = "Goal",
                            Detail = isOg ? "Own Goal" : "Goal",
                            Minute = min,
                            ExtraMinute = extra,
                            TeamId = bTeam.Id,
                            TeamName = bTeam.Name,
                            PlayerName = pName,
                            PlayerId = pId,
                            Description = isOg ? $"{pName} (OG)" : pName
                        });
                    }
                }
            }

            if (TryGetProp(hdrEventsObj, "homeTeamGoals", out var htg)) ParseHeaderGoalMap(htg, isHomeTeam: true);
            if (TryGetProp(hdrEventsObj, "awayTeamGoals", out var atg)) ParseHeaderGoalMap(atg, isHomeTeam: false);
        }

        results.Sort((a, b) =>
        {
            if (a.Minute != b.Minute) return a.Minute.CompareTo(b.Minute);
            return a.ExtraMinute.CompareTo(b.ExtraMinute);
        });

        return results;
    }

    private static List<MatchCommentaryItemDto> ExtractCommentary(
        JsonElement content,
        JsonElement header,
        List<TrayEventDto> events,
        TeamSummaryDto homeTeam,
        TeamSummaryDto awayTeam,
        string matchId,
        string matchStatus,
        int? currentMinute,
        int? homeScore,
        int? awayScore)
    {
        var items = new List<MatchCommentaryItemDto>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCommentary(MatchCommentaryItemDto item)
        {
            if (seenIds.Add(item.Id)) items.Add(item);
        }

        bool isStarted = IsStartedState(matchStatus);
        bool reachedHt = matchStatus == "HALFTIME" || (currentMinute ?? 0) >= 45 || IsFinishedState(matchStatus);
        bool isFinished = IsFinishedState(matchStatus);

        // 1. Period Whistle Markers
        if (isStarted)
        {
            AddCommentary(new MatchCommentaryItemDto
            {
                Id = $"comm_{matchId}_whistle_kickoff",
                Minute = 1,
                Type = "whistle",
                Text = $"First half kicks off! {homeTeam.Name} vs {awayTeam.Name} is underway."
            });
        }

        if (reachedHt)
        {
            AddCommentary(new MatchCommentaryItemDto
            {
                Id = $"comm_{matchId}_whistle_halftime",
                Minute = 45,
                Type = "whistle",
                Text = $"Half-time whistle blows! Score at the break: {homeTeam.Name} {homeScore ?? 0}–{awayScore ?? 0} {awayTeam.Name}."
            });
            AddCommentary(new MatchCommentaryItemDto
            {
                Id = $"comm_{matchId}_whistle_secondhalf",
                Minute = 46,
                Type = "whistle",
                Text = "Second half is underway!"
            });
        }

        if (isFinished)
        {
            AddCommentary(new MatchCommentaryItemDto
            {
                Id = $"comm_{matchId}_whistle_fulltime",
                Minute = Math.Max(90, currentMinute ?? 90),
                Type = "whistle",
                Text = $"Full-time whistle! The referee brings the match to an end: {homeTeam.Name} {homeScore ?? 0}–{awayScore ?? 0} {awayTeam.Name}."
            });
        }

        // 2. Events narration (Goals, Cards, Substitutions)
        foreach (var ev in events)
        {
            string rawType = ev.Type ?? "Event";
            string typeLower = rawType.ToLowerInvariant();
            if (typeLower.Contains("half") || typeLower.Contains("period") || typeLower.Contains("whistle") || typeLower.Contains("kickoff") || typeLower.Contains("fulltime"))
                continue;

            string cType = rawType.ToLowerInvariant();
            string text = ev.Description ?? rawType;

            if (ev.Type == "Goal")
            {
                cType = "goal";
                string assistText = !string.IsNullOrWhiteSpace(ev.AssistPlayerName) ? $" (Assist: {ev.AssistPlayerName})" : "";
                text = $"GOAL! {ev.PlayerName ?? "Player"} scores for {ev.TeamName ?? "team"}!{assistText}";
            }
            else if (ev.Type == "YellowCard")
            {
                cType = "card";
                text = $"Yellow card shown to {ev.PlayerName ?? "Player"} ({ev.TeamName ?? "team"}).";
            }
            else if (ev.Type == "RedCard")
            {
                cType = "card";
                text = $"RED CARD! {ev.PlayerName ?? "Player"} ({ev.TeamName ?? "team"}) is sent off!";
            }
            else if (ev.Type == "Substitution")
            {
                cType = "sub";
                text = $"Substitution for {ev.TeamName ?? "team"}: {ev.AssistPlayerName ?? "Player"} on for {ev.PlayerName ?? "outgoing player"}.";
            }

            if (string.Equals(text.Trim(), "Event", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(text))
            {
                text = !string.IsNullOrWhiteSpace(ev.PlayerName)
                    ? $"Match incident involving {ev.PlayerName} ({ev.TeamName ?? "team"})."
                    : $"Match incident for {ev.TeamName ?? "the match"}.";
            }

            AddCommentary(new MatchCommentaryItemDto
            {
                Id = $"comm_{ev.Id}",
                Minute = ev.Minute,
                ExtraMinute = ev.ExtraMinute,
                Type = cType,
                Text = text,
                TeamId = ev.TeamId,
                TeamName = ev.TeamName,
                TeamLogoUrl = ev.TeamLogoUrl
            });
        }

        // 3. Shots, Saves, Woodwork & Set-Piece Situations (from content.shotmap.shots or content.matchFacts.shotmap.shots)
        JsonElement shotsElem = default;
        if (content.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProp(content, "shotmap", out var smElem) && smElem.ValueKind == JsonValueKind.Object &&
                TryGetProp(smElem, "shots", out var s1) && s1.ValueKind == JsonValueKind.Array)
            {
                shotsElem = s1;
            }
            else if (TryGetProp(content, "matchFacts", out var mfElem) && mfElem.ValueKind == JsonValueKind.Object &&
                     TryGetProp(mfElem, "shotmap", out var smElem2) && smElem2.ValueKind == JsonValueKind.Object &&
                     TryGetProp(smElem2, "shots", out var s2) && s2.ValueKind == JsonValueKind.Array)
            {
                shotsElem = s2;
            }
        }

        if (shotsElem.ValueKind == JsonValueKind.Array)
        {
            int shotIdx = 0;
            foreach (var shot in shotsElem.EnumerateArray())
            {
                shotIdx++;
                if (shot.ValueKind != JsonValueKind.Object) continue;

                string evType = TryGetProp(shot, "eventType", out var evProp) ? evProp.GetString() ?? "" : "";
                int minute = TryGetProp(shot, "min", out var mProp) && mProp.ValueKind == JsonValueKind.Number ? mProp.GetInt32() :
                             (mProp.ValueKind == JsonValueKind.String && int.TryParse(mProp.GetString(), out int pm) ? pm : 0);
                int extraMinute = TryGetProp(shot, "minAdded", out var maProp) && maProp.ValueKind == JsonValueKind.Number ? maProp.GetInt32() :
                                  (maProp.ValueKind == JsonValueKind.String && int.TryParse(maProp.GetString(), out int pma) ? pma : 0);

                // Skip shots that correspond to goals already in events list to avoid duplicate goal cards
                if (string.Equals(evType, "Goal", StringComparison.OrdinalIgnoreCase))
                {
                    bool hasGoalEvent = events.Any(e => string.Equals(e.Type, "Goal", StringComparison.OrdinalIgnoreCase) && Math.Abs(e.Minute - minute) <= 1);
                    if (hasGoalEvent) continue;
                }

                string shotTeamId = TryGetProp(shot, "teamId", out var tidProp) ? tidProp.ToString() : "";
                bool isHome = string.Equals(shotTeamId, homeTeam.Id, StringComparison.OrdinalIgnoreCase);
                string teamId = isHome ? homeTeam.Id : awayTeam.Id;
                string teamName = isHome ? homeTeam.Name : awayTeam.Name;
                string? teamLogoUrl = isHome ? homeTeam.LogoUrl : awayTeam.LogoUrl;
                string playerName = TryGetProp(shot, "playerName", out var pnProp) && !string.IsNullOrWhiteSpace(pnProp.GetString())
                    ? pnProp.GetString()!
                    : (isHome ? homeTeam.Name : awayTeam.Name);

                bool insideBox = TryGetProp(shot, "insideBox", out var ibProp) && ibProp.ValueKind == JsonValueKind.True;
                string situation = TryGetProp(shot, "situation", out var sitProp) ? sitProp.GetString() ?? "" : "";
                string shotTypeStr = TryGetProp(shot, "shotType", out var stProp) ? stProp.GetString()?.ToLowerInvariant() ?? "shot" : "shot";
                bool isPen = string.Equals(situation, "Penalty", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(shotTypeStr, "penalty", StringComparison.OrdinalIgnoreCase);
                string location = isPen ? "inside the box" : (insideBox ? "inside the box" : "outside the box");

                double xGVal = 0.0;
                if (TryGetProp(shot, "expectedGoals", out var xgProp))
                {
                    if (xgProp.ValueKind == JsonValueKind.Number) xGVal = xgProp.GetDouble();
                    else if (xgProp.ValueKind == JsonValueKind.String && double.TryParse(xgProp.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsedXg))
                        xGVal = parsedXg;
                }
                string xGText = xGVal > 0 ? $" (xG: {xGVal.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)})" : "";

                string prefix = "";
                if (string.Equals(situation, "FromCorner", StringComparison.OrdinalIgnoreCase))
                {
                    prefix = $"Corner kick for {teamName} — {playerName} connects with a {shotTypeStr} from {location}. ";
                }
                else if (string.Equals(situation, "FreeKick", StringComparison.OrdinalIgnoreCase))
                {
                    prefix = $"Foul gives {teamName} a dangerous free kick! {playerName} strikes it from {location}. ";
                }
                else if (string.Equals(situation, "FastBreak", StringComparison.OrdinalIgnoreCase))
                {
                    prefix = $"Quick counter-attack by {teamName}! {playerName} fires a shot from {location}. ";
                }
                else if (string.Equals(situation, "Penalty", StringComparison.OrdinalIgnoreCase))
                {
                    prefix = $"Penalty opportunity! {playerName} takes aim from 12 yards. ";
                }

                bool isBlocked = TryGetProp(shot, "isBlocked", out var blkProp) && blkProp.ValueKind == JsonValueKind.True;
                bool isOnTarget = TryGetProp(shot, "isOnTarget", out var ontProp) && ontProp.ValueKind == JsonValueKind.True;

                string outcome = "";
                string cType = "shot";

                if (string.Equals(evType, "Post", StringComparison.OrdinalIgnoreCase) || evType.IndexOf("woodwork", StringComparison.OrdinalIgnoreCase) >= 0 || evType.IndexOf("post", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    cType = "woodwork";
                    outcome = $"Off the woodwork! A blistering effort by {playerName} ({teamName}) rattles the frame of the goal!";
                }
                else if (isBlocked || string.Equals(evType, "Blocked", StringComparison.OrdinalIgnoreCase))
                {
                    cType = "block";
                    outcome = $"Blocked! {playerName} ({teamName}) tries an effort from {location}, but the defense throws a body in the way.";
                }
                else if (string.Equals(evType, "AttemptSaved", StringComparison.OrdinalIgnoreCase) || isOnTarget)
                {
                    cType = "save";
                    outcome = $"Save by the keeper! {playerName} ({teamName}) unleashes a shot from {location}{xGText}, parried away by the goalkeeper.";
                }
                else if (string.Equals(evType, "Miss", StringComparison.OrdinalIgnoreCase))
                {
                    cType = "shot";
                    outcome = $"Just wide! {playerName} ({teamName}) lets fly from {location}, but the effort drifts off target.";
                }
                else
                {
                    cType = "shot";
                    outcome = $"{playerName} ({teamName}) has an effort on goal from {location}{xGText}.";
                }

                string fullText = string.IsNullOrEmpty(prefix) ? outcome : $"{prefix}{outcome}";
                string shotIdStr = TryGetProp(shot, "id", out var idProp) ? idProp.ToString() : $"{minute}_{extraMinute}_{shotIdx}";

                AddCommentary(new MatchCommentaryItemDto
                {
                    Id = $"comm_{matchId}_shot_{shotIdStr}",
                    Minute = minute,
                    ExtraMinute = extraMinute,
                    Type = cType,
                    Text = fullText,
                    TeamId = teamId,
                    TeamName = teamName,
                    TeamLogoUrl = teamLogoUrl
                });
            }
        }

        // 4. Direct commentary feed from liveticker / superlive
        if (content.ValueKind == JsonValueKind.Object)
        {
            JsonElement liveTicker = default;
            if (TryGetProp(content, "liveticker", out var lt)) liveTicker = lt;
            else if (TryGetProp(content, "superlive", out var sl)) liveTicker = sl;
            else if (TryGetProp(content, "commentary", out var com)) liveTicker = com;

            if (liveTicker.ValueKind == JsonValueKind.Object && TryGetProp(liveTicker, "entries", out var ent))
            {
                liveTicker = ent;
            }

            if (liveTicker.ValueKind == JsonValueKind.Array)
            {
                int idx = 0;
                foreach (var entry in liveTicker.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    string text = TryGetProp(entry, "text", out var tProp) ? tProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    int minute = TryGetProp(entry, "time", out var tm) && tm.ValueKind == JsonValueKind.Number ? tm.GetInt32() : 0;
                    int extra = TryGetProp(entry, "overloadTime", out var ot) && ot.ValueKind == JsonValueKind.Number ? ot.GetInt32() : 0;
                    string type = TryGetProp(entry, "type", out var typ) ? typ.GetString() ?? "commentary" : "commentary";

                    AddCommentary(new MatchCommentaryItemDto
                    {
                        Id = $"comm_lt_{matchId}_{minute}_{extra}_{idx++}",
                        Minute = minute,
                        ExtraMinute = extra,
                        Type = type,
                        Text = text
                    });
                }
            }
        }

        // Sort descending: latest match incidents at top
        items.Sort((a, b) =>
        {
            if (a.Minute != b.Minute) return b.Minute.CompareTo(a.Minute);
            return b.ExtraMinute.CompareTo(a.ExtraMinute);
        });

        return items;
    }

    internal static List<TeamStatisticsDto> ExtractStatistics(
        JsonElement content,
        string homeTeamId,
        string awayTeamId,
        string matchId,
        int? homeScore,
        int? awayScore,
        bool isPrematch = false)
    {
        if (isPrematch)
        {
            return new List<TeamStatisticsDto>();
        }

        var homeStats = new TeamStatisticsDto { TeamId = homeTeamId, FixtureId = matchId };
        var awayStats = new TeamStatisticsDto { TeamId = awayTeamId, FixtureId = matchId };

        bool hasStats = false;

        if (content.ValueKind == JsonValueKind.Object &&
            TryGetProp(content, "stats", out var statsElem) && statsElem.ValueKind == JsonValueKind.Object &&
            TryGetProp(statsElem, "Periods", out var periodsElem) && periodsElem.ValueKind == JsonValueKind.Object &&
            TryGetProp(periodsElem, "All", out var allElem) && allElem.ValueKind == JsonValueKind.Object &&
            TryGetProp(allElem, "stats", out var allStatsArr) &&
            allStatsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in allStatsArr.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object) continue;
                if (!TryGetProp(group, "stats", out var statRows) || statRows.ValueKind != JsonValueKind.Array) continue;

                foreach (var row in statRows.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) continue;
                    string key = TryGetProp(row, "key", out var kProp) ? kProp.GetString()?.ToLowerInvariant() ?? "" : "";
                    string title = TryGetProp(row, "title", out var tProp) ? tProp.GetString()?.ToLowerInvariant() ?? "" : "";
                    if (!TryGetProp(row, "stats", out var valArr) || valArr.ValueKind != JsonValueKind.Array) continue;

                    var vals = valArr.EnumerateArray().ToList();
                    if (vals.Count < 2) continue;

                    int? hVal = ParseInt(vals[0]);
                    int? aVal = ParseInt(vals[1]);
                    double? hDbl = ParseDouble(vals[0]);
                    double? aDbl = ParseDouble(vals[1]);

                    if (key.Contains("posses") || title.Contains("posses"))
                    {
                        if (!homeStats.PossessionPercent.HasValue)
                        {
                            homeStats.PossessionPercent = hVal;
                            awayStats.PossessionPercent = aVal;
                            hasStats = true;
                        }
                    }
                    else if (key.Contains("xg") || key.Contains("expected_goals") || title.Contains("expected goals"))
                    {
                        if (!homeStats.ExpectedGoals.HasValue)
                        {
                            homeStats.ExpectedGoals = hDbl;
                            awayStats.ExpectedGoals = aDbl;
                            hasStats = true;
                        }
                    }
                    else if ((key == "total_shots" || key == "shots" || key == "totalshots" || key == "shots_total" || title == "total shots" || title == "shots") &&
                             !key.Contains("box") && !title.Contains("box") && !key.Contains("target") && !title.Contains("target"))
                    {
                        if (!homeStats.TotalShots.HasValue)
                        {
                            homeStats.TotalShots = hVal;
                            awayStats.TotalShots = aVal;
                            hasStats = true;
                        }
                    }
                    else if (key.Contains("target") || key.Contains("on_goal") || title.Contains("on target") || title.Contains("on goal"))
                    {
                        if (!homeStats.ShotsOnGoal.HasValue)
                        {
                            homeStats.ShotsOnGoal = hVal;
                            awayStats.ShotsOnGoal = aVal;
                            hasStats = true;
                        }
                    }
                    else if (key.Contains("corner") || title.Contains("corner"))
                    {
                        if (!homeStats.CornerKicks.HasValue)
                        {
                            homeStats.CornerKicks = hVal;
                            awayStats.CornerKicks = aVal;
                            hasStats = true;
                        }
                    }
                    else if (key.Contains("foul") || title.Contains("foul"))
                    {
                        if (!homeStats.Fouls.HasValue)
                        {
                            homeStats.Fouls = hVal;
                            awayStats.Fouls = aVal;
                            hasStats = true;
                        }
                    }
                    else if (key.Contains("offside") || title.Contains("offside"))
                    {
                        if (!homeStats.Offsides.HasValue)
                        {
                            homeStats.Offsides = hVal;
                            awayStats.Offsides = aVal;
                            hasStats = true;
                        }
                    }
                    else if (key.Contains("yellow") || title.Contains("yellow"))
                    {
                        if (!homeStats.YellowCards.HasValue)
                        {
                            homeStats.YellowCards = hVal;
                            awayStats.YellowCards = aVal;
                            hasStats = true;
                        }
                    }
                    else if (key.Contains("red") || title.Contains("red"))
                    {
                        if (!homeStats.RedCards.HasValue)
                        {
                            homeStats.RedCards = hVal;
                            awayStats.RedCards = aVal;
                            hasStats = true;
                        }
                    }
                    else if (key.Contains("save") || title.Contains("save"))
                    {
                        if (!homeStats.GoalkeeperSaves.HasValue)
                        {
                            homeStats.GoalkeeperSaves = hVal;
                            awayStats.GoalkeeperSaves = aVal;
                            hasStats = true;
                        }
                    }
                    else if ((key == "passes" || key == "total_passes" || key == "totalpasses" || key == "passes_total" || title == "passes" || title == "total passes") &&
                             !key.Contains("half") && !title.Contains("half") && !key.Contains("accurate") && !title.Contains("accurate"))
                    {
                        if (!homeStats.TotalPasses.HasValue)
                        {
                            homeStats.TotalPasses = hVal;
                            awayStats.TotalPasses = aVal;
                            hasStats = true;
                        }
                    }
                    else if (key.Contains("accurate") || title.Contains("accurate"))
                    {
                        if (!homeStats.PassesAccurate.HasValue)
                        {
                            homeStats.PassesAccurate = hVal;
                            awayStats.PassesAccurate = aVal;
                            hasStats = true;
                        }
                    }
                }
            }
        }

        if (!hasStats || isPrematch)
        {
            return new List<TeamStatisticsDto>();
        }

        return new List<TeamStatisticsDto> { homeStats, awayStats };
    }

    private static List<LineupDto> ExtractLineups(JsonElement content, string homeTeamId, string awayTeamId, string matchId)
    {
        var results = new List<LineupDto>();

        if (content.ValueKind != JsonValueKind.Object || !TryGetProp(content, "lineup", out var lineupElem) || lineupElem.ValueKind != JsonValueKind.Object)
        {
            return results;
        }

        JsonElement homeLineupElem = default;
        JsonElement awayLineupElem = default;

        if (TryGetProp(lineupElem, "homeTeam", out var hl) && hl.ValueKind == JsonValueKind.Object) homeLineupElem = hl;
        if (TryGetProp(lineupElem, "awayTeam", out var al) && al.ValueKind == JsonValueKind.Object) awayLineupElem = al;

        if (homeLineupElem.ValueKind == JsonValueKind.Object)
        {
            results.Add(ParseSingleLineup(homeLineupElem, homeTeamId, matchId));
        }
        if (awayLineupElem.ValueKind == JsonValueKind.Object)
        {
            results.Add(ParseSingleLineup(awayLineupElem, awayTeamId, matchId));
        }

        return results;
    }

    private static LineupDto ParseSingleLineup(JsonElement teamElem, string teamId, string matchId)
    {
        if (teamElem.ValueKind != JsonValueKind.Object)
        {
            return new LineupDto { TeamId = teamId, FixtureId = matchId };
        }

        string? formation = TryGetProp(teamElem, "formation", out var fProp) ? fProp.GetString() : "4-4-2";
        string? coachName = null;
        if (TryGetProp(teamElem, "coach", out var coachElem) && coachElem.ValueKind == JsonValueKind.Object &&
            TryGetProp(coachElem, "name", out var cn))
        {
            coachName = cn.GetString();
        }

        var starters = new List<LineupPlayerDto>();
        var substitutes = new List<LineupPlayerDto>();
        var unavailable = new List<UnavailablePlayerDto>();

        if (TryGetProp(teamElem, "starters", out var startersElem) && startersElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in startersElem.EnumerateArray())
            {
                if (p.ValueKind == JsonValueKind.Object)
                    starters.Add(ParseLineupPlayer(p, isStarter: true));
            }
        }
        else if (TryGetProp(teamElem, "startXi", out var startXiElem) && startXiElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in startXiElem.EnumerateArray())
            {
                if (p.ValueKind == JsonValueKind.Object)
                    starters.Add(ParseLineupPlayer(p, isStarter: true));
            }
        }

        JsonElement subsElem = default;
        if ((TryGetProp(teamElem, "subs", out subsElem) ||
             TryGetProp(teamElem, "substitutes", out subsElem) ||
             TryGetProp(teamElem, "bench", out subsElem)) && subsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in subsElem.EnumerateArray())
            {
                if (p.ValueKind == JsonValueKind.Object)
                    substitutes.Add(ParseLineupPlayer(p, isStarter: false));
            }
        }

        if (TryGetProp(teamElem, "unavailable", out var unavailElem) && unavailElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in unavailElem.EnumerateArray())
            {
                if (u.ValueKind != JsonValueKind.Object) continue;
                unavailable.Add(new UnavailablePlayerDto
                {
                    Id = TryGetProp(u, "id", out var idProp) ? idProp.ToString() : null,
                    Name = TryGetProp(u, "name", out var nProp) ? nProp.GetString() ?? "Unknown" : "Unknown",
                    Position = TryGetProp(u, "position", out var posProp) ? posProp.GetString() : null,
                    Reason = TryGetProp(u, "reason", out var rProp) ? rProp.GetString() ?? "Ruled out" : "Ruled out"
                });
            }
        }

        // Assign 5-anchor formation pitch grid coordinates
        var startersWithGrid = AssignFormationGrid(starters, formation);

        return new LineupDto
        {
            TeamId = teamId,
            FixtureId = matchId,
            Formation = formation,
            Coach = coachName != null ? new CoachDto { Name = coachName } : null,
            CoachName = coachName,
            StartingXI = startersWithGrid,
            Substitutes = substitutes,
            UnavailablePlayers = unavailable
        };
    }

    private static LineupPlayerDto ParseLineupPlayer(JsonElement p, bool isStarter)
    {
        if (p.ValueKind != JsonValueKind.Object)
        {
            return new LineupPlayerDto { Id = Guid.NewGuid().ToString("N"), Name = "Unknown", IsStarter = isStarter };
        }

        string id = TryGetProp(p, "id", out var idElem) ? idElem.ToString() : Guid.NewGuid().ToString("N");
        string name = TryGetProp(p, "name", out var nElem) ? nElem.GetString() ?? "Unknown" : "Unknown";
        int? number = null;
        if (TryGetProp(p, "shirtNumber", out var sn) && sn.ValueKind == JsonValueKind.Number) number = sn.GetInt32();
        else if (TryGetProp(p, "number", out var num) && num.ValueKind == JsonValueKind.Number) number = num.GetInt32();

        string? position = TryGetProp(p, "position", out var pos) ? pos.GetString() : null;

        double? rating = null;
        if (TryGetProp(p, "performance", out var perf) && perf.ValueKind == JsonValueKind.Object &&
            TryGetProp(perf, "rating", out var pr) && pr.ValueKind == JsonValueKind.Number)
        {
            rating = Math.Round(pr.GetDouble(), 1);
        }
        else if (TryGetProp(p, "rating", out var rElem) && rElem.ValueKind == JsonValueKind.Number)
        {
            rating = Math.Round(rElem.GetDouble(), 1);
        }

        bool isCaptain = (TryGetProp(p, "isCaptain", out var cap1) && cap1.ValueKind == JsonValueKind.True) || (TryGetProp(p, "captain", out var cap2) && cap2.ValueKind == JsonValueKind.True) || (TryGetProp(p, "isCap", out var cap3) && cap3.ValueKind == JsonValueKind.True) || (TryGetProp(p, "role", out var rProp) && string.Equals(rProp.GetString(), "captain", StringComparison.OrdinalIgnoreCase));
        bool isTopRating = TryGetProp(p, "isTopRating", out var tr) && tr.ValueKind == JsonValueKind.True;

        return new LineupPlayerDto
        {
            Id = id,
            PlayerId = id,
            Name = name,
            Number = number,
            ShirtNumber = number,
            Position = position,
            IsStarter = isStarter,
            Rating = rating,
            IsCaptain = isCaptain,
            IsManOfTheMatch = isTopRating
        };
    }

    /// <summary>
    /// Assigns pitch grid coordinates ("row:col") and positions ('G', 'D', 'M', 'F') to starters.
    /// Row 1 = GK ("1:1", "G"); outfield rows follow the formation.
    /// </summary>
    public static List<LineupPlayerDto> AssignFormationGrid(List<LineupPlayerDto> starters, string? formation)
    {
        var rows = ParseFormation(formation);
        int lastRowIndex = rows.Count + 1;

        string PositionForRow(int row)
        {
            if (row == 1) return "G";
            if (row == 2) return "D";
            if (row == lastRowIndex) return "F";
            return "M";
        }

        var results = new List<LineupPlayerDto>();
        for (int idx = 0; idx < starters.Count; idx++)
        {
            var p = starters[idx];
            if (idx == 0)
            {
                p.Grid = "1:1";
                p.Row = 1;
                p.Col = 1;
                p.Position = "G";
                results.Add(p);
                continue;
            }

            int remaining = idx - 1;
            int row = 2;
            int col = 1;
            for (int r = 0; r < rows.Count; r++)
            {
                if (remaining < rows[r])
                {
                    row = r + 2;
                    col = remaining + 1;
                    break;
                }
                remaining -= rows[r];
                if (r == rows.Count - 1)
                {
                    row = lastRowIndex;
                    col = rows[r] + remaining + 1;
                }
            }

            p.Grid = $"{row}:{col}";
            p.Row = row;
            p.Col = col;
            p.Position = PositionForRow(row);
            results.Add(p);
        }

        return results;
    }

    private static List<int> ParseFormation(string? formation)
    {
        var fallback = new List<int> { 4, 4, 2 };
        if (string.IsNullOrWhiteSpace(formation)) return fallback;

        var parts = FormationSplitRegex.Split(formation)
            .Select(p => int.TryParse(p, out int n) ? n : 0)
            .Where(n => n > 0)
            .ToList();

        if (parts.Count == 0 || parts.Sum() != 10) return fallback;
        return parts;
    }

    private static List<TeamHeatmapDto> ExtractHeatmaps(JsonElement content, string homeTeamId, string awayTeamId, bool isPrematch = false)
    {
        if (isPrematch)
        {
            return new List<TeamHeatmapDto>();
        }

        var homeShots = new List<HeatmapPointDto>();
        var awayShots = new List<HeatmapPointDto>();

        // Check content.shotmap.shots OR content.matchFacts.shotmap.shots
        JsonElement shotsElem = default;
        if (content.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProp(content, "shotmap", out var smElem) && smElem.ValueKind == JsonValueKind.Object &&
                TryGetProp(smElem, "shots", out var s1) && s1.ValueKind == JsonValueKind.Array)
            {
                shotsElem = s1;
            }
            else if (TryGetProp(content, "matchFacts", out var mfElem) && mfElem.ValueKind == JsonValueKind.Object &&
                     TryGetProp(mfElem, "shotmap", out var smElem2) && smElem2.ValueKind == JsonValueKind.Object &&
                     TryGetProp(smElem2, "shots", out var s2) && s2.ValueKind == JsonValueKind.Array)
            {
                shotsElem = s2;
            }
        }

        if (shotsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var shot in shotsElem.EnumerateArray())
            {
                if (shot.ValueKind != JsonValueKind.Object) continue;
                double? xVal = null;
                double? yVal = null;

                if (TryGetProp(shot, "x", out var xProp))
                {
                    if (xProp.ValueKind == JsonValueKind.Number) xVal = xProp.GetDouble();
                    else if (double.TryParse(xProp.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedX)) xVal = parsedX;
                }

                if (TryGetProp(shot, "y", out var yProp))
                {
                    if (yProp.ValueKind == JsonValueKind.Number) yVal = yProp.GetDouble();
                    else if (double.TryParse(yProp.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedY)) yVal = parsedY;
                }

                if (!xVal.HasValue || !yVal.HasValue || double.IsNaN(xVal.Value) || double.IsNaN(yVal.Value)) continue;

                string shotTeamId = TryGetProp(shot, "teamId", out var tid) ? tid.ToString() : "";
                bool isHome = string.Equals(shotTeamId, homeTeamId, StringComparison.OrdinalIgnoreCase) ||
                              (TryGetProp(shot, "isHome", out var ih) && ih.ValueKind == JsonValueKind.True);

                bool isGoal = (TryGetProp(shot, "isGoal", out var ig) && ig.ValueKind == JsonValueKind.True) ||
                              (TryGetProp(shot, "eventType", out var et) && string.Equals(et.GetString(), "Goal", StringComparison.OrdinalIgnoreCase));
                bool onTarget = isGoal || (TryGetProp(shot, "isOnTarget", out var ont) && ont.ValueKind == JsonValueKind.True);
                double intensity = isGoal ? 1.0 : (onTarget ? 0.8 : 0.5);

                // Opta / upstream telemetry provider pitch coordinates:
                // Pitch length: 0..105 meters (goal line to goal line)
                // Pitch width: 0..68 meters (touchline to touchline; center/penalty spot at 34m = 50.0%)
                // Vertical canvas orientation in MatchStatisticsControl:
                // Home attacks towards the bottom goal (Y -> 100%)
                // Away attacks towards the top goal (Y -> 0%)
                double rawLengthX = xVal.Value;
                double rawWidthY = yVal.Value;

                double pctY = isHome
                    ? Math.Clamp((rawLengthX / 105.0) * 100.0, 5.0, 95.0)
                    : Math.Clamp((1.0 - (rawLengthX / 105.0)) * 100.0, 5.0, 95.0);

                double pctX = Math.Clamp((rawWidthY / 68.0) * 100.0, 5.0, 95.0);

                var pt = new HeatmapPointDto
                {
                    X = Math.Round(pctX, 1),
                    Y = Math.Round(pctY, 1),
                    Intensity = intensity,
                    TeamId = isHome ? homeTeamId : awayTeamId
                };

                if (isHome) homeShots.Add(pt);
                else awayShots.Add(pt);
            }
        }

        // Extract possession stats
        int homePoss = 50;
        int awayPoss = 50;
        if (content.ValueKind == JsonValueKind.Object &&
            TryGetProp(content, "stats", out var statsElem) && statsElem.ValueKind == JsonValueKind.Object &&
            TryGetProp(statsElem, "Periods", out var periodsElem) && periodsElem.ValueKind == JsonValueKind.Object &&
            TryGetProp(periodsElem, "All", out var allElem) && allElem.ValueKind == JsonValueKind.Object &&
            TryGetProp(allElem, "stats", out var allStatsArr) && allStatsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in allStatsArr.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object || !TryGetProp(group, "stats", out var statRows) || statRows.ValueKind != JsonValueKind.Array) continue;
                foreach (var row in statRows.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) continue;
                    string key = TryGetProp(row, "key", out var kProp) ? kProp.GetString()?.ToLowerInvariant() ?? "" : "";
                    string title = TryGetProp(row, "title", out var tProp) ? tProp.GetString()?.ToLowerInvariant() ?? "" : "";
                    if (key.Contains("possession") && TryGetProp(row, "stats", out var valArr) && valArr.ValueKind == JsonValueKind.Array)
                    {
                        var vals = valArr.EnumerateArray().ToList();
                        if (vals.Count >= 2)
                        {
                            homePoss = ParseInt(vals[0]) ?? 50;
                            awayPoss = ParseInt(vals[1]) ?? 50;
                            break;
                        }
                    }
                }
            }
        }

        // Territorial Thirds: Calculate dynamically from upstream telemetry provider Opta minute-by-minute momentum pressure (content.momentum.main.data)
        // Sum MUST strictly equal 100%: Bottom (Home Attacking) + Middle (Midfield Contest) + Top (Away Attacking) = 100%
        int homeAttPct = 34;
        int awayAttPct = 28;
        int midPct = 38;
        bool computedFromMomentum = false;

        if (content.ValueKind == JsonValueKind.Object &&
            TryGetProp(content, "momentum", out var momElem) && momElem.ValueKind == JsonValueKind.Object &&
            TryGetProp(momElem, "main", out var mainElem) && mainElem.ValueKind == JsonValueKind.Object &&
            TryGetProp(mainElem, "data", out var mData) && mData.ValueKind == JsonValueKind.Array)
        {
            int hCount = 0;
            int mCount = 0;
            int aCount = 0;

            foreach (var mItem in mData.EnumerateArray())
            {
                if (mItem.ValueKind == JsonValueKind.Object && TryGetProp(mItem, "value", out var vVal))
                {
                    double v = ParseDouble(vVal) ?? 0;
                    if (v > 15.0) hCount++;
                    else if (v < -15.0) aCount++;
                    else mCount++;
                }
            }

            int totalMom = hCount + mCount + aCount;
            if (totalMom > 0)
            {
                homeAttPct = (int)Math.Round((double)hCount / totalMom * 100.0);
                awayAttPct = (int)Math.Round((double)aCount / totalMom * 100.0);
                midPct = Math.Max(10, 100 - homeAttPct - awayAttPct);
                if (homeAttPct + midPct + awayAttPct != 100)
                {
                    midPct = 100 - homeAttPct - awayAttPct;
                }
                computedFromMomentum = true;
            }
        }

        if (!computedFromMomentum)
        {
            // Dynamic territorial calculation from possession and shot pressure
            double hWeight = (homePoss * 0.5) + (homeShots.Count * 2.0);
            double aWeight = (awayPoss * 0.5) + (awayShots.Count * 2.0);
            double mWeight = 40.0;
            double totalW = hWeight + aWeight + mWeight;
            if (totalW > 0)
            {
                homeAttPct = (int)Math.Round(hWeight / totalW * 100.0);
                awayAttPct = (int)Math.Round(aWeight / totalW * 100.0);
                midPct = 100 - homeAttPct - awayAttPct;
            }
        }

        var homeHeatmap = new TeamHeatmapDto
        {
            TeamId = homeTeamId,
            AttackingThirdPct = homeAttPct,
            MiddleThirdPct = midPct,
            DefensiveThirdPct = awayAttPct,
            Points = homeShots
        };

        var awayHeatmap = new TeamHeatmapDto
        {
            TeamId = awayTeamId,
            AttackingThirdPct = awayAttPct,
            MiddleThirdPct = midPct,
            DefensiveThirdPct = homeAttPct,
            Points = awayShots
        };

        return new List<TeamHeatmapDto> { homeHeatmap, awayHeatmap };
    }

    private static H2HAndPredictionsDto? ExtractHeadToHead(JsonElement content, TeamSummaryDto homeTeam, TeamSummaryDto awayTeam, List<StandingRowDto>? standings = null)
    {
        if (content.ValueKind != JsonValueKind.Object) return null;

        JsonElement h2hElem = default;
        TryGetProp(content, "h2h", out h2hElem);

        var lastFiveMeetings = new List<H2HMeetingDto>();
        int homeWins = 0;
        int draws = 0;
        int awayWins = 0;

        if (h2hElem.ValueKind == JsonValueKind.Object)
        {
            // Parse summary array or object if available
            if (TryGetProp(h2hElem, "summary", out var sumElem))
            {
                if (sumElem.ValueKind == JsonValueKind.Array)
                {
                    int sIdx = 0;
                    foreach (var s in sumElem.EnumerateArray())
                    {
                        int val = s.ValueKind == JsonValueKind.Number ? s.GetInt32() : (int.TryParse(s.GetString(), out var p) ? p : 0);
                        if (sIdx == 0) homeWins = val;
                        else if (sIdx == 1) draws = val;
                        else if (sIdx == 2) awayWins = val;
                        sIdx++;
                    }
                }
                else if (sumElem.ValueKind == JsonValueKind.Object)
                {
                    homeWins = TryGetProp(sumElem, "homeWins", out var hw) ? ParseInt(hw) ?? 0 : 0;
                    draws = TryGetProp(sumElem, "draws", out var dr) ? ParseInt(dr) ?? 0 : 0;
                    awayWins = TryGetProp(sumElem, "awayWins", out var aw) ? ParseInt(aw) ?? 0 : 0;
                }
            }

            // Parse matches array
            if (TryGetProp(h2hElem, "matches", out var matchesArr) && matchesArr.ValueKind == JsonValueKind.Array)
            {
                var candidateMatches = new List<(DateTimeOffset Date, H2HMeetingDto Meeting, string HomeName, int HomeScore, int AwayScore, JsonElement RawMatch)>();
                int mIdx = 0;

                foreach (var m in matchesArr.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object) continue;

                    // 1. Check completion status - filter out future/unplayed/cancelled matches
                    bool isFinished = false;
                    bool isStarted = false;
                    bool isCancelled = false;
                    string? statusScoreStr = null;

                    if (TryGetProp(m, "status", out var mStat) && mStat.ValueKind == JsonValueKind.Object)
                    {
                        if (TryGetProp(mStat, "finished", out var fin) && fin.ValueKind == JsonValueKind.True) isFinished = true;
                        if (TryGetProp(mStat, "started", out var st) && st.ValueKind == JsonValueKind.True) isStarted = true;
                        if (TryGetProp(mStat, "cancelled", out var canc) && canc.ValueKind == JsonValueKind.True) isCancelled = true;
                        if (TryGetProp(mStat, "scoreStr", out var ss) && ss.ValueKind == JsonValueKind.String) statusScoreStr = ss.GetString();
                    }
                    if (TryGetProp(m, "finished", out var finTop) && finTop.ValueKind == JsonValueKind.True) isFinished = true;
                    if (TryGetProp(m, "started", out var stTop) && stTop.ValueKind == JsonValueKind.True) isStarted = true;
                    if (TryGetProp(m, "cancelled", out var cancTop) && cancTop.ValueKind == JsonValueKind.True) isCancelled = true;

                    if (!isFinished || !isStarted || isCancelled) continue;

                    // 2. Check match date - must not be in the future
                    string dateUtc = "";
                    if (TryGetProp(m, "time", out var mt) && TryGetProp(mt, "utcTime", out var mutc)) dateUtc = mutc.GetString() ?? "";
                    else if (TryGetProp(m, "status", out var ms) && TryGetProp(ms, "utcTime", out var msutc)) dateUtc = msutc.GetString() ?? "";
                    else if (TryGetProp(m, "date", out var md)) dateUtc = md.ToString();

                    DateTimeOffset sortDate = DateTimeOffset.MinValue;
                    if (DateTimeOffset.TryParse(dateUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDt))
                    {
                        if (parsedDt > DateTimeOffset.UtcNow) continue; // Future meeting - skip!
                        sortDate = parsedDt;
                    }

                    string mHomeName = "Home";
                    string? mHomeId = null;
                    if (TryGetProp(m, "home", out var mHome) && mHome.ValueKind == JsonValueKind.Object)
                    {
                        if (TryGetProp(mHome, "name", out var mhn)) mHomeName = mhn.GetString() ?? "Home";
                        if (TryGetProp(mHome, "id", out var mhid)) mHomeId = mhid.ToString();
                    }
                    else if (TryGetProp(m, "homeTeam", out var mHomeT) && mHomeT.ValueKind == JsonValueKind.Object)
                    {
                        if (TryGetProp(mHomeT, "name", out var mhtn)) mHomeName = mhtn.GetString() ?? "Home";
                        if (TryGetProp(mHomeT, "id", out var mhtid)) mHomeId = mhtid.ToString();
                    }

                    string mAwayName = "Away";
                    string? mAwayId = null;
                    if (TryGetProp(m, "away", out var mAway) && mAway.ValueKind == JsonValueKind.Object)
                    {
                        if (TryGetProp(mAway, "name", out var man)) mAwayName = man.GetString() ?? "Away";
                        if (TryGetProp(mAway, "id", out var maid)) mAwayId = maid.ToString();
                    }
                    else if (TryGetProp(m, "awayTeam", out var mAwayT) && mAwayT.ValueKind == JsonValueKind.Object)
                    {
                        if (TryGetProp(mAwayT, "name", out var matn)) mAwayName = matn.GetString() ?? "Away";
                        if (TryGetProp(mAwayT, "id", out var matid)) mAwayId = matid.ToString();
                    }

                    int? mHomeScore = null;
                    if (TryGetProp(m, "home", out var mh) && TryGetProp(mh, "score", out var mhs)) mHomeScore = ParseInt(mhs);
                    else if (TryGetProp(m, "homeScore", out var mhs2)) mHomeScore = ParseInt(mhs2);

                    int? mAwayScore = null;
                    if (TryGetProp(m, "away", out var ma) && TryGetProp(ma, "score", out var mas)) mAwayScore = ParseInt(mas);
                    else if (TryGetProp(m, "awayScore", out var mas2)) mAwayScore = ParseInt(mas2);

                    // Fallback to scoreStr, score, result if scores are unassigned
                    if (!mHomeScore.HasValue || !mAwayScore.HasValue || (mHomeScore == 0 && mAwayScore == 0))
                    {
                        string? scoreStr = statusScoreStr;
                        if (string.IsNullOrWhiteSpace(scoreStr) && TryGetProp(m, "scoreStr", out var ss2) && ss2.ValueKind == JsonValueKind.String)
                        {
                            scoreStr = ss2.GetString();
                        }
                        else if (string.IsNullOrWhiteSpace(scoreStr) && TryGetProp(m, "score", out var scElem))
                        {
                            if (scElem.ValueKind == JsonValueKind.String)
                            {
                                scoreStr = scElem.GetString();
                            }
                            else if (scElem.ValueKind == JsonValueKind.Array)
                            {
                                var arr = scElem.EnumerateArray().ToList();
                                if (arr.Count >= 2)
                                {
                                    mHomeScore = ParseInt(arr[0]) ?? 0;
                                    mAwayScore = ParseInt(arr[1]) ?? 0;
                                }
                            }
                            else if (scElem.ValueKind == JsonValueKind.Object)
                            {
                                if (TryGetProp(scElem, "home", out var sch)) mHomeScore = ParseInt(sch) ?? 0;
                                if (TryGetProp(scElem, "away", out var sca)) mAwayScore = ParseInt(sca) ?? 0;
                            }
                        }
                        else if (string.IsNullOrWhiteSpace(scoreStr) && TryGetProp(m, "result", out var resElem) && resElem.ValueKind == JsonValueKind.String)
                        {
                            scoreStr = resElem.GetString();
                        }
                        else if (string.IsNullOrWhiteSpace(scoreStr) && TryGetProp(m, "header", out var mHdr) && TryGetProp(mHdr, "status", out var mHdrStat) && TryGetProp(mHdrStat, "scoreStr", out var hss) && hss.ValueKind == JsonValueKind.String)
                        {
                            scoreStr = hss.GetString();
                        }

                        if (!string.IsNullOrWhiteSpace(scoreStr))
                        {
                            var matchRegex = ScoreExtractRegex.Match(scoreStr);
                            if (matchRegex.Success && int.TryParse(matchRegex.Groups[1].Value, out var parsedH) && int.TryParse(matchRegex.Groups[2].Value, out var parsedA))
                            {
                                mHomeScore = parsedH;
                                mAwayScore = parsedA;
                            }
                        }
                    }

                    // A finished match must have a valid score
                    if (!mHomeScore.HasValue || !mAwayScore.HasValue) continue;

                    string mMatchId = TryGetProp(m, "matchId", out var mid) ? mid.ToString() : (TryGetProp(m, "id", out var mid2) ? mid2.ToString() : $"h2h_{mIdx}");

                    string? compName = null;
                    if (TryGetProp(m, "league", out var ml) && TryGetProp(ml, "name", out var mln)) compName = mln.GetString();
                    else if (TryGetProp(m, "tournament", out var mtn) && TryGetProp(mtn, "name", out var mtnn)) compName = mtnn.GetString();
                    else if (TryGetProp(m, "competition", out var mc))
                    {
                        if (mc.ValueKind == JsonValueKind.Object && TryGetProp(mc, "name", out var mcn)) compName = mcn.GetString();
                        else if (mc.ValueKind == JsonValueKind.String) compName = mc.GetString();
                    }

                    var meetingDto = new H2HMeetingDto
                    {
                        MatchId = mMatchId,
                        DateUtc = dateUtc,
                        CompetitionName = compName,
                        HomeTeamId = mHomeId,
                        AwayTeamId = mAwayId,
                        HomeTeamName = mHomeName,
                        AwayTeamName = mAwayName,
                        HomeScore = mHomeScore.Value,
                        AwayScore = mAwayScore.Value
                    };

                    candidateMatches.Add((sortDate, meetingDto, mHomeName, mHomeScore.Value, mAwayScore.Value, m.Clone()));
                    mIdx++;
                }

                // Sort candidate past meetings in descending chronological order and take the last 5
                var pastMeetings = candidateMatches.OrderByDescending(c => c.Date).Take(5).ToList();
                foreach (var item in pastMeetings)
                {
                    lastFiveMeetings.Add(item.Meeting);
                }

                // If summary wasn't present, calculate from match results
                if (!TryGetProp(h2hElem, "summary", out _))
                {
                    homeWins = 0;
                    draws = 0;
                    awayWins = 0;
                    foreach (var item in candidateMatches)
                    {
                        bool isCurrentHomeFirst = item.HomeName.Contains(homeTeam.Name, StringComparison.OrdinalIgnoreCase) ||
                            (TryGetProp(item.RawMatch, "home", out var mhObj) && TryGetProp(mhObj, "id", out var mhid) && string.Equals(mhid.ToString(), homeTeam.Id, StringComparison.OrdinalIgnoreCase));
                        if (item.HomeScore == item.AwayScore) draws++;
                        else if (item.HomeScore > item.AwayScore)
                        {
                            if (isCurrentHomeFirst) homeWins++;
                            else awayWins++;
                        }
                        else
                        {
                            if (isCurrentHomeFirst) awayWins++;
                            else homeWins++;
                        }
                    }
                }
            }
        }
        var homeFormMatches = new List<TeamFormMatchDto>();
        var awayFormMatches = new List<TeamFormMatchDto>();

        // 1. Check content.matchFacts.teamForm OR content.teamForm
        JsonElement tfElem = default;
        if (TryGetProp(content, "matchFacts", out var mf) && TryGetProp(mf, "teamForm", out var mfTf))
        {
            tfElem = mfTf;
        }
        else if (TryGetProp(content, "teamForm", out var tf1))
        {
            tfElem = tf1;
        }

        if (tfElem.ValueKind == JsonValueKind.Array)
        {
            var tfArr = tfElem.EnumerateArray().ToList();
            if (tfArr.Count >= 2)
            {
                var elem0 = tfArr[0];
                var elem1 = tfArr[1];

                bool swap = false;
                if (elem0.ValueKind == JsonValueKind.Object && TryGetProp(elem0, "teamId", out var tId0))
                {
                    string tId0Str = tId0.ToString();
                    if (!string.IsNullOrWhiteSpace(awayTeam.LegacyId) && tId0Str == awayTeam.LegacyId)
                    {
                        swap = true;
                    }
                }

                var homeObj = swap ? elem1 : elem0;
                var awayObj = swap ? elem0 : elem1;

                JsonElement homeItems = homeObj;
                if (homeObj.ValueKind == JsonValueKind.Object && TryGetProp(homeObj, "form", out var hf)) homeItems = hf;
                else if (homeObj.ValueKind == JsonValueKind.Object && TryGetProp(homeObj, "items", out var hi)) homeItems = hi;
                else if (homeObj.ValueKind == JsonValueKind.Object && TryGetProp(homeObj, "recentMatches", out var hr)) homeItems = hr;

                JsonElement awayItems = awayObj;
                if (awayObj.ValueKind == JsonValueKind.Object && TryGetProp(awayObj, "form", out var af)) awayItems = af;
                else if (awayObj.ValueKind == JsonValueKind.Object && TryGetProp(awayObj, "items", out var ai)) awayItems = ai;
                else if (awayObj.ValueKind == JsonValueKind.Object && TryGetProp(awayObj, "recentMatches", out var ar)) awayItems = ar;

                homeFormMatches = ParseTeamFormMatches(homeItems, homeTeam.Name, awayTeam.Name);
                awayFormMatches = ParseTeamFormMatches(awayItems, awayTeam.Name, homeTeam.Name);
            }
        }
        else if (tfElem.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProp(tfElem, "home", out var tfH)) homeFormMatches = ParseTeamFormMatches(tfH, homeTeam.Name, awayTeam.Name);
            else if (TryGetProp(tfElem, "homeTeam", out var tfHt)) homeFormMatches = ParseTeamFormMatches(tfHt, homeTeam.Name, awayTeam.Name);

            if (TryGetProp(tfElem, "away", out var tfA)) awayFormMatches = ParseTeamFormMatches(tfA, awayTeam.Name, homeTeam.Name);
            else if (TryGetProp(tfElem, "awayTeam", out var tfAt)) awayFormMatches = ParseTeamFormMatches(tfAt, awayTeam.Name, homeTeam.Name);
        }

        // 2. Check content.h2h.homeTeam.form and content.h2h.awayTeam.form
        if (homeFormMatches.Count == 0 && h2hElem.ValueKind == JsonValueKind.Object && TryGetProp(h2hElem, "homeTeam", out var htElem))
        {
            if (TryGetProp(htElem, "form", out var hf2)) homeFormMatches = ParseTeamFormMatches(hf2, homeTeam.Name, awayTeam.Name);
            else if (TryGetProp(htElem, "recentMatches", out var rm1)) homeFormMatches = ParseTeamFormMatches(rm1, homeTeam.Name, awayTeam.Name);
        }
        if (awayFormMatches.Count == 0 && h2hElem.ValueKind == JsonValueKind.Object && TryGetProp(h2hElem, "awayTeam", out var atElem))
        {
            if (TryGetProp(atElem, "form", out var af2)) awayFormMatches = ParseTeamFormMatches(af2, awayTeam.Name, homeTeam.Name);
            else if (TryGetProp(atElem, "recentMatches", out var rm2)) awayFormMatches = ParseTeamFormMatches(rm2, awayTeam.Name, homeTeam.Name);
        }

        // Map to HomeForm and AwayForm for backward-compatibility with real values (zero synthetic placeholders)
        var homeForm = homeFormMatches.Select(m => new FormMatchDto
        {
            Result = m.Result,
            OpponentName = m.OpponentName,
            Score = m.ScoreText,
            DateUtc = m.DateText
        }).ToList();

        var awayForm = awayFormMatches.Select(m => new FormMatchDto
        {
            Result = m.Result,
            OpponentName = m.OpponentName,
            Score = m.ScoreText,
            DateUtc = m.DateText
        }).ToList();

        // Win probabilities: extract from poll or calculate
        int homeWinPct = 0, drawPct = 0, awayWinPct = 0;
        JsonElement pollElem = default;
        if (TryGetProp(content, "poll", out var p1)) pollElem = p1;
        else if (TryGetProp(content, "superlive", out var p2)) pollElem = p2;

        if (pollElem.ValueKind == JsonValueKind.Object)
        {
            JsonElement optsElem = default;
            if (TryGetProp(pollElem, "options", out var op1) && op1.ValueKind == JsonValueKind.Array) optsElem = op1;
            else if (TryGetProp(pollElem, "renderData", out var rd) && TryGetProp(rd, "odds", out var ro) && ro.ValueKind == JsonValueKind.Array) optsElem = ro;
            else if (TryGetProp(pollElem, "odds", out var op2) && op2.ValueKind == JsonValueKind.Array) optsElem = op2;

            if (optsElem.ValueKind == JsonValueKind.Array)
            {
                var optsList = optsElem.EnumerateArray().ToList();
                if (optsList.Count >= 3)
                {
                    double v1 = ParseDouble(optsList[0]) ?? 0;
                    double v2 = ParseDouble(optsList[1]) ?? 0;
                    double v3 = ParseDouble(optsList[2]) ?? 0;
                    double tot = v1 + v2 + v3;
                    if (tot > 0)
                    {
                        homeWinPct = (int)Math.Round((v1 / tot) * 100.0);
                        drawPct = (int)Math.Round((v2 / tot) * 100.0);
                        awayWinPct = Math.Max(0, 100 - homeWinPct - drawPct);
                    }
                }
            }
        }

        if (homeWinPct == 0 && drawPct == 0 && awayWinPct == 0)
        {
            int totMeetings = homeWins + draws + awayWins;
            if (totMeetings > 0)
            {
                homeWinPct = (int)Math.Round((double)homeWins / totMeetings * 100.0);
                awayWinPct = (int)Math.Round((double)awayWins / totMeetings * 100.0);
                drawPct = Math.Max(0, 100 - homeWinPct - awayWinPct);
            }
            else
            {
                homeWinPct = 45;
                drawPct = 25;
                awayWinPct = 30;
            }
        }

        string summaryDisplay = $"All Time: {homeWins} {homeTeam.Name} Wins • {draws} Draws • {awayWins} {awayTeam.Name} Wins";

        return new H2HAndPredictionsDto
        {
            HomeWinProbabilityPct = homeWinPct,
            DrawProbabilityPct = drawPct,
            AwayWinProbabilityPct = awayWinPct,
            HomeWins = homeWins,
            Draws = draws,
            AwayWins = awayWins,
            SummaryDisplay = summaryDisplay,
            HomeForm = homeForm,
            AwayForm = awayForm,
            HomeFormMatches = homeFormMatches,
            AwayFormMatches = awayFormMatches,
            LastFiveMeetings = lastFiveMeetings
        };
    }

    private static string ExtractSafeString(JsonElement elem)
    {
        if (elem.ValueKind == JsonValueKind.String)
        {
            return elem.GetString() ?? string.Empty;
        }
        if (elem.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProp(elem, "name", out var n) && n.ValueKind == JsonValueKind.String) return n.GetString() ?? string.Empty;
            if (TryGetProp(elem, "text", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString() ?? string.Empty;
            if (TryGetProp(elem, "utcTime", out var u) && u.ValueKind == JsonValueKind.String) return u.GetString() ?? string.Empty;
            return string.Empty;
        }
        if (elem.ValueKind == JsonValueKind.Number)
        {
            return elem.ToString();
        }
        return string.Empty;
    }

    internal static List<TeamFormMatchDto> ParseTeamFormMatches(JsonElement elem, string currentTeamName, string defaultOpponentName)
    {
        var list = new List<TeamFormMatchDto>();
        if (elem.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in elem.EnumerateArray())
        {
            if (list.Count >= 5) break;

            if (item.ValueKind == JsonValueKind.String)
            {
                string letter = (item.GetString() ?? "").ToUpperInvariant().Trim();
                if (!string.IsNullOrEmpty(letter))
                {
                    string res = letter.StartsWith("W") ? "W" : (letter.StartsWith("L") ? "L" : "D");
                    list.Add(new TeamFormMatchDto
                    {
                        Result = res,
                        OpponentName = string.Empty,
                        ScoreText = string.Empty,
                        MatchSummaryTooltip = res switch { "W" => "Win", "L" => "Loss", _ => "Draw" }
                    });
                }
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object) continue;

            // 1. Result
            string result = "D";
            if (TryGetProp(item, "resultString", out var rs))
            {
                string s = ExtractSafeString(rs).Trim().ToUpperInvariant();
                if (!string.IsNullOrEmpty(s))
                {
                    result = s.StartsWith("W") ? "W" : (s.StartsWith("L") ? "L" : "D");
                }
            }
            else if (TryGetProp(item, "result", out var rVal) && rVal.ValueKind == JsonValueKind.Number)
            {
                int r = rVal.GetInt32();
                result = r > 0 ? "W" : (r == 0 ? "D" : "L");
            }
            else if (TryGetProp(item, "outcome", out var oVal))
            {
                string s = ExtractSafeString(oVal).Trim().ToUpperInvariant();
                if (!string.IsNullOrEmpty(s))
                {
                    result = s.StartsWith("W") ? "W" : (s.StartsWith("L") ? "L" : "D");
                }
            }

            // 2. ScoreText
            string score = string.Empty;
            if (TryGetProp(item, "score", out var scElem))
            {
                score = ExtractSafeString(scElem);
            }
            else if (TryGetProp(item, "scoreString", out var ssElem))
            {
                score = ExtractSafeString(ssElem);
            }
            else if (TryGetProp(item, "scoreStr", out var ssElem2))
            {
                score = ExtractSafeString(ssElem2);
            }

            // 3. Opponent Name & Tooltip object
            string opponent = string.Empty;
            string? dateText = null;
            string? matchId = null;

            if (TryGetProp(item, "matchId", out var mIdProp)) matchId = mIdProp.ToString();
            else if (TryGetProp(item, "id", out var idProp)) matchId = idProp.ToString();
            else if (TryGetProp(item, "linkToMatch", out var linkProp) && linkProp.ValueKind == JsonValueKind.String)
            {
                string link = linkProp.GetString() ?? "";
                int lastSlash = link.LastIndexOf('/');
                if (lastSlash >= 0 && lastSlash < link.Length - 1)
                {
                    matchId = link[(lastSlash + 1)..];
                }
            }

            if (TryGetProp(item, "date", out var dProp)) dateText = ExtractSafeString(dProp);

            string tooltipSummary = string.Empty;
            if (TryGetProp(item, "tooltipText", out var ttElem))
            {
                if (ttElem.ValueKind == JsonValueKind.Object)
                {
                    string ttHome = TryGetProp(ttElem, "homeTeam", out var htProp) ? ExtractSafeString(htProp) : "";
                    string ttAway = TryGetProp(ttElem, "awayTeam", out var atProp) ? ExtractSafeString(atProp) : "";
                    int? ttHomeScore = TryGetProp(ttElem, "homeScore", out var hsProp) && hsProp.ValueKind == JsonValueKind.Number ? hsProp.GetInt32() : null;
                    int? ttAwayScore = TryGetProp(ttElem, "awayScore", out var asProp) && asProp.ValueKind == JsonValueKind.Number ? asProp.GetInt32() : null;
                    if (TryGetProp(ttElem, "utcTime", out var utProp))
                    {
                        string utStr = ExtractSafeString(utProp);
                        if (!string.IsNullOrWhiteSpace(utStr)) dateText ??= utStr;
                    }

                    if (string.IsNullOrWhiteSpace(score) && ttHomeScore.HasValue && ttAwayScore.HasValue)
                    {
                        score = $"{ttHomeScore} - {ttAwayScore}";
                    }

                    if (!string.IsNullOrWhiteSpace(currentTeamName))
                    {
                        if (string.Equals(ttHome, currentTeamName, StringComparison.OrdinalIgnoreCase) ||
                            ttHome.Contains(currentTeamName, StringComparison.OrdinalIgnoreCase) ||
                            currentTeamName.Contains(ttHome, StringComparison.OrdinalIgnoreCase))
                        {
                            opponent = ttAway;
                            if (ttHomeScore.HasValue && ttAwayScore.HasValue)
                            {
                                result = ttHomeScore > ttAwayScore ? "W" : (ttHomeScore == ttAwayScore ? "D" : "L");
                            }
                        }
                        else if (string.Equals(ttAway, currentTeamName, StringComparison.OrdinalIgnoreCase) ||
                                 ttAway.Contains(currentTeamName, StringComparison.OrdinalIgnoreCase) ||
                                 currentTeamName.Contains(ttAway, StringComparison.OrdinalIgnoreCase))
                        {
                            opponent = ttHome;
                            if (ttHomeScore.HasValue && ttAwayScore.HasValue)
                            {
                                result = ttAwayScore > ttHomeScore ? "W" : (ttAwayScore == ttHomeScore ? "D" : "L");
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(ttHome) && !string.IsNullOrWhiteSpace(ttAway) && ttHomeScore.HasValue && ttAwayScore.HasValue)
                    {
                        tooltipSummary = $"{ttHome} {ttHomeScore} - {ttAwayScore} {ttAway}";
                    }
                }
                else if (ttElem.ValueKind == JsonValueKind.String)
                {
                    tooltipSummary = ttElem.GetString() ?? "";
                }
            }

            if (string.IsNullOrWhiteSpace(opponent))
            {
                if (TryGetProp(item, "opponent", out var oppElem))
                {
                    opponent = ExtractSafeString(oppElem);
                }
                else if (TryGetProp(item, "opponentName", out var on2))
                {
                    opponent = ExtractSafeString(on2);
                }
            }

            if (string.IsNullOrWhiteSpace(tooltipSummary))
            {
                if (!string.IsNullOrWhiteSpace(opponent) && !string.IsNullOrWhiteSpace(score))
                {
                    tooltipSummary = $"vs {opponent} ({score})";
                }
                else if (!string.IsNullOrWhiteSpace(opponent))
                {
                    tooltipSummary = $"vs {opponent}";
                }
                else
                {
                    tooltipSummary = result switch { "W" => "Win", "L" => "Loss", _ => "Draw" };
                }
            }

            list.Add(new TeamFormMatchDto
            {
                Result = result,
                OpponentName = opponent,
                ScoreText = score,
                MatchSummaryTooltip = tooltipSummary,
                DateText = dateText,
                MatchId = matchId
            });
        }

        return list;
    }

    private async Task<List<TeamFormMatchDto>> FetchTeamRecentFormMatchesAsync(int teamId, string teamName, CancellationToken ct)
    {
        try
        {
            string url = FormatEndpoint(
                OtaConfigService.Instance.CurrentManifest.Endpoints?.TeamHubUrlTemplate,
                "{teamId}",
                teamId.ToString(),
                $"{UpstreamPrimaryBase}/teams?id={teamId}");
            string? json = await FetchJsonResilientAsync(url, ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                url = FormatEndpoint(
                    OtaConfigService.Instance.CurrentManifest.Endpoints?.TeamHubFallbackUrlTemplate,
                    "{teamId}",
                    teamId.ToString(),
                    $"{UpstreamFallbackBase}/teams?id={teamId}");
                json = await FetchJsonResilientAsync(url, ct);
            }

            if (string.IsNullOrWhiteSpace(json)) return new List<TeamFormMatchDto>();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (TryGetProp(root, "overview", out var ov) &&
                TryGetProp(ov, "teamForm", out var tf) &&
                tf.ValueKind == JsonValueKind.Array)
            {
                return ParseTeamFormMatches(tf, teamName, string.Empty);
            }

                // Fallback to fixtures.allFixtures.fixtures (last finished matches)
                if (TryGetProp(root, "fixtures", out var fxElem) &&
                    TryGetProp(fxElem, "allFixtures", out var afxElem) &&
                    TryGetProp(afxElem, "fixtures", out var fixturesArr) &&
                    fixturesArr.ValueKind == JsonValueKind.Array)
                {
                    var finishedFixtures = new List<JsonElement>();
                    foreach (var fix in fixturesArr.EnumerateArray())
                    {
                        if (fix.ValueKind != JsonValueKind.Object) continue;
                        if (TryGetProp(fix, "status", out var st) &&
                            TryGetProp(st, "finished", out var fin) &&
                            fin.ValueKind == JsonValueKind.True)
                        {
                            finishedFixtures.Add(fix);
                        }
                    }

                    if (finishedFixtures.Count > 0)
                    {
                        var sorted = finishedFixtures.TakeLast(5).Reverse().ToList();
                        var list = new List<TeamFormMatchDto>();
                        foreach (var fix in sorted)
                        {
                            var homeElem = TryGetProp(fix, "home", out var h) ? h : default;
                            var awayElem = TryGetProp(fix, "away", out var a) ? a : default;
                            string hName = TryGetProp(homeElem, "name", out var hn) ? hn.GetString() ?? "" : "";
                            string aName = TryGetProp(awayElem, "name", out var an) ? an.GetString() ?? "" : "";

                            bool isCurrentHome = string.Equals(hName, teamName, StringComparison.OrdinalIgnoreCase) || hName.Contains(teamName, StringComparison.OrdinalIgnoreCase);
                            string oppName = isCurrentHome ? aName : hName;

                            string scoreText = "";
                            int hScore = 0, aScore = 0;
                            if (TryGetProp(fix, "status", out var stElem) && TryGetProp(stElem, "scoreStr", out var scProp))
                            {
                                scoreText = scProp.GetString() ?? "";
                            }
                            if (string.IsNullOrWhiteSpace(scoreText) && TryGetProp(homeElem, "score", out var hs) && TryGetProp(awayElem, "score", out var as_))
                            {
                                hScore = hs.GetInt32();
                                aScore = as_.GetInt32();
                                scoreText = $"{hScore} - {aScore}";
                            }

                            string result = "D";
                            if (isCurrentHome)
                            {
                                result = hScore > aScore ? "W" : (hScore == aScore ? "D" : "L");
                            }
                            else
                            {
                                result = aScore > hScore ? "W" : (aScore == hScore ? "D" : "L");
                            }

                            list.Add(new TeamFormMatchDto
                            {
                                Result = result,
                                OpponentName = oppName,
                                ScoreText = scoreText,
                                MatchSummaryTooltip = !string.IsNullOrWhiteSpace(oppName) && !string.IsNullOrWhiteSpace(scoreText)
                                    ? $"vs {oppName} ({scoreText})"
                                    : result switch { "W" => "Win", "L" => "Loss", _ => "Draw" }
                            });
                        }
                        return list;
                    }
                }
            }
        catch { }

        return new List<TeamFormMatchDto>();
    }

    private static List<string> ExtractUpstreamInsights(JsonElement content)
    {
        var list = new List<string>();
        if (content.ValueKind != JsonValueKind.Object) return list;

        JsonElement insightsElem = default;
        if (TryGetProp(content, "matchFacts", out var mf) && mf.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProp(mf, "insights", out var ins) && ins.ValueKind == JsonValueKind.Array) insightsElem = ins;
            else if (TryGetProp(mf, "topFacts", out var tf) && tf.ValueKind == JsonValueKind.Array) insightsElem = tf;
            else if (TryGetProp(mf, "facts", out var fa) && fa.ValueKind == JsonValueKind.Array) insightsElem = fa;
        }

        if (insightsElem.ValueKind != JsonValueKind.Array)
        {
            if (TryGetProp(content, "insights", out var ci) && ci.ValueKind == JsonValueKind.Array) insightsElem = ci;
            else if (TryGetProp(content, "topFacts", out var ctf) && ctf.ValueKind == JsonValueKind.Array) insightsElem = ctf;
        }

        if (insightsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in insightsElem.EnumerateArray())
            {
                string? text = null;
                if (item.ValueKind == JsonValueKind.String)
                {
                    text = item.GetString();
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    if (TryGetProp(item, "text", out var tElem) && tElem.ValueKind == JsonValueKind.String)
                        text = tElem.GetString();
                    else if (TryGetProp(item, "fact", out var fElem) && fElem.ValueKind == JsonValueKind.String)
                        text = fElem.GetString();
                    else if (TryGetProp(item, "content", out var cElem) && cElem.ValueKind == JsonValueKind.String)
                        text = cElem.GetString();
                    else if (TryGetProp(item, "subtitle", out var sElem) && sElem.ValueKind == JsonValueKind.String)
                        text = sElem.GetString();
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    string clean = TacticalNarrativeSynthesizer.CleanInsightText(text);
                    if (!string.IsNullOrWhiteSpace(clean) && !list.Contains(clean, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(clean);
                    }
                }
            }
        }

        return list;
    }

    private static PlayerOfTheMatchDto? ExtractPlayerOfTheMatch(
        JsonElement content,
        List<LineupDto>? lineups,
        TeamSummaryDto homeTeam,
        TeamSummaryDto awayTeam,
        string matchStatus)
    {
        if (content.ValueKind == JsonValueKind.Object)
        {
            JsonElement motmElem = default;
            if (TryGetProp(content, "matchFacts", out var mf) && mf.ValueKind == JsonValueKind.Object &&
                TryGetProp(mf, "playerOfTheMatch", out var pom1) && pom1.ValueKind == JsonValueKind.Object)
            {
                motmElem = pom1;
            }
            else if (TryGetProp(content, "playerOfTheMatch", out var pom2) && pom2.ValueKind == JsonValueKind.Object)
            {
                motmElem = pom2;
            }

            if (motmElem.ValueKind == JsonValueKind.Object)
            {
                string? pName = null;
                if (TryGetProp(motmElem, "name", out var nElem))
                {
                    if (nElem.ValueKind == JsonValueKind.String) pName = nElem.GetString();
                    else if (nElem.ValueKind == JsonValueKind.Object)
                    {
                        if (TryGetProp(nElem, "fullName", out var fn) && fn.ValueKind == JsonValueKind.String) pName = fn.GetString();
                        else if (TryGetProp(nElem, "name", out var n2) && n2.ValueKind == JsonValueKind.String) pName = n2.GetString();
                        else if (TryGetProp(nElem, "lastName", out var ln) && ln.ValueKind == JsonValueKind.String) pName = ln.GetString();
                    }
                }
                else if (TryGetProp(motmElem, "fullName", out var fn2) && fn2.ValueKind == JsonValueKind.String)
                {
                    pName = fn2.GetString();
                }

                string? tName = null;
                if (TryGetProp(motmElem, "teamName", out var tn) && tn.ValueKind == JsonValueKind.String)
                {
                    tName = tn.GetString();
                }
                else if (TryGetProp(motmElem, "team", out var tElem))
                {
                    if (tElem.ValueKind == JsonValueKind.String) tName = tElem.GetString();
                    else if (tElem.ValueKind == JsonValueKind.Object && TryGetProp(tElem, "name", out var tn2) && tn2.ValueKind == JsonValueKind.String)
                    {
                        tName = tn2.GetString();
                    }
                }

                string? ratingStr = null;
                if (TryGetProp(motmElem, "rating", out var rElem))
                {
                    if (rElem.ValueKind == JsonValueKind.String) ratingStr = rElem.GetString();
                    else if (rElem.ValueKind == JsonValueKind.Number && rElem.TryGetDouble(out var d)) ratingStr = d.ToString("0.0", CultureInfo.InvariantCulture);
                    else if (rElem.ValueKind == JsonValueKind.Object)
                    {
                        if (TryGetProp(rElem, "num", out var rn) && rn.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                            ratingStr = rn.ToString();
                        else if (TryGetProp(rElem, "value", out var rv) && rv.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                            ratingStr = rv.ToString();
                    }
                }

                if (!string.IsNullOrWhiteSpace(pName))
                {
                    string safePName = TacticalNarrativeSynthesizer.CleanInsightText(pName);
                    string safeTName = !string.IsNullOrWhiteSpace(tName) ? TacticalNarrativeSynthesizer.CleanInsightText(tName) : homeTeam.Name;

                    var parts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(ratingStr)) parts.Add($"Rating {ratingStr}");
                    if (TryGetProp(motmElem, "goals", out var gElem) && gElem.ValueKind == JsonValueKind.Number && gElem.TryGetInt32(out var g) && g > 0)
                        parts.Add($"{g} {(g == 1 ? "Goal" : "Goals")}");
                    else if (TryGetProp(motmElem, "stats", out var statsElem) && statsElem.ValueKind == JsonValueKind.Object &&
                             TryGetProp(statsElem, "goals", out var sg) && sg.ValueKind == JsonValueKind.Number && sg.TryGetInt32(out var g2) && g2 > 0)
                        parts.Add($"{g2} {(g2 == 1 ? "Goal" : "Goals")}");

                    string summaryText = parts.Count > 0 ? string.Join(" • ", parts) : (!string.IsNullOrWhiteSpace(ratingStr) ? $"Rating {ratingStr}" : "Player of the Match");

                    return new PlayerOfTheMatchDto
                    {
                        PlayerName = safePName,
                        TeamName = safeTName,
                        Rating = ratingStr,
                        SummaryText = summaryText
                    };
                }
            }
        }

        // Fallback: evaluate lineups if match is Live (2H/FT/Finished)
        if (lineups != null && lineups.Count > 0 &&
            (FixtureRelevanceEngine.IsFinishedState(matchStatus) || FixtureRelevanceEngine.IsLiveState(matchStatus)))
        {
            LineupPlayerDto? bestPlayer = null;
            string bestTeamName = homeTeam.Name;

            foreach (var lineup in lineups)
            {
                string teamName = homeTeam.Name;
                if (!string.IsNullOrWhiteSpace(lineup.TeamId))
                {
                    if (string.Equals(lineup.TeamId, awayTeam.Id, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(lineup.TeamId, awayTeam.LegacyId, StringComparison.OrdinalIgnoreCase) ||
                        lineup.TeamId.EndsWith(awayTeam.Id.Replace("lt_", string.Empty), StringComparison.OrdinalIgnoreCase))
                    {
                        teamName = awayTeam.Name;
                    }
                }

                var allPlayers = (lineup.StartingXI ?? Enumerable.Empty<LineupPlayerDto>())
                    .Concat(lineup.Substitutes ?? Enumerable.Empty<LineupPlayerDto>());

                foreach (var player in allPlayers)
                {
                    if (string.IsNullOrWhiteSpace(player.Name)) continue;

                    if (player.IsManOfTheMatch && player.Rating.GetValueOrDefault() >= 6.5)
                    {
                        bestPlayer = player;
                        bestTeamName = teamName;
                        break;
                    }

                    if (player.Rating.HasValue && player.Rating.Value >= 7.5)
                    {
                        if (bestPlayer == null || player.Rating.Value > bestPlayer.Rating.GetValueOrDefault() ||
                            (player.Rating.Value == bestPlayer.Rating.GetValueOrDefault() && (player.Goals > bestPlayer.Goals || player.IsStarter)))
                        {
                            bestPlayer = player;
                            bestTeamName = teamName;
                        }
                    }
                }

                if (bestPlayer != null && bestPlayer.IsManOfTheMatch) break;
            }

            if (bestPlayer != null && bestPlayer.Rating.GetValueOrDefault() >= 7.5)
            {
                string rStr = bestPlayer.Rating.GetValueOrDefault().ToString("0.0", CultureInfo.InvariantCulture);
                var parts = new List<string> { $"Rating {rStr}" };
                if (bestPlayer.Goals > 0) parts.Add($"{bestPlayer.Goals} {(bestPlayer.Goals == 1 ? "Goal" : "Goals")}");
                if (bestPlayer.Assists > 0) parts.Add($"{bestPlayer.Assists} {(bestPlayer.Assists == 1 ? "Assist" : "Assists")}");

                return new PlayerOfTheMatchDto
                {
                    PlayerName = TacticalNarrativeSynthesizer.CleanInsightText(bestPlayer.Name),
                    TeamName = bestTeamName,
                    Rating = rStr,
                    SummaryText = string.Join(" • ", parts)
                };
            }
        }

        return null;
    }

    private async Task<(List<StandingRowDto>? Standings, List<PlayerStatLeaderDto>? TopScorers, List<PlayerStatLeaderDto>? TopAssists, List<CompetitionFixtureDto>? Fixtures, List<KnockoutStageDto>? KnockoutStages)>
        ResolveStandingsAndLeadersAsync(JsonElement content, string leagueId, string homeId, string awayId, CancellationToken ct)
    {
        List<StandingRowDto>? tableStandings = null;
        if (content.ValueKind == JsonValueKind.Object && TryGetProp(content, "table", out var tableElem) && tableElem.ValueKind == JsonValueKind.Array)
        {
            var parsed = ParseStandingsFromTableElement(tableElem, homeId, awayId);
            if (parsed.Count > 0)
            {
                tableStandings = parsed;
            }
        }

        if (!string.IsNullOrWhiteSpace(leagueId) && leagueId != "0")
        {
            try
            {
                var hub = await GetCompetitionHubAsync(leagueId, ct: ct);
                if (hub != null)
                {
                    List<StandingRowDto>? enrichedStandings = tableStandings;
                    if (enrichedStandings == null && hub.Standings?.Count > 0)
                    {
                        enrichedStandings = hub.Standings.Select(s => new StandingRowDto
                        {
                            Rank = s.Rank,
                            TeamId = s.TeamId,
                            TeamName = s.TeamName,
                            TeamShortName = s.TeamShortName,
                            TeamLogoUrl = s.TeamLogoUrl,
                            Played = s.Played,
                            Wins = s.Wins,
                            Draws = s.Draws,
                            Losses = s.Losses,
                            GoalsFor = s.GoalsFor,
                            GoalsAgainst = s.GoalsAgainst,
                            GoalDiff = s.GoalDiff,
                            Points = s.Points,
                            Form = s.Form,
                            QualificationColor = s.QualificationColor,
                            IsHomeTeam = string.Equals(s.TeamId, homeId, StringComparison.OrdinalIgnoreCase),
                            IsAwayTeam = string.Equals(s.TeamId, awayId, StringComparison.OrdinalIgnoreCase)
                        }).ToList();
                    }

                    return (enrichedStandings, hub.TopScorers, hub.TopAssists, hub.Fixtures, hub.KnockoutStages);
                }
            }
            catch
            {
                // Non-blocking fallback
            }
        }

        return (tableStandings, null, null, null, null);
    }

    private static List<StandingRowDto> ParseStandingsFromLeagueDoc(JsonElement root)
    {
        var standings = new List<StandingRowDto>();
        if (root.ValueKind != JsonValueKind.Object || !TryGetProp(root, "table", out var tableArr) || tableArr.ValueKind != JsonValueKind.Array)
        {
            return standings;
        }

        var firstTable = tableArr.EnumerateArray().FirstOrDefault();
        if (firstTable.ValueKind != JsonValueKind.Object) return standings;

        // Extract form map
        var formMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (TryGetProp(firstTable, "teamForm", out var teamFormElem) && teamFormElem.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in teamFormElem.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    var badges = new StringBuilder();
                    foreach (var m in prop.Value.EnumerateArray())
                    {
                        if (m.ValueKind != JsonValueKind.Object) continue;
                        if (TryGetProp(m, "resultString", out var rs)) badges.Append(GetStringOrToString(rs));
                        else if (TryGetProp(m, "result", out var r) && r.ValueKind == JsonValueKind.Number)
                        {
                            int val = r.GetInt32();
                            badges.Append(val == 1 ? "W" : (val == -1 ? "L" : "D"));
                        }
                    }
                    formMap[prop.Name] = badges.ToString();
                }
            }
        }

        if (TryGetProp(firstTable, "data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Object)
        {
            // Case A: data.table.all
            if (TryGetProp(dataElem, "table", out var innerTable) && innerTable.ValueKind == JsonValueKind.Object &&
                TryGetProp(innerTable, "all", out var allArr) && allArr.ValueKind == JsonValueKind.Array)
            {
                ExtractRowsFromAllArray(allArr, standings, formMap);
            }
            // Case B: data.tables (conferences / multi-groups)
            else if (TryGetProp(dataElem, "tables", out var tablesArr) && tablesArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var groupTable in tablesArr.EnumerateArray())
                {
                    if (groupTable.ValueKind == JsonValueKind.Object &&
                        TryGetProp(groupTable, "table", out var gTable) && gTable.ValueKind == JsonValueKind.Object &&
                        TryGetProp(gTable, "all", out var gAllArr) && gAllArr.ValueKind == JsonValueKind.Array)
                    {
                        ExtractRowsFromAllArray(gAllArr, standings, formMap);
                    }
                }
            }
        }

        return standings;
    }

    private static void ExtractRowsFromAllArray(JsonElement allArr, List<StandingRowDto> standings, Dictionary<string, string> formMap)
    {
        int idx = standings.Count + 1;
        foreach (var row in allArr.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            string teamId = TryGetProp(row, "id", out var idElem) ? GetStringOrToString(idElem) ?? "" : "";
            string teamName = TryGetProp(row, "name", out var nElem) ? GetStringOrToString(nElem) ?? "" : "";
            string? shortName = TryGetProp(row, "shortName", out var snElem) ? GetStringOrToString(snElem) : DeriveTla(teamName);

            int played = TryGetProp(row, "played", out var pElem) && pElem.ValueKind == JsonValueKind.Number ? pElem.GetInt32() : 0;
            int wins = TryGetProp(row, "wins", out var wElem) && wElem.ValueKind == JsonValueKind.Number ? wElem.GetInt32() : 0;
            int draws = TryGetProp(row, "draws", out var dElem) && dElem.ValueKind == JsonValueKind.Number ? dElem.GetInt32() : 0;
            int losses = TryGetProp(row, "losses", out var lElem) && lElem.ValueKind == JsonValueKind.Number ? lElem.GetInt32() : 0;
            int points = TryGetProp(row, "pts", out var ptsElem) && ptsElem.ValueKind == JsonValueKind.Number ? ptsElem.GetInt32() : 0;

            int gf = 0, ga = 0;
            if (TryGetProp(row, "scoresStr", out var scStr))
            {
                var parts = (scStr.GetString() ?? "").Split('-', '–', ':');
                if (parts.Length >= 2)
                {
                    int.TryParse(parts[0].Trim(), out gf);
                    int.TryParse(parts[1].Trim(), out ga);
                }
            }

            int gd = TryGetProp(row, "goalConDiff", out var gdElem) && gdElem.ValueKind == JsonValueKind.Number
                ? gdElem.GetInt32()
                : (gf - ga);

            string? qualColor = TryGetProp(row, "qualColor", out var qcElem) ? qcElem.GetString() : null;
            formMap.TryGetValue(teamId, out var form);

            standings.Add(new StandingRowDto
            {
                Rank = TryGetProp(row, "idx", out var rankElem) && rankElem.ValueKind == JsonValueKind.Number ? rankElem.GetInt32() : idx++,
                TeamId = teamId,
                TeamName = teamName,
                TeamShortName = shortName,
                TeamLogoUrl = BuildTeamLogoUrl(teamId),
                Played = played,
                Wins = wins,
                Draws = draws,
                Losses = losses,
                GoalsFor = gf,
                GoalsAgainst = ga,
                GoalDiff = gd,
                Points = points,
                Form = form,
                QualificationColor = qualColor
            });
        }
    }

    private static List<StandingRowDto> ParseStandingsFromTableElement(JsonElement tableElem, string homeId, string awayId)
    {
        var list = new List<StandingRowDto>();
        if (tableElem.ValueKind != JsonValueKind.Array) return list;

        int idx = 1;
        foreach (var item in tableElem.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (TryGetProp(item, "data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (TryGetProp(data, "table", out var tbl) && tbl.ValueKind == JsonValueKind.Object &&
                    TryGetProp(tbl, "all", out var all) && all.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in all.EnumerateArray())
                    {
                        if (row.ValueKind != JsonValueKind.Object) continue;
                        string teamId = TryGetProp(row, "id", out var idElem) ? GetStringOrToString(idElem) ?? "" : "";
                        string teamName = TryGetProp(row, "name", out var nElem) ? GetStringOrToString(nElem) ?? "" : "";
                        list.Add(new StandingRowDto
                        {
                            Rank = TryGetProp(row, "idx", out var rankElem) && rankElem.ValueKind == JsonValueKind.Number ? rankElem.GetInt32() : idx++,
                            TeamId = teamId,
                            TeamName = teamName,
                            TeamShortName = DeriveTla(teamName),
                            TeamLogoUrl = BuildTeamLogoUrl(teamId),
                            Played = TryGetProp(row, "played", out var pElem) && pElem.ValueKind == JsonValueKind.Number ? pElem.GetInt32() : 0,
                            Wins = TryGetProp(row, "wins", out var wElem) && wElem.ValueKind == JsonValueKind.Number ? wElem.GetInt32() : 0,
                            Draws = TryGetProp(row, "draws", out var dElem) && dElem.ValueKind == JsonValueKind.Number ? dElem.GetInt32() : 0,
                            Losses = TryGetProp(row, "losses", out var lElem) && lElem.ValueKind == JsonValueKind.Number ? lElem.GetInt32() : 0,
                            Points = TryGetProp(row, "pts", out var ptsElem) && ptsElem.ValueKind == JsonValueKind.Number ? ptsElem.GetInt32() : 0,
                            IsHomeTeam = string.Equals(teamId, homeId, StringComparison.OrdinalIgnoreCase),
                            IsAwayTeam = string.Equals(teamId, awayId, StringComparison.OrdinalIgnoreCase)
                        });
                    }
                }
            }
        }
        return list;
    }

    private async Task<(List<PlayerStatLeaderDto> TopScorers, List<PlayerStatLeaderDto> TopAssists)>
        ParseLeadersFromLeagueDocAsync(JsonElement root, CancellationToken ct)
    {
        var topScorers = new List<PlayerStatLeaderDto>();
        var topAssists = new List<PlayerStatLeaderDto>();

        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetProp(root, "stats", out var statsElem) || statsElem.ValueKind != JsonValueKind.Object ||
            !TryGetProp(statsElem, "players", out var playersArr) ||
            playersArr.ValueKind != JsonValueKind.Array)
        {
            return (topScorers, topAssists);
        }

        foreach (var stat in playersArr.EnumerateArray())
        {
            if (stat.ValueKind != JsonValueKind.Object) continue;
            string name = TryGetProp(stat, "name", out var n) ? GetStringOrToString(n) ?? "" : "";
            string header = TryGetProp(stat, "header", out var h) ? GetStringOrToString(h)?.ToLowerInvariant() ?? "" : "";
            string? fetchAllUrl = TryGetProp(stat, "fetchAllUrl", out var fau) ? GetStringOrToString(fau) : null;

            bool isGoals = name == "goals" || header.Contains("scorer");
            bool isAssists = name == "goal_assist" || header.Contains("assist");

            if (!isGoals && !isAssists) continue;

            List<PlayerStatLeaderDto>? targetList = isGoals ? topScorers : topAssists;

            // Fetch detailed list if fetchAllUrl is present
            if (!string.IsNullOrWhiteSpace(fetchAllUrl))
            {
                try
                {
                    string? subJson = await FetchJsonResilientAsync(fetchAllUrl, ct, timeoutSeconds: 3);
                    if (!string.IsNullOrWhiteSpace(subJson))
                    {
                        using var subDoc = JsonDocument.Parse(subJson);
                        if (subDoc.RootElement.ValueKind == JsonValueKind.Object &&
                            TryGetProp(subDoc.RootElement, "TopLists", out var topLists) &&
                            topLists.ValueKind == JsonValueKind.Array &&
                            topLists.GetArrayLength() > 0)
                        {
                            var firstTopList = topLists[0];
                            if (firstTopList.ValueKind == JsonValueKind.Object &&
                                TryGetProp(firstTopList, "StatList", out var statList) &&
                                statList.ValueKind == JsonValueKind.Array)
                            {
                                int rk = 1;
                                foreach (var item in statList.EnumerateArray().Take(15))
                                {
                                    if (item.ValueKind != JsonValueKind.Object) continue;
                                    string pId = TryGetProp(item, "ParticipantId", out var pid) ? GetStringOrToString(pid) ?? "" : (TryGetProp(item, "id", out var idElem) ? GetStringOrToString(idElem) ?? "" : rk.ToString());
                                    string pName = TryGetProp(item, "ParticipantName", out var pn) ? GetStringOrToString(pn) ?? "Player" : "Player";
                                    string tId = TryGetProp(item, "TeamId", out var tid) ? GetStringOrToString(tid) ?? "" : "";
                                    string tName = TryGetProp(item, "TeamName", out var tn) ? GetStringOrToString(tn) ?? "" : "";
                                    int val = TryGetProp(item, "StatValue", out var sv) && sv.ValueKind == JsonValueKind.Number ? (int)sv.GetDouble() : 0;
                                    int? subVal = TryGetProp(item, "SubStatValue", out var ssv) && ssv.ValueKind == JsonValueKind.Number ? (int?)ssv.GetDouble() : null;

                                    targetList.Add(new PlayerStatLeaderDto
                                    {
                                        Rank = rk++,
                                        PlayerId = pId,
                                        PlayerName = pName,
                                        TeamId = tId,
                                        TeamName = tName,
                                        TeamLogoUrl = !string.IsNullOrWhiteSpace(tId) ? BuildTeamLogoUrl(tId) : null,
                                        Goals = isGoals ? val : null,
                                        Assists = isAssists ? val : null,
                                        Penalties = subVal
                                    });
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Fall back to topThree
                }
            }

            // Fallback to topThree if empty
            if (targetList.Count == 0 && TryGetProp(stat, "topThree", out var topThree) && topThree.ValueKind == JsonValueKind.Array)
            {
                int rk = 1;
                foreach (var item in topThree.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string pId = TryGetProp(item, "id", out var pid) ? GetStringOrToString(pid) ?? "" : rk.ToString();
                    string pName = TryGetProp(item, "name", out var pn) ? GetStringOrToString(pn) ?? "Player" : "Player";
                    string tId = TryGetProp(item, "teamId", out var tid) ? GetStringOrToString(tid) ?? "" : "";
                    string tName = TryGetProp(item, "teamName", out var tn) ? GetStringOrToString(tn) ?? "" : "";
                    int val = TryGetProp(item, "value", out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;

                    targetList.Add(new PlayerStatLeaderDto
                    {
                        Rank = rk++,
                        PlayerId = pId,
                        PlayerName = pName,
                        TeamId = tId,
                        TeamName = tName,
                        TeamLogoUrl = !string.IsNullOrWhiteSpace(tId) ? BuildTeamLogoUrl(tId) : null,
                        Goals = isGoals ? val : null,
                        Assists = isAssists ? val : null
                    });
                }
            }
        }

        return (topScorers, topAssists);
    }

    public static int GetCompetitionPriority(string? compId, string? compName)
    {
        string id = (compId ?? "").Trim();
        string name = (compName ?? "").ToLowerInvariant();

        // 1. Demote Youth / Reserve / U18 / U19 / U21 / U23 leagues
        if (name.Contains("u18") || name.Contains("u19") || name.Contains("u21") || name.Contains("u23") ||
            name.Contains("reserve") || name.Contains("youth") || name.Contains("premier league 2") || name.EndsWith(" 2"))
        {
            return 2000;
        }

        // 2. Brazilian Serie A is Tier 4 (rank 39), NOT Italian Serie A Tier 3 (rank 27)
        if (id == "268" || name.Contains("brasileir") || name.Contains("brazil"))
        {
            return 39;
        }

        var priorityList = OtaConfigService.Instance.CurrentManifest?.PriorityLeagueIds;
        if (priorityList != null && priorityList.Count > 0)
        {
            if (int.TryParse(id, out int numericId))
            {
                int idx = priorityList.IndexOf(numericId);
                if (idx >= 0) return idx + 1;
            }

            int? upstreamId = ResolveUpstreamLeagueId(id, name);
            if (upstreamId.HasValue)
            {
                int idx = priorityList.IndexOf(upstreamId.Value);
                if (idx >= 0) return idx + 1;
            }
        }

        if (id == "519" || id == "10650" || id == "191" || name.Contains("egypt")) return 15;
        if (id == "47" || (name.Contains("premier league") && !name.Contains("egypt"))) return 25;
        if (id == "42" || name.Contains("champions league")) return 2;
        if (id == "87" || name.Contains("la liga") || name.Contains("laliga")) return 26;
        if (id == "55" || (name.Contains("serie a") && !name.Contains("brazil"))) return 27;
        if (id == "54" || name.Contains("bundesliga")) return 28;
        if (id == "53" || name.Contains("ligue 1")) return 29;
        if (id == "73" || name.Contains("europa league")) return 8;
        if (id == "10216" || name.Contains("conference league")) return 9;
        if (id == "536" || name.Contains("saudi")) return 18;
        if (id == "130" || name.Contains("major league soccer")) return 38;
        return 100;
    }

    private static bool IsLiveState(string state) =>
        FixtureRelevanceEngine.IsLiveState(state);

    private static bool IsStartedState(string state) =>
        !string.Equals(state, "PREMATCH", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(state, "SCHEDULED", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(state, "TIMED", StringComparison.OrdinalIgnoreCase);

    private static bool IsFinishedState(string state) =>
        state is "FINISHED" or "FINISHED_AET" or "FINISHED_PEN" or "FT" or "AET" or "PEN";

    private static string? GetStringOrToString(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.ToString()
        };

    private static int? ParseInt(JsonElement elem)
    {
        if (elem.ValueKind == JsonValueKind.Number) return (int)Math.Round(elem.GetDouble());
        if (elem.ValueKind == JsonValueKind.String && int.TryParse(elem.GetString(), out int val)) return val;
        return null;
    }

    private static double? ParseDouble(JsonElement elem)
    {
        if (elem.ValueKind == JsonValueKind.Number) return Math.Round(elem.GetDouble(), 2);
        if (elem.ValueKind == JsonValueKind.String && double.TryParse(elem.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double val)) return Math.Round(val, 2);
        return null;
    }

    private static bool IsEtagMatch(string clientEtag, string? currentEtag)
    {
        if (string.IsNullOrWhiteSpace(clientEtag) || string.IsNullOrWhiteSpace(currentEtag)) return false;
        string cleanClient = clientEtag.Trim().Trim('\"');
        if (cleanClient.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) cleanClient = cleanClient[2..].Trim('\"');

        string cleanCurrent = currentEtag.Trim().Trim('\"');
        if (cleanCurrent.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) cleanCurrent = cleanCurrent[2..].Trim('\"');

        return string.Equals(cleanClient, cleanCurrent, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeSha256Hex(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string RemoveDiacritics(string text)
    {
        string normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (char c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private async Task<string?> FetchJsonResilientAsync(string url, CancellationToken ct, int timeoutSeconds = 8)
    {
        if (!FootballTray.Core.Security.UrlSecurityValidator.IsSafeHttpsUrl(url, allowLoopbackHttpForTests: true))
        {
            Diagnostics.AppDiagnosticsLogger.LogError("NativeTelemetryProvider", "InsecureUrlBlocked", $"Blocked non-HTTPS or unsafe URL: {url}");
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            string? ua = OtaConfigService.Instance.CurrentManifest?.UserAgent;
            if (!string.IsNullOrWhiteSpace(ua))
            {
                req.Headers.TryAddWithoutValidation("User-Agent", ua);
            }
            using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!res.IsSuccessStatusCode) return null;

            return await res.Content.ReadAsStringAsync(cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }

    #endregion

    private static List<CompetitionFixtureDto> ParseFixturesFromLeagueDoc(JsonElement root)
    {
        var fixtures = new List<CompetitionFixtureDto>();
        if (root.ValueKind != JsonValueKind.Object) return fixtures;

        JsonElement matchesArr = default;
        int firstUnplayedIndex = -1;

        if (TryGetProp(root, "fixtures", out var fx) && fx.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProp(fx, "firstUnplayedMatch", out var fum) && TryGetProp(fum, "firstUnplayedMatchIndex", out var fumIdx) && fumIdx.ValueKind == JsonValueKind.Number)
            {
                firstUnplayedIndex = fumIdx.GetInt32();
            }

            if (TryGetProp(fx, "allMatches", out var fam) && fam.ValueKind == JsonValueKind.Array)
            {
                matchesArr = fam;
            }
        }

        if (matchesArr.ValueKind != JsonValueKind.Array && TryGetProp(root, "overview", out var ov) && ov.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProp(ov, "leagueOverviewMatches", out var lom) && lom.ValueKind == JsonValueKind.Array)
            {
                matchesArr = lom;
            }
            else if (TryGetProp(ov, "matches", out var ovm) && ovm.ValueKind == JsonValueKind.Array)
            {
                matchesArr = ovm;
            }
        }

        if (matchesArr.ValueKind != JsonValueKind.Array && TryGetProp(root, "matches", out var mObj) && mObj.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProp(mObj, "allMatches", out var mam) && mam.ValueKind == JsonValueKind.Array)
            {
                matchesArr = mam;
            }
        }

        if (matchesArr.ValueKind != JsonValueKind.Array) return fixtures;

        string rootCompId = "";
        string rootCompName = "";
        if (TryGetProp(root, "details", out var dElem))
        {
            if (TryGetProp(dElem, "id", out var did)) rootCompId = GetStringOrToString(did) ?? "";
            if (TryGetProp(dElem, "name", out var dn)) rootCompName = GetStringOrToString(dn) ?? "";
        }

        var allList = new List<CompetitionFixtureDto>();
        int idx = 0;
        foreach (var m in matchesArr.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;

            string id = TryGetProp(m, "id", out var idElem) ? GetStringOrToString(idElem) ?? "" : (TryGetProp(m, "matchId", out var midElem) ? GetStringOrToString(midElem) ?? "" : $"fix_{idx}");

            string round = "Match";
            if (TryGetProp(m, "round", out var rElem)) round = $"Round {GetStringOrToString(rElem)}";
            else if (TryGetProp(m, "roundName", out var rnElem)) round = $"Round {GetStringOrToString(rnElem)}";
            else if (TryGetProp(m, "tournament", out var tElem) && TryGetProp(tElem, "stage", out var stElem))
            {
                var st = GetStringOrToString(stElem);
                if (!string.IsNullOrWhiteSpace(st)) round = st;
            }

            string homeId = "", homeName = "Home";
            string? homeShort = null;
            if (TryGetProp(m, "home", out var hElem) && hElem.ValueKind == JsonValueKind.Object)
            {
                homeId = TryGetProp(hElem, "id", out var hid) ? GetStringOrToString(hid) ?? "" : "";
                homeName = TryGetProp(hElem, "name", out var hn) ? GetStringOrToString(hn) ?? "Home" : "Home";
                if (TryGetProp(hElem, "shortName", out var hsn)) homeShort = GetStringOrToString(hsn);
            }

            string awayId = "", awayName = "Away";
            string? awayShort = null;
            if (TryGetProp(m, "away", out var aElem) && aElem.ValueKind == JsonValueKind.Object)
            {
                awayId = TryGetProp(aElem, "id", out var aid) ? GetStringOrToString(aid) ?? "" : "";
                awayName = TryGetProp(aElem, "name", out var an) ? GetStringOrToString(an) ?? "Away" : "Away";
                if (TryGetProp(aElem, "shortName", out var asn)) awayShort = GetStringOrToString(asn);
            }

            bool finished = false, started = false, cancelled = false;
            string kickoffUtc = "", statusText = "Upcoming";
            int? homeScore = null, awayScore = null;

            if (TryGetProp(m, "status", out var sElem) && sElem.ValueKind == JsonValueKind.Object)
            {
                if (TryGetProp(sElem, "utcTime", out var uElem)) kickoffUtc = GetStringOrToString(uElem) ?? "";
                if (TryGetProp(sElem, "finished", out var fin) && fin.ValueKind == JsonValueKind.True) finished = true;
                if (TryGetProp(sElem, "started", out var st) && st.ValueKind == JsonValueKind.True) started = true;
                if (TryGetProp(sElem, "cancelled", out var canc) && canc.ValueKind == JsonValueKind.True) cancelled = true;

                if (TryGetProp(sElem, "reason", out var rObj) && TryGetProp(rObj, "short", out var rShort)) statusText = GetStringOrToString(rShort) ?? "";
                else if (finished) statusText = "FT";

                if (TryGetProp(sElem, "scoreStr", out var ss))
                {
                    var ssv = GetStringOrToString(ss);
                    if (!string.IsNullOrWhiteSpace(ssv))
                    {
                        var matchRx = ScoreExtractRegex.Match(ssv);
                        if (matchRx.Success && int.TryParse(matchRx.Groups[1].Value, out var ph) && int.TryParse(matchRx.Groups[2].Value, out var pa))
                        {
                            homeScore = ph;
                            awayScore = pa;
                        }
                    }
                }
            }

            if (!homeScore.HasValue && TryGetProp(m, "home", out var hElem2) && TryGetProp(hElem2, "score", out var hsElem))
            {
                homeScore = ParseInt(hsElem);
            }
            if (!awayScore.HasValue && TryGetProp(m, "away", out var aElem2) && TryGetProp(aElem2, "score", out var asElem))
            {
                awayScore = ParseInt(asElem);
            }

            bool isLive = started && !finished && !cancelled;

            allList.Add(new CompetitionFixtureDto
            {
                Id = id,
                RoundName = round,
                CompetitionId = rootCompId,
                CompetitionName = rootCompName,
                CompetitionLogo = !string.IsNullOrWhiteSpace(rootCompId) ? BuildLeagueLogoUrl(rootCompId) : null,
                KickoffUtc = kickoffUtc,
                HomeTeamId = homeId,
                HomeTeamName = homeName,
                HomeTeamShortName = homeShort ?? DeriveTla(homeName),
                HomeTeamLogo = !string.IsNullOrWhiteSpace(homeId) ? $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/{homeId}.png" : null,
                AwayTeamId = awayId,
                AwayTeamName = awayName,
                AwayTeamShortName = awayShort ?? DeriveTla(awayName),
                AwayTeamLogo = !string.IsNullOrWhiteSpace(awayId) ? $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/{awayId}.png" : null,
                HomeScore = homeScore,
                AwayScore = awayScore,
                StatusText = statusText,
                IsFinished = finished,
                IsStarted = started,
                IsLive = isLive
            });

            idx++;
        }

        if (allList.Count <= 40)
        {
            return allList;
        }

        // Take recent + upcoming slice around first unplayed match
        int centerIdx = firstUnplayedIndex;
        if (centerIdx < 0 || centerIdx >= allList.Count)
        {
            centerIdx = allList.FindIndex(f => !f.IsFinished);
            if (centerIdx < 0) centerIdx = allList.Count - 1;
        }

        int start = Math.Max(0, centerIdx - 10);
        int count = Math.Min(30, allList.Count - start);
        return allList.GetRange(start, count);
    }

    private static List<KnockoutStageDto> ParseKnockoutStagesFromLeagueDoc(JsonElement root)
    {
        var stages = new List<KnockoutStageDto>();
        if (root.ValueKind != JsonValueKind.Object) return stages;

        if (TryGetProp(root, "playoff", out var poElem) && poElem.ValueKind == JsonValueKind.Object &&
            TryGetProp(poElem, "rounds", out var roundsArr) && roundsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in roundsArr.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object) continue;

                string rawStage = TryGetProp(r, "stage", out var stElem) ? GetStringOrToString(stElem) ?? "" : "";
                int participantCount = TryGetProp(r, "participantCount", out var pcElem) && pcElem.ValueKind == JsonValueKind.Number ? pcElem.GetInt32() : 0;

                string stageName = NormalizeStageName(rawStage, participantCount);

                var ties = new List<KnockoutTieDto>();
                if (TryGetProp(r, "matchups", out var matchupsArr) && matchupsArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in matchupsArr.EnumerateArray())
                    {
                        if (m.ValueKind != JsonValueKind.Object) continue;

                        string homeName = TryGetProp(m, "homeTeam", out var ht) ? GetStringOrToString(ht) ?? "" : "";
                        string homeId = TryGetProp(m, "homeTeamId", out var htid) ? GetStringOrToString(htid) ?? "" : "";
                        string awayName = TryGetProp(m, "awayTeam", out var at) ? GetStringOrToString(at) ?? "" : "";
                        string awayId = TryGetProp(m, "awayTeamId", out var atid) ? GetStringOrToString(atid) ?? "" : "";

                        // If matchup is an unassigned slot in a drawn bracket
                        if (string.IsNullOrWhiteSpace(homeName) && string.IsNullOrWhiteSpace(awayName))
                        {
                            homeName = "TBD";
                            awayName = "TBD";
                        }

                        string? homeLogo = (!string.IsNullOrWhiteSpace(homeId) && homeId != "-1" && homeId != "0")
                            ? $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/{homeId}.png"
                            : null;
                        string? awayLogo = (!string.IsNullOrWhiteSpace(awayId) && awayId != "-1" && awayId != "0")
                            ? $"{GetDefaultUpstreamImageBaseUrl()}/image_resources/logo/teamlogo/{awayId}.png"
                            : null;

                        string? aggScore = null;
                        if (TryGetProp(m, "aggregatedResult", out var arObj) && arObj.ValueKind == JsonValueKind.Object)
                        {
                            int ah = TryGetProp(arObj, "homeScore", out var ahs) ? ParseInt(ahs) ?? 0 : 0;
                            int aa = TryGetProp(arObj, "awayScore", out var aas) ? ParseInt(aas) ?? 0 : 0;
                            if (ah > 0 || aa > 0) aggScore = $"{ah} - {aa} (Agg)";
                        }

                        string? leg1 = null;
                        string? leg2 = null;
                        bool isFin = false;
                        string? winner = null;
                        if (TryGetProp(m, "aggregatedWinner", out var awElem))
                        {
                            var aw = GetStringOrToString(awElem);
                            if (!string.IsNullOrWhiteSpace(aw)) winner = aw;
                        }

                        if (TryGetProp(m, "matches", out var mArr) && mArr.ValueKind == JsonValueKind.Array)
                        {
                            int legIdx = 0;
                            foreach (var match in mArr.EnumerateArray())
                            {
                                int lh = TryGetProp(match, "home", out var lmh) && TryGetProp(lmh, "score", out var lmhs) ? ParseInt(lmhs) ?? 0 : 0;
                                int la = TryGetProp(match, "away", out var lma) && TryGetProp(lma, "score", out var lmas) ? ParseInt(lmas) ?? 0 : 0;
                                bool lFin = TryGetProp(match, "status", out var lst) && TryGetProp(lst, "finished", out var lfin) && lfin.ValueKind == JsonValueKind.True;
                                if (lFin) isFin = true;
                                if (legIdx == 0) leg1 = $"{lh} - {la}";
                                else if (legIdx == 1) leg2 = $"{lh} - {la}";

                                if (string.IsNullOrWhiteSpace(winner) && TryGetProp(match, "home", out var lmh2) && TryGetProp(lmh2, "winner", out var hw) && hw.ValueKind == JsonValueKind.True) winner = homeName;
                                else if (string.IsNullOrWhiteSpace(winner) && TryGetProp(match, "away", out var lma2) && TryGetProp(lma2, "winner", out var aw2) && aw2.ValueKind == JsonValueKind.True) winner = awayName;

                                legIdx++;
                            }
                        }

                        string displayScore = !string.IsNullOrWhiteSpace(aggScore)
                            ? aggScore
                            : (!string.IsNullOrWhiteSpace(leg1) ? leg1 : "vs");

                        ties.Add(new KnockoutTieDto
                        {
                            StageName = stageName,
                            HomeTeamId = homeId,
                            HomeTeamName = homeName,
                            HomeTeamLogo = homeLogo,
                            AwayTeamId = awayId,
                            AwayTeamName = awayName,
                            AwayTeamLogo = awayLogo,
                            Leg1Score = leg1,
                            Leg2Score = leg2,
                            AggregateScore = displayScore,
                            ScoreOrStatus = displayScore,
                            WinnerTeamName = winner,
                            IsFinished = isFin
                        });
                    }
                }

                if (ties.Count > 0)
                {
                    stages.Add(new KnockoutStageDto
                    {
                        StageName = stageName,
                        ParticipantCount = participantCount,
                        Ties = ties
                    });
                }
            }
        }

        return stages;
    }

    private static string NormalizeStageName(string rawStage, int participantCount)
    {
        string lower = rawStage.Trim().ToLowerInvariant();
        if (lower == "final" || lower == "finals") return "Final";
        if (lower == "1/2" || lower.Contains("semi")) return "Semi-Finals";
        if (lower == "1/4" || lower.Contains("quarter")) return "Quarter-Finals";
        if (lower == "1/8" || lower == "r16" || lower.Contains("round of 16") || participantCount == 16) return "Round of 16";
        if (lower == "1/16" || lower == "r32" || lower.Contains("round of 32") || participantCount == 32) return "Round of 32";
        if (lower == "playoff" || lower == "playoffs") return "Playoff Round";
        if (int.TryParse(lower, out int rNum)) return $"Round {rNum}";
        if (string.IsNullOrWhiteSpace(rawStage)) return "Knockout Round";
        return char.ToUpperInvariant(rawStage[0]) + rawStage.Substring(1);
    }
}