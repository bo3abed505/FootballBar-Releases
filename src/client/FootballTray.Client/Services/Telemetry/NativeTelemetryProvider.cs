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
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services.Telemetry;

/// <summary>
/// In-process native C# telemetry engine that directly communicates with live sports telemetry
/// endpoints. Fulfills the entire client API contract (Tray fixtures, Match Details, Competition Hub,
/// and Catalog Search) with zero external daemon runtime dependency.
/// </summary>
public sealed class NativeTelemetryProvider : IDisposable
{
    private const string UpstreamPrimaryBase = "https://www.fotmob.com/api/data";
    private const string UpstreamFallbackBase = "https://www.fotmob.com/api";
    private const string TelemetryProviderName = "live-telemetry";

    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;

    // Short-TTL in-memory caches to prevent redundant upstream requests
    private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, TrayApiResponse Response)> _trayCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, MatchDetailDto Data)> _matchDetailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, CompetitionHubDto Data)> _hubCache = new(StringComparer.OrdinalIgnoreCase);

    // In-memory catalog indices populated from loaded matches
    private readonly ConcurrentDictionary<string, V2CatalogTeamDto> _teamCatalog = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, V2CatalogCompetitionDto> _compCatalog = new(StringComparer.OrdinalIgnoreCase);

    // Static priority competition definitions matching canonical system
    private static readonly Dictionary<string, int> CompetitionSlugToFotmobId = new(StringComparer.OrdinalIgnoreCase)
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
        ["fa_cup"] = 138,
        ["fa-cup"] = 138,
        ["fa cup"] = 138,
        ["facup"] = 138,
        ["copa_del_rey"] = 137,
        ["copa-del-rey"] = 137,
        ["copa del rey"] = 137,
        ["copadelrey"] = 137,
        ["coppa_italia"] = 141,
        ["coppa-italia"] = 141,
        ["coppa italia"] = 141,
        ["coppaitalia"] = 141,
        ["dfb_pokal"] = 209,
        ["dfb-pokal"] = 209,
        ["dfb pokal"] = 209,
        ["dfbpokal"] = 209,
        ["coupe_de_france"] = 142,
        ["coupe-de-france"] = 142,
        ["coupe de france"] = 142,
        ["coupedefrance"] = 142,
        ["egyptian_premier_league"] = 525,
        ["egyptian-premier-league"] = 525,
        ["egyptian premier league"] = 525,
        ["egyptianpremierleague"] = 525,
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
        ["africa_cup_of_nations"] = 132,
        ["africa-cup-of-nations"] = 132,
        ["africa cup of nations"] = 132,
        ["afcon"] = 132
    };

    private static readonly Dictionary<string, int> CanonicalCompetitionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ft_comp_01M33DKNCCNEJAG93CDAGJSR4A"] = 47, // Premier League
        ["ft_comp_01M33DKNC8CKGMJ9Z7898AH8HA"] = 87, // La Liga
        ["ft_comp_01M33DKNCABRRYR4K6D9DKTP40"] = 525, // Egyptian Premier League
        ["ft_comp_01M33DKNC97EMTT3FNZR1YTC9E"] = 42, // Champions League
        ["ft_comp_01M33DKNC7HY1Q85DNPB713V5Y"] = 73, // Europa League
        ["ft_comp_01M33DKNC6Q5V0Q4D89Z37F8W8"] = 10216, // Conference League
        ["ft_comp_01M33DKNC5E4N9Q4P4Y9A9Q2G7"] = 55, // Serie A
        ["ft_comp_01M33DKNC4D3M8P3N3X8Z8P1F6"] = 54, // Bundesliga
        ["ft_comp_01M33DKNC3C2L7N2M2W7Y7O0E5"] = 53, // Ligue 1
        ["ft_comp_01M33DKNC2B1K6M1L1V6X6N9D4"] = 138, // FA Cup
        ["ft_comp_01M33DKNC1A0J5L0K0U5W5M8C3"] = 137, // Copa del Rey
        ["ft_comp_01M33DKNC09ZH4KZJZTZV4L7B2"] = 536, // Saudi Pro League
        ["ft_comp_01M33DKNBZ8YG3JYIYSYU3K6A1"] = 130, // Major League Soccer
        ["ft_comp_01M33DKNCDV0T7DEEHHZ20DMN7"] = 132, // Africa Cup of Nations
        ["ft_comp_01M33DKNBZRHDX2EN9YQJPSV5Q"] = 77, // FIFA World Cup
        ["ft_comp_01M3Y7E15VJYGS76SM21GBR1YD"] = 50, // European Championship
        ["ft_comp_01M3Y7E15Z5X08Y7S7K0JPAGE6"] = 9806, // UEFA Nations League
        ["ft_comp_01M3Y7E1608TK80WDRTGYQHF2X"] = 44, // Copa America
        ["ft_comp_01M467Y24DRWPKFG9AX4XX88ZE"] = 9806, // Legacy Nations League
        ["comp_39"] = 47,
        ["comp_140"] = 87,
        ["comp_233"] = 525,
        ["comp_2"] = 42,
        ["comp_3"] = 73,
        ["comp_848"] = 10216,
        ["comp_135"] = 55,
        ["comp_78"] = 54,
        ["comp_61"] = 53,
        ["comp_45"] = 138,
        ["comp_143"] = 137,
        ["comp_307"] = 536,
        ["comp_253"] = 130,
        ["comp_6"] = 132,
        ["comp_1"] = 77,
        ["comp_4"] = 50,
        ["comp_5"] = 9806,
        ["comp_9"] = 44
    };

    private static readonly Dictionary<string, int> CanonicalTeamMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ft_team_01M33DKNCTXK0YKJNJDDC15GS8"] = 8634, // Barcelona
        ["team_529"] = 8634,
        ["529"] = 8634,
        ["ft_team_01M33DKNCTXK0YKJNJDDC15GS9"] = 8633, // Real Madrid
        ["team_541"] = 8633,
        ["541"] = 8633,
        ["ft_team_01M33DKNCP7HVGD8FVFYW5YNAA"] = 9825, // Arsenal
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
        ["ft_team_01M33DKNCP7HVGD8FVFYW5YNAK"] = 9748, // Al Ahly
        ["team_1029"] = 9748,
        ["1029"] = 9748,
        ["team_1028"] = 9750, // Zamalek
        ["team_2939"] = 10214, // Al Hilal
        ["team_2938"] = 10215, // Al Nassr
        ["team_2940"] = 10213 // Al Ittihad
    };

    private static readonly Dictionary<int, string> FotmobIdToCanonicalMap = CanonicalCompetitionMap
        .Where(kvp => kvp.Key.StartsWith("ft_comp_", StringComparison.OrdinalIgnoreCase))
        .GroupBy(kvp => kvp.Value)
        .ToDictionary(g => g.Key, g => g.First().Key);

    private static readonly Dictionary<int, string> FotmobTeamIdToCanonicalMap = CanonicalTeamMap
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
        ("9825", "Arsenal", "ARS", "England", "https://images.fotmob.com/image_resources/logo/teamlogo/9825.png"),
        ("10260", "Manchester United", "MUN", "England", "https://images.fotmob.com/image_resources/logo/teamlogo/10260.png"),
        ("8456", "Manchester City", "MCI", "England", "https://images.fotmob.com/image_resources/logo/teamlogo/8456.png"),
        ("8650", "Liverpool", "LIV", "England", "https://images.fotmob.com/image_resources/logo/teamlogo/8650.png"),
        ("8455", "Chelsea", "CHE", "England", "https://images.fotmob.com/image_resources/logo/teamlogo/8455.png"),
        ("8586", "Tottenham Hotspur", "TOT", "England", "https://images.fotmob.com/image_resources/logo/teamlogo/8586.png"),
        ("10261", "Newcastle United", "NEW", "England", "https://images.fotmob.com/image_resources/logo/teamlogo/10261.png"),
        ("10252", "Aston Villa", "AVL", "England", "https://images.fotmob.com/image_resources/logo/teamlogo/10252.png"),
        ("8633", "Real Madrid", "RMA", "Spain", "https://images.fotmob.com/image_resources/logo/teamlogo/8633.png"),
        ("8634", "Barcelona", "BAR", "Spain", "https://images.fotmob.com/image_resources/logo/teamlogo/8634.png"),
        ("9906", "Atletico Madrid", "ATM", "Spain", "https://images.fotmob.com/image_resources/logo/teamlogo/9906.png"),
        ("8686", "AS Roma", "ASR", "Italy", "https://images.fotmob.com/image_resources/logo/teamlogo/8686.png"),
        ("9885", "Juventus", "JUV", "Italy", "https://images.fotmob.com/image_resources/logo/teamlogo/9885.png"),
        ("8636", "Inter", "INT", "Italy", "https://images.fotmob.com/image_resources/logo/teamlogo/8636.png"),
        ("8564", "AC Milan", "MIL", "Italy", "https://images.fotmob.com/image_resources/logo/teamlogo/8564.png"),
        ("9823", "Bayern Munich", "BAY", "Germany", "https://images.fotmob.com/image_resources/logo/teamlogo/9823.png"),
        ("9789", "Borussia Dortmund", "BVB", "Germany", "https://images.fotmob.com/image_resources/logo/teamlogo/9789.png"),
        ("8178", "Bayer Leverkusen", "B04", "Germany", "https://images.fotmob.com/image_resources/logo/teamlogo/8178.png"),
        ("9847", "Paris Saint-Germain", "PSG", "France", "https://images.fotmob.com/image_resources/logo/teamlogo/9847.png"),
        ("9748", "Al Ahly", "AHL", "Egypt", "https://images.fotmob.com/image_resources/logo/teamlogo/9748.png"),
        ("9750", "Zamalek", "ZAM", "Egypt", "https://images.fotmob.com/image_resources/logo/teamlogo/9750.png"),
        ("10214", "Al Hilal", "HIL", "Saudi Arabia", "https://images.fotmob.com/image_resources/logo/teamlogo/10214.png"),
        ("10215", "Al Nassr", "NAS", "Saudi Arabia", "https://images.fotmob.com/image_resources/logo/teamlogo/10215.png"),
        ("10213", "Al Ittihad", "ITT", "Saudi Arabia", "https://images.fotmob.com/image_resources/logo/teamlogo/10213.png")
    };

    private static readonly (string Id, string Name, string Code, string Country, string LogoUrl)[] BuiltInTopCompetitions =
    {
        ("47", "Premier League", "PL", "ENG", "https://images.fotmob.com/image_resources/logo/leaguelogo/dark/47.png"),
        ("42", "UEFA Champions League", "UCL", "INT", "https://images.fotmob.com/image_resources/logo/leaguelogo/dark/42.png"),
        ("73", "UEFA Europa League", "UEL", "INT", "https://images.fotmob.com/image_resources/logo/leaguelogo/dark/73.png"),
        ("10216", "UEFA Conference League", "UECL", "INT", "https://images.fotmob.com/image_resources/logo/leaguelogo/dark/10216.png"),
        ("87", "La Liga", "PD", "ESP", "https://images.fotmob.com/image_resources/logo/leaguelogo/dark/87.png"),
        ("55", "Serie A", "SA", "ITA", "https://images.fotmob.com/image_resources/logo/leaguelogo/dark/55.png"),
        ("54", "Bundesliga", "BL1", "GER", "https://images.fotmob.com/image_resources/logo/leaguelogo/dark/54.png"),
        ("53", "Ligue 1", "FL1", "FRA", "https://images.fotmob.com/image_resources/logo/leaguelogo/dark/53.png"),
        ("138", "FA Cup", "FAC", "ENG", "https://images.fotmob.com/image_resources/logo/leaguelogo/dark/138.png"),
        ("137", "Copa del Rey", "CDR", "ESP", "https://images.fotmob.com/image_resources/logo/leaguelogo/dark/137.png"),
        ("536", "Saudi Pro League", "SPL", "KSA", "https://images.fotmob.com/image_resources/logo/leaguelogo/dark/536.png"),
        ("525", "Egyptian Premier League", "EPL", "EGY", "https://images.fotmob.com/image_resources/logo/leaguelogo/dark/525.png"),
        ("130", "Major League Soccer", "MLS", "USA", "https://images.fotmob.com/image_resources/logo/leaguelogo/dark/130.png")
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
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.fotmob.com/");
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

    private static string FormatEndpoint(string? template, string token, string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(template)) return fallback;
        return template.Replace(token, value, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildTeamLogoUrl(string id)
    {
        var template = OtaConfigService.Instance.CurrentManifest.Endpoints?.TeamLogoUrlTemplate;
        if (string.IsNullOrWhiteSpace(template))
        {
            return $"https://images.fotmob.com/image_resources/logo/teamlogo/{id}.png";
        }
        return template.Replace("{teamId}", id, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildLeagueLogoUrl(string id)
    {
        var template = OtaConfigService.Instance.CurrentManifest.Endpoints?.LeagueLogoUrlTemplate;
        if (string.IsNullOrWhiteSpace(template))
        {
            return $"https://images.fotmob.com/image_resources/logo/leaguelogo/dark/{id}.png";
        }
        return template.Replace("{leagueId}", id, StringComparison.OrdinalIgnoreCase);
    }

    #region 1. GetTrayForDateAsync

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
            ? DateTime.UtcNow.ToString("yyyyMMdd")
            : dateStr.Replace("-", "").Trim();

        string cacheKey = $"{cleanDate}_{string.Join(",", teams ?? Array.Empty<string>())}_{string.Join(",", competitions ?? Array.Empty<string>())}_{pinnedMatchId}";

        // Fast TTL cache check (12 seconds)
        if (_trayCache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromSeconds(12))
        {
            if (!string.IsNullOrWhiteSpace(ifNoneMatchEtag) &&
                IsEtagMatch(ifNoneMatchEtag, cached.Response.ETag))
            {
                return TrayApiResponse.NotModified(cached.Response.ETag);
            }
            return cached.Response;
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

            var matches = new List<TrayMatchDto>();
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
                        if (FotmobTeamIdToCanonicalMap.TryGetValue(fTeamId, out var ctId))
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
                    int? fId = ResolveFotmobLeagueId(c);
                    if (fId.HasValue)
                    {
                        followedLeagueIds.Add(fId.Value.ToString());
                        if (FotmobIdToCanonicalMap.TryGetValue(fId.Value, out var cId))
                        {
                            followedLeagueIds.Add(cId);
                        }
                    }
                }
            }

            foreach (var league in leaguesElem.EnumerateArray())
            {
                if (league.ValueKind != JsonValueKind.Object) continue;

                string leagueId = TryGetProp(league, "id", out var idElem) ? idElem.ToString() : string.Empty;
                string primaryId = TryGetProp(league, "primaryId", out var primElem) ? primElem.ToString() : leagueId;
                string logoLeagueId = !string.IsNullOrWhiteSpace(primaryId) && primaryId != "0" ? primaryId : leagueId;
                string leagueName = TryGetProp(league, "name", out var nameElem) ? nameElem.GetString() ?? "Competition" : "Competition";
                string? ccode = TryGetProp(league, "ccode", out var ccElem) ? ccElem.GetString()?.ToUpperInvariant() : null;

                string effectiveCompId = !string.IsNullOrWhiteSpace(primaryId) ? primaryId : leagueId;
                string legacyCompId = effectiveCompId;
                if (int.TryParse(effectiveCompId, out int numCompId) && FotmobIdToCanonicalMap.TryGetValue(numCompId, out var canonicalCompId))
                {
                    effectiveCompId = canonicalCompId;
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

                    string awayId = TryGetProp(awayElem, "id", out var aid) ? aid.ToString() : string.Empty;
                    string awayName = TryGetProp(awayElem, "name", out var aname) ? aname.GetString() ?? "Away" : "Away";
                    string? awayLong = TryGetProp(awayElem, "longName", out var along) ? along.GetString() : null;
                    string? awayTla = TryGetProp(awayElem, "tla", out var atla) ? atla.GetString() : null;
                    int? awayScore = TryGetProp(awayElem, "score", out var asc) && asc.ValueKind == JsonValueKind.Number ? asc.GetInt32() : null;

                    string effHomeId = homeId;
                    string legacyHomeId = homeId;
                    if (int.TryParse(homeId, out int numHId) && FotmobTeamIdToCanonicalMap.TryGetValue(numHId, out var canonHId))
                    {
                        effHomeId = canonHId;
                    }

                    string effAwayId = awayId;
                    string legacyAwayId = awayId;
                    if (int.TryParse(awayId, out int numAId) && FotmobTeamIdToCanonicalMap.TryGetValue(numAId, out var canonAId))
                    {
                        effAwayId = canonAId;
                    }

                    var homeTeamDto = new TeamSummaryDto
                    {
                        Id = effHomeId,
                        LegacyId = legacyHomeId,
                        Name = homeName,
                        ShortName = DeriveTla(homeLong ?? homeName, homeTla),
                        LogoUrl = BuildTeamLogoUrl(homeId)
                    };

                    var awayTeamDto = new TeamSummaryDto
                    {
                        Id = effAwayId,
                        LegacyId = legacyAwayId,
                        Name = awayName,
                        ShortName = DeriveTla(awayLong ?? awayName, awayTla),
                        LogoUrl = BuildTeamLogoUrl(awayId)
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
                        Id = $"fotmob_{matchId}",
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

                bool aLive = IsLiveState(a.State);
                bool bLive = IsLiveState(b.State);
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
                Data = new TrayDataDto
                {
                    Matches = matches,
                    FollowedTeamCompetitions = followedTeamComps,
                    QuotaExhausted = false,
                    CachedFallback = false,
                    IsNotModified = false
                }
            };

            AppLogger.Info("Telemetry", "TrayMatchesNormalized", $"Normalized {matches.Count} matches across {leaguesElem.GetArrayLength()} leagues for {cleanDate} (ETag: {etag[..Math.Min(16, etag.Length)]}...).");

            var successResponse = TrayApiResponse.Success(envelope, etag);
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

    #endregion

    #region 2. GetMatchDetailsAsync

    /// <summary>
    /// Fetches comprehensive match center details (header, events, commentary, statistics,
    /// lineups with 1000x600 5-anchor pitch grid, and embedded standings/leaders).
    /// </summary>
    public async Task<MatchDetailDto?> GetMatchDetailsAsync(string matchId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(matchId)) return null;

        string numericId = Regex.Match(matchId, @"\d+").Value;
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

            JsonElement statusElem = default;
            if (headerElem.ValueKind == JsonValueKind.Object)
            {
                TryGetProp(headerElem, "status", out statusElem);
            }
            var (matchStatus, minute, addedTime, displayClock) = ParseLiveTime(statusElem);

            string effectiveCompId = effectiveLeagueId;
            string legacyCompId = effectiveLeagueId;
            if (int.TryParse(effectiveLeagueId, out int numEffLId) && FotmobIdToCanonicalMap.TryGetValue(numEffLId, out var canonEffCompId))
            {
                effectiveCompId = canonEffCompId;
            }

            var competitionSummary = new CompetitionSummaryDto
            {
                Id = effectiveCompId,
                LegacyId = legacyCompId,
                Name = compName,
                LogoUrl = $"https://images.fotmob.com/image_resources/logo/leaguelogo/dark/{effectiveLeagueId}.png"
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

            // Commentary & Whistle Markers
            var commentary = ExtractCommentary(contentElem, headerElem, events, homeTeam, awayTeam, mId, matchStatus, minute, homeScore, awayScore);

            // Statistics (Possession bar + comparison groups)
            var statistics = ExtractStatistics(contentElem, homeTeam.Id, awayTeam.Id, mId, homeScore, awayScore);

            // Lineups (5-anchor pitch grid assignment)
            var lineups = ExtractLineups(contentElem, homeTeam.Id, awayTeam.Id, mId);

            // Embedded Standings & Season Leaders
            var (standings, topScorers, topAssists) = await ResolveStandingsAndLeadersAsync(contentElem, effectiveLeagueId, homeTeam.Id, awayTeam.Id, ct);

            var detailDto = new MatchDetailDto
            {
                Id = $"fotmob_{mId}",
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
                Standings = standings,
                TopScorers = topScorers,
                TopAssists = topAssists,
                Trust = new V2TrustMetadataDto
                {
                    Provider = TelemetryProviderName,
                    FreshnessState = "FRESH",
                    DataAgeSeconds = 0,
                    SourceObservedAt = DateTimeOffset.UtcNow
                }
            };

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

    #region 3. GetCompetitionHubAsync

    /// <summary>
    /// Resolves the league ID and fetches full standings, top scorers, and top assists.
    /// Results are cached for 15 minutes.
    /// </summary>
    public async Task<CompetitionHubDto?> GetCompetitionHubAsync(
        string competitionId,
        string? name = null,
        string? logoUrl = null,
        CancellationToken ct = default)
    {
        int? fotmobLeagueId = ResolveFotmobLeagueId(competitionId, name, logoUrl);
        if (!fotmobLeagueId.HasValue) return null;

        string cacheKey = fotmobLeagueId.Value.ToString();
        if (_hubCache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromMinutes(15))
        {
            return cached.Data;
        }

        try
        {
            string url = FormatEndpoint(
                OtaConfigService.Instance.CurrentManifest.Endpoints?.LeagueHubUrlTemplate,
                "{leagueId}",
                fotmobLeagueId.Value.ToString(),
                $"{UpstreamPrimaryBase}/leagues?id={fotmobLeagueId.Value}");
            string? json = await FetchJsonResilientAsync(url, ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                url = FormatEndpoint(
                    OtaConfigService.Instance.CurrentManifest.Endpoints?.LeagueHubFallbackUrlTemplate,
                    "{leagueId}",
                    fotmobLeagueId.Value.ToString(),
                    $"{UpstreamFallbackBase}/leagues?id={fotmobLeagueId.Value}");
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
                if (TryGetProp(detailsElem, "name", out var nElem) && !string.IsNullOrWhiteSpace(nElem.GetString()))
                {
                    compName = nElem.GetString()!;
                }
                if (TryGetProp(detailsElem, "country", out var cElem))
                {
                    countryCode = cElem.GetString();
                }
                if (TryGetProp(detailsElem, "selectedSeason", out var sElem))
                {
                    season = sElem.GetString();
                }
            }

            var standings = ParseStandingsFromLeagueDoc(root);
            var (topScorers, topAssists) = await ParseLeadersFromLeagueDocAsync(root, ct);

            var hubDto = new CompetitionHubDto
            {
                CompetitionId = competitionId,
                CompetitionName = compName,
                CompetitionCode = countryCode,
                CompetitionLogoUrl = $"https://images.fotmob.com/image_resources/logo/leaguelogo/dark/{fotmobLeagueId.Value}.png",
                CountryCode = countryCode,
                Season = season,
                Standings = standings,
                TopScorers = topScorers,
                TopAssists = topAssists
            };

            _hubCache[cacheKey] = (DateTimeOffset.UtcNow, hubDto);
            _hubCache[competitionId] = (DateTimeOffset.UtcNow, hubDto);

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

    #endregion

    #region 4. SearchTeamsAsync & SearchCompetitionsAsync

    /// <summary>
    /// Searches teams across memory index, curated dictionary, and upstream suggestion API.
    /// </summary>
    public async Task<List<V2CatalogTeamDto>> SearchTeamsAsync(string query, int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<V2CatalogTeamDto>();

        var results = new Dictionary<string, V2CatalogTeamDto>(StringComparer.OrdinalIgnoreCase);
        string q = query.Trim();

        // 1. Search in-memory catalog
        foreach (var team in _teamCatalog.Values)
        {
            if (team.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(team.ShortName) && team.ShortName.Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                results[team.Id] = team;
            }
        }

        // 2. Upstream suggestion fallback if not reached limit
        if (results.Count < limit)
        {
            try
            {
                string url = FormatEndpoint(
                    OtaConfigService.Instance.CurrentManifest.Endpoints?.SearchSuggestUrlTemplate,
                    "{term}",
                    Uri.EscapeDataString(q),
                    $"{UpstreamFallbackBase}/search/suggest?term={Uri.EscapeDataString(q)}");
                string? json = await FetchJsonResilientAsync(url, ct, timeoutSeconds: 3);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    if (TryGetProp(doc.RootElement, "team", out var teamArr) && teamArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in teamArr.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object) continue;
                            if (!TryGetProp(item, "id", out var idElem)) continue;
                            string id = idElem.ToString();
                            string name = TryGetProp(item, "name", out var n) ? n.GetString() ?? "" : "";
                            if (string.IsNullOrWhiteSpace(name)) continue;

                            string logo = BuildTeamLogoUrl(id);
                            results[id] = new V2CatalogTeamDto
                            {
                                Id = id,
                                LegacyId = id,
                                Name = name,
                                ShortName = DeriveTla(name),
                                Logo = logo,
                                LogoUrl = logo
                            };
                            if (results.Count >= limit * 2) break;
                        }
                    }
                }
            }
            catch
            {
                // Non-blocking fallback
            }
        }

        return results.Values.Take(limit).ToList();
    }

    /// <summary>
    /// Searches competitions across memory index, curated dictionary, and upstream suggestion API.
    /// </summary>
    public async Task<List<V2CatalogCompetitionDto>> SearchCompetitionsAsync(string query, int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<V2CatalogCompetitionDto>();

        var results = new Dictionary<string, V2CatalogCompetitionDto>(StringComparer.OrdinalIgnoreCase);
        string q = query.Trim();

        // 1. Search in-memory catalog
        foreach (var comp in _compCatalog.Values)
        {
            if (comp.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(comp.Code) && comp.Code.Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                results[comp.Id] = comp;
            }
        }

        // 2. Upstream suggestion fallback if not reached limit
        if (results.Count < limit)
        {
            try
            {
                string url = FormatEndpoint(
                    OtaConfigService.Instance.CurrentManifest.Endpoints?.SearchSuggestUrlTemplate,
                    "{term}",
                    Uri.EscapeDataString(q),
                    $"{UpstreamFallbackBase}/search/suggest?term={Uri.EscapeDataString(q)}");
                string? json = await FetchJsonResilientAsync(url, ct, timeoutSeconds: 3);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    if (TryGetProp(doc.RootElement, "league", out var leagueArr) && leagueArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in leagueArr.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object) continue;
                            if (!TryGetProp(item, "id", out var idElem)) continue;
                            string id = idElem.ToString();
                            string name = TryGetProp(item, "name", out var n) ? n.GetString() ?? "" : "";
                            if (string.IsNullOrWhiteSpace(name)) continue;

                            string logo = BuildLeagueLogoUrl(id);
                            results[id] = new V2CatalogCompetitionDto
                            {
                                Id = id,
                                LegacyId = id,
                                Name = name,
                                Logo = logo,
                                LogoUrl = logo
                            };
                            if (results.Count >= limit * 2) break;
                        }
                    }
                }
            }
            catch
            {
                // Non-blocking fallback
            }
        }

        return results.Values.Take(limit).ToList();
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
            if (Regex.IsMatch(cleanExp, @"^[A-Za-z0-9]{2,4}$"))
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
        var allWords = Regex.Split(ascii, @"[\s\-–_.'’/&]+")
            .Select(w => Regex.Replace(w, @"[^A-Za-z0-9]", ""))
            .Where(w => w.Length > 0)
            .ToList();

        var words = allWords.Where(w => !TlaStopwords.Contains(w) && !Regex.IsMatch(w, @"^\d+$")).ToList();
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
    /// Resolves FotMob numeric competition ID using priority rules (LogoUrl -> Direct numeric -> Slug/alias -> Trailing digits).
    /// </summary>
    public static int? ResolveFotmobLeagueId(string competitionId, string? competitionName = null, string? logoUrl = null)
    {
        // 0. Extract numeric ID from logoUrl if present
        if (!string.IsNullOrWhiteSpace(logoUrl))
        {
            var logoMatch = Regex.Match(logoUrl, @"/leaguelogo/(?:dark|light)/(\d+)\.png", RegexOptions.IgnoreCase);
            if (!logoMatch.Success) logoMatch = Regex.Match(logoUrl, @"/(\d+)\.png", RegexOptions.IgnoreCase);
            if (logoMatch.Success && int.TryParse(logoMatch.Groups[1].Value, out int logoNum) && logoNum > 0)
            {
                return logoNum;
            }
        }

        string rawId = (competitionId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(rawId) && string.IsNullOrEmpty(competitionName)) return null;

        string lower = rawId.ToLowerInvariant();

        // 0.5 Check OTA competition overrides first
        var otaOverrides = OtaConfigService.Instance.CurrentManifest?.CompetitionOverrides;
        if (otaOverrides != null)
        {
            if (otaOverrides.TryGetValue(lower, out int otaSlugId)) return otaSlugId;
            if (otaOverrides.TryGetValue(rawId, out int otaRawId)) return otaRawId;
            string otaStripped = Regex.Replace(lower, @"^(?:ft_comp_|comp:fotmob:|fotmob:|comp_)", "");
            if (otaOverrides.TryGetValue(otaStripped, out int otaStrippedId)) return otaStrippedId;
        }

        // 0.8 Check canonical competition mapping
        if (CanonicalCompetitionMap.TryGetValue(lower, out int canonLowerId)) return canonLowerId;
        if (CanonicalCompetitionMap.TryGetValue(rawId, out int canonRawId)) return canonRawId;

        // 1. Direct slug match
        if (CompetitionSlugToFotmobId.TryGetValue(lower, out int slugId)) return slugId;

        string stripped = Regex.Replace(lower, @"^(?:ft_comp_|comp:fotmob:|fotmob:|comp_)", "");
        if (CompetitionSlugToFotmobId.TryGetValue(stripped, out int strippedId)) return strippedId;

        string normalizedSlug = stripped.Replace("-", "_").Replace(" ", "_");
        if (CompetitionSlugToFotmobId.TryGetValue(normalizedSlug, out int normSlugId)) return normSlugId;

        // 2. Direct numeric check
        var numMatch = Regex.Match(lower, @"^(?:comp:fotmob:|fotmob_|fotmob:|ft_comp_|comp_)?(\d+)$");
        if (numMatch.Success && int.TryParse(numMatch.Groups[1].Value, out int directNum) && directNum > 0)
        {
            return directNum;
        }

        // 3. Name check
        string nameLower = (competitionName ?? string.Empty).ToLowerInvariant().Trim();
        if (!string.IsNullOrEmpty(nameLower))
        {
            if (otaOverrides != null)
            {
                if (otaOverrides.TryGetValue(nameLower, out int otaNameId)) return otaNameId;
                string otaNameSlug = Regex.Replace(nameLower, @"[^a-z0-9]+", "_").Trim('_');
                if (otaOverrides.TryGetValue(otaNameSlug, out int otaNameSlugId)) return otaNameSlugId;
            }

            if (CompetitionSlugToFotmobId.TryGetValue(nameLower, out int nameId)) return nameId;
            string nameSlug = Regex.Replace(nameLower, @"[^a-z0-9]+", "_").Trim('_');
            if (CompetitionSlugToFotmobId.TryGetValue(nameSlug, out int nameSlugId)) return nameSlugId;
        }

        string combined = $"{lower} {nameLower}";
        if (combined.Contains("conference") || combined.Contains("uecl")) return 10216;
        if (combined.Contains("europa") || combined.Contains("uel")) return 73;
        if (combined.Contains("champions") || combined.Contains("ucl")) return 42;
        if (combined.Contains("premier") && combined.Contains("league") && !combined.Contains("egy")) return 47;
        if (combined.Contains("egypt")) return 525;
        if (combined.Contains("laliga") || combined.Contains("la liga") || combined.Contains("la_liga")) return 87;
        if (combined.Contains("serie a") || combined.Contains("serie_a")) return 55;
        if (combined.Contains("bundesliga")) return 54;
        if (combined.Contains("ligue 1") || combined.Contains("ligue_1")) return 53;
        if (combined.Contains("saudi") || combined.Contains("spl")) return 536;
        if (combined.Contains("mls") || combined.Contains("major league")) return 130;
        if (combined.Contains("eredivisie")) return 57;
        if (combined.Contains("portugal") || combined.Contains("primeira")) return 61;
        if (combined.Contains("championship")) return 48;
        if (combined.Contains("fa cup") || combined.Contains("fa_cup")) return 138;
        if (combined.Contains("copa del rey") || combined.Contains("copa_del_rey")) return 137;
        if (combined.Contains("coppa italia") || combined.Contains("coppa_italia")) return 141;
        if (combined.Contains("dfb")) return 209;
        if (combined.Contains("coupe de france")) return 142;

        // 4. Trailing digits (only for non-canonical hash IDs)
        if (!rawId.StartsWith("ft_", StringComparison.OrdinalIgnoreCase))
        {
            string digitsOnly = Regex.Replace(rawId, @"\D", "");
            if (digitsOnly.Length > 0 && int.TryParse(digitsOnly, out int trailingNum) && trailingNum > 0)
            {
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
            liveShort = Regex.Replace(ltsProp.GetString() ?? "", @"[\u2019\u2032\u00B4`'\u200E\u200F\s]", "").ToUpperInvariant().Trim();
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

            var clockMatch = Regex.Match(liveShort, @"^(\d+)(?:\+(\d+))?");
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

        string? canonicalHomeId = int.TryParse(hId, out int hNum) && FotmobTeamIdToCanonicalMap.TryGetValue(hNum, out var chId) ? chId : null;
        string? canonicalAwayId = int.TryParse(aId, out int aNum) && FotmobTeamIdToCanonicalMap.TryGetValue(aNum, out var caId) ? caId : null;

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
                if (typeLower.Contains("addedtime")) continue;

                int minute = TryGetProp(ev, "time", out var timeProp) && timeProp.ValueKind == JsonValueKind.Number ? timeProp.GetInt32() : 0;
                int extraMinute = TryGetProp(ev, "overloadTime", out var olProp) && olProp.ValueKind == JsonValueKind.Number ? olProp.GetInt32() : 0;

                bool isHome = TryGetProp(ev, "isHome", out var ihProp) && ihProp.ValueKind == JsonValueKind.True;
                bool isOwnGoal = TryGetProp(ev, "ownGoal", out var ogProp) && ogProp.ValueKind == JsonValueKind.True;

                // Beneficiary team logic: For own goals, the team whose score increments is the opponent!
                var beneficiaryTeam = isOwnGoal ? (isHome ? awayTeam : homeTeam) : (isHome ? homeTeam : awayTeam);

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
                    Description = detail ?? type
                });
            }
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
            string cType = ev.Type.ToLowerInvariant();
            string text = ev.Description ?? ev.Type;

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

        // 3. Direct commentary feed from liveticker / superlive
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

    private static List<TeamStatisticsDto> ExtractStatistics(
        JsonElement content,
        string homeTeamId,
        string awayTeamId,
        string matchId,
        int? homeScore,
        int? awayScore)
    {
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
                    if (!TryGetProp(row, "stats", out var valArr) || valArr.ValueKind != JsonValueKind.Array) continue;

                    var vals = valArr.EnumerateArray().ToList();
                    if (vals.Count < 2) continue;

                    int? hVal = ParseInt(vals[0]);
                    int? aVal = ParseInt(vals[1]);
                    double? hDbl = ParseDouble(vals[0]);
                    double? aDbl = ParseDouble(vals[1]);

                    if (key.Contains("possession"))
                    {
                        homeStats.PossessionPercent = hVal;
                        awayStats.PossessionPercent = aVal;
                        hasStats = true;
                    }
                    else if (key.Contains("xg") || key.Contains("expected_goals"))
                    {
                        homeStats.ExpectedGoals = hDbl;
                        awayStats.ExpectedGoals = aDbl;
                        hasStats = true;
                    }
                    else if (key.Contains("shots") && !key.Contains("target"))
                    {
                        homeStats.TotalShots = hVal;
                        awayStats.TotalShots = aVal;
                        hasStats = true;
                    }
                    else if (key.Contains("target") || key.Contains("on_goal"))
                    {
                        homeStats.ShotsOnGoal = hVal;
                        awayStats.ShotsOnGoal = aVal;
                        hasStats = true;
                    }
                    else if (key.Contains("corner"))
                    {
                        homeStats.CornerKicks = hVal;
                        awayStats.CornerKicks = aVal;
                        hasStats = true;
                    }
                    else if (key.Contains("foul"))
                    {
                        homeStats.Fouls = hVal;
                        awayStats.Fouls = aVal;
                        hasStats = true;
                    }
                    else if (key.Contains("offside"))
                    {
                        homeStats.Offsides = hVal;
                        awayStats.Offsides = aVal;
                        hasStats = true;
                    }
                    else if (key.Contains("yellow"))
                    {
                        homeStats.YellowCards = hVal;
                        awayStats.YellowCards = aVal;
                        hasStats = true;
                    }
                    else if (key.Contains("red"))
                    {
                        homeStats.RedCards = hVal;
                        awayStats.RedCards = aVal;
                        hasStats = true;
                    }
                    else if (key.Contains("save"))
                    {
                        homeStats.GoalkeeperSaves = hVal;
                        awayStats.GoalkeeperSaves = aVal;
                        hasStats = true;
                    }
                    else if (key.Contains("passes") && !key.Contains("accurate"))
                    {
                        homeStats.TotalPasses = hVal;
                        awayStats.TotalPasses = aVal;
                        hasStats = true;
                    }
                    else if (key.Contains("accurate"))
                    {
                        homeStats.PassesAccurate = hVal;
                        awayStats.PassesAccurate = aVal;
                        hasStats = true;
                    }
                }
            }
        }

        // Fallback baseline if stats were not populated
        if (!hasStats)
        {
            homeStats.PossessionPercent = 50;
            awayStats.PossessionPercent = 50;
            homeStats.TotalShots = homeScore ?? 0;
            awayStats.TotalShots = awayScore ?? 0;
            homeStats.ShotsOnGoal = homeScore ?? 0;
            awayStats.ShotsOnGoal = awayScore ?? 0;
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

        if (TryGetProp(teamElem, "substitutes", out var subsElem) && subsElem.ValueKind == JsonValueKind.Array)
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

        bool isCaptain = TryGetProp(p, "captain", out var cap) && cap.ValueKind == JsonValueKind.True;
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

        var parts = Regex.Split(formation, @"[-\s]+")
            .Select(p => int.TryParse(p, out int n) ? n : 0)
            .Where(n => n > 0)
            .ToList();

        if (parts.Count == 0 || parts.Sum() != 10) return fallback;
        return parts;
    }

    private async Task<(List<StandingRowDto>? Standings, List<PlayerStatLeaderDto>? TopScorers, List<PlayerStatLeaderDto>? TopAssists)>
        ResolveStandingsAndLeadersAsync(JsonElement content, string leagueId, string homeId, string awayId, CancellationToken ct)
    {
        // 1. Check if standings table is directly embedded in match details (content.table)
        if (content.ValueKind == JsonValueKind.Object && TryGetProp(content, "table", out var tableElem) && tableElem.ValueKind == JsonValueKind.Array)
        {
            var standings = ParseStandingsFromTableElement(tableElem, homeId, awayId);
            if (standings.Count > 0)
            {
                return (standings, null, null);
            }
        }

        // 2. Fall back to competition hub
        if (!string.IsNullOrWhiteSpace(leagueId) && leagueId != "0")
        {
            try
            {
                var hub = await GetCompetitionHubAsync(leagueId, ct: ct);
                if (hub != null && hub.Standings?.Count > 0)
                {
                    var enrichedStandings = hub.Standings.Select(s => new StandingRowDto
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

                    return (enrichedStandings, hub.TopScorers, hub.TopAssists);
                }
            }
            catch
            {
                // Non-blocking fallback
            }
        }

        return (null, null, null);
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
                        if (TryGetProp(m, "resultString", out var rs)) badges.Append(rs.GetString());
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
            string teamId = TryGetProp(row, "id", out var idElem) ? idElem.ToString() : "";
            string teamName = TryGetProp(row, "name", out var nElem) ? nElem.GetString() ?? "" : "";
            string? shortName = TryGetProp(row, "shortName", out var snElem) ? snElem.GetString() : DeriveTla(teamName);

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
                        string teamId = TryGetProp(row, "id", out var idElem) ? idElem.ToString() : "";
                        string teamName = TryGetProp(row, "name", out var nElem) ? nElem.GetString() ?? "" : "";
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
            string name = TryGetProp(stat, "name", out var n) ? n.GetString() ?? "" : "";
            string header = TryGetProp(stat, "header", out var h) ? h.GetString()?.ToLowerInvariant() ?? "" : "";
            string? fetchAllUrl = TryGetProp(stat, "fetchAllUrl", out var fau) ? fau.GetString() : null;

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
                                    string pId = TryGetProp(item, "ParticipantId", out var pid) ? pid.ToString() : (TryGetProp(item, "id", out var idElem) ? idElem.ToString() : rk.ToString());
                                    string pName = TryGetProp(item, "ParticipantName", out var pn) ? pn.GetString() ?? "Player" : "Player";
                                    string tId = TryGetProp(item, "TeamId", out var tid) ? tid.ToString() : "";
                                    string tName = TryGetProp(item, "TeamName", out var tn) ? tn.GetString() ?? "" : "";
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
                    string pId = TryGetProp(item, "id", out var pid) ? pid.ToString() : rk.ToString();
                    string pName = TryGetProp(item, "name", out var pn) ? pn.GetString() ?? "Player" : "Player";
                    string tId = TryGetProp(item, "teamId", out var tid) ? tid.ToString() : "";
                    string tName = TryGetProp(item, "teamName", out var tn) ? tn.GetString() ?? "" : "";
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

    private static int GetCompetitionPriority(string? compId, string? compName)
    {
        var priorityList = OtaConfigService.Instance.CurrentManifest?.PriorityLeagueIds;
        if (priorityList != null && priorityList.Count > 0)
        {
            if (int.TryParse(compId, out int numericId))
            {
                int idx = priorityList.IndexOf(numericId);
                if (idx >= 0) return idx + 1;
            }
        }

        string id = (compId ?? "").Trim();
        string name = (compName ?? "").ToLowerInvariant();

        if (id == "47" || name.Contains("premier league")) return 1;
        if (id == "42" || name.Contains("champions league")) return 2;
        if (id == "87" || name.Contains("la liga") || name.Contains("laliga")) return 3;
        if (id == "55" || name.Contains("serie a")) return 4;
        if (id == "54" || name.Contains("bundesliga")) return 5;
        if (id == "53" || name.Contains("ligue 1")) return 6;
        if (id == "73" || name.Contains("europa league")) return 7;
        if (id == "10216" || name.Contains("conference league")) return 8;
        if (id == "525" || name.Contains("egypt")) return 9;
        if (id == "536" || name.Contains("saudi")) return 10;
        if (id == "130" || name.Contains("major league soccer")) return 11;
        return 100;
    }

    private static bool IsLiveState(string state) =>
        state is "LIVE" or "FIRST_HALF" or "HALFTIME" or "SECOND_HALF" or "EXTRA_TIME" or "EXTRA_TIME_HALFTIME" or "PENALTIES";

    private static bool IsStartedState(string state) =>
        !string.Equals(state, "PREMATCH", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(state, "SCHEDULED", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(state, "TIMED", StringComparison.OrdinalIgnoreCase);

    private static bool IsFinishedState(string state) =>
        state is "FINISHED" or "FINISHED_AET" or "FINISHED_PEN" or "FT" or "AET" or "PEN";

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
}
