using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using FootballTray.Client.Services.Preferences;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services.Telemetry;

/// <summary>
/// On-device tactical narrative synthesizer generating broadcast-grade match recaps
/// and high-signal statistical insights across Pre-Match, Live In-Play, and Concluded fixtures.
/// </summary>
public static class TacticalNarrativeSynthesizer
{
    public static void Synthesize(MatchDetailDto dto, IEnumerable<string>? extractedUpstreamInsights = null)
    {
        if (dto == null) return;

        string state = (dto.State ?? "PREMATCH").Trim().ToUpperInvariant();
        string home = !string.IsNullOrWhiteSpace(dto.HomeTeam?.Name) ? dto.HomeTeam.Name.Trim() : "Home Team";
        string away = !string.IsNullOrWhiteSpace(dto.AwayTeam?.Name) ? dto.AwayTeam.Name.Trim() : "Away Team";
        int hScore = dto.Score?.Home ?? 0;
        int aScore = dto.Score?.Away ?? 0;

        var insights = new List<string>();

        // Prepend clean extracted upstream insights
        if (extractedUpstreamInsights != null)
        {
            foreach (var item in extractedUpstreamInsights)
            {
                if (string.IsNullOrWhiteSpace(item)) continue;
                string clean = CleanInsightText(item);
                if (!string.IsNullOrWhiteSpace(clean) && !insights.Contains(clean, StringComparer.OrdinalIgnoreCase))
                {
                    insights.Add(clean);
                }
            }
        }

        if (FixtureRelevanceEngine.IsFinishedState(state))
        {
            SynthesizeFinished(dto, home, away, hScore, aScore, insights);
        }
        else if (FixtureRelevanceEngine.IsLiveState(state))
        {
            SynthesizeLive(dto, home, away, hScore, aScore, insights);
        }
        else
        {
            SynthesizePrematch(dto, home, away, insights);
        }

        // Cap insights at 4 high-signal items
        dto.KeyMatchInsights = insights.Take(4).ToList();
    }

    #region 1. Pre-Match Preview

    private static void SynthesizePrematch(MatchDetailDto dto, string home, string away, List<string> insights)
    {
        dto.TacticalRecapBadge = "PRE-MATCH PREVIEW";

        var sentences = new List<string>();
        var h2h = dto.H2HAndPredictions;

        // 1. Form Analysis
        string? homeFormStr = null;
        string? awayFormStr = null;
        if (h2h?.HomeForm != null && h2h.HomeForm.Count > 0)
        {
            int hWins = h2h.HomeForm.Count(f => f.Result == "W");
            int hLosses = h2h.HomeForm.Count(f => f.Result == "L");
            homeFormStr = string.Join("-", h2h.HomeForm.Take(5).Select(f => f.Result));

            if (hWins >= 3)
            {
                sentences.Add($"{home} enter on strong form with {hWins} {(hWins == 1 ? "win" : "wins")} from their last {h2h.HomeForm.Count} outings ({homeFormStr})");
            }
            else if (hLosses >= 3)
            {
                sentences.Add($"{home} look to steady the ship following a difficult run of {hLosses} {(hLosses == 1 ? "defeat" : "defeats")} in their last {h2h.HomeForm.Count} fixtures ({homeFormStr})");
            }
            else
            {
                sentences.Add($"{home} arrive with a balanced run of results across their last {h2h.HomeForm.Count} fixtures ({homeFormStr})");
            }
        }

        if (h2h?.AwayForm != null && h2h.AwayForm.Count > 0)
        {
            int aWins = h2h.AwayForm.Count(f => f.Result == "W");
            int aLosses = h2h.AwayForm.Count(f => f.Result == "L");
            awayFormStr = string.Join("-", h2h.AwayForm.Take(5).Select(f => f.Result));

            if (sentences.Count > 0)
            {
                string leadSentence = sentences[0];
                sentences.Clear();
                if (aWins >= 3)
                {
                    sentences.Add($"{leadSentence}, while {away} pose a serious threat having taken {aWins} {(aWins == 1 ? "victory" : "victories")} from their last {h2h.AwayForm.Count} games ({awayFormStr}).");
                }
                else if (aLosses >= 3)
                {
                    sentences.Add($"{leadSentence}, while {away} look to bounce back from a mixed run ({awayFormStr}).");
                }
                else
                {
                    sentences.Add($"{leadSentence}, while {away} carry a steady run ({awayFormStr}) into this clash.");
                }
            }
            else
            {
                sentences.Add($"{away} arrive with form {awayFormStr} across their last {h2h.AwayForm.Count} fixtures.");
            }
        }

        // 2. Head-to-Head Analysis
        if (h2h?.LastFiveMeetings != null && h2h.LastFiveMeetings.Count > 0)
        {
            int hWins = h2h.HomeWins;
            int aWins = h2h.AwayWins;
            int draws = h2h.Draws;
            double avgGoals = Math.Round(h2h.LastFiveMeetings.Average(m => m.HomeScore + m.AwayScore), 1);
            var latest = h2h.LastFiveMeetings[0];

            if (hWins > aWins)
            {
                sentences.Add($"Across their last {h2h.LastFiveMeetings.Count} head-to-head meetings, {home} hold the edge with {hWins} {(hWins == 1 ? "win" : "wins")} and {draws} {(draws == 1 ? "draw" : "draws")} (averaging {avgGoals:0.0} goals per match), including a {latest.ScoreDisplay} victory in their latest encounter.");
            }
            else if (aWins > hWins)
            {
                sentences.Add($"Across their last {h2h.LastFiveMeetings.Count} head-to-head meetings, {away} hold the advantage with {aWins} {(aWins == 1 ? "win" : "wins")} (averaging {avgGoals:0.0} goals per match), including a {latest.ScoreDisplay} result in their latest encounter.");
            }
            else
            {
                sentences.Add($"Head-to-head history is finely balanced across their last {h2h.LastFiveMeetings.Count} meetings ({hWins} wins for {home}, {aWins} wins for {away}, {draws} draws), producing an average of {avgGoals:0.0} goals per contest.");
            }
        }

        // Fallback preview if neither form nor H2H present
        if (sentences.Count == 0)
        {
            string comp = dto.Competition?.Name ?? "league action";
            string venuePart = !string.IsNullOrWhiteSpace(dto.Venue) ? $" at {dto.Venue}" : string.Empty;
            sentences.Add($"{home} host {away} in {comp}{venuePart}. Both sides will be eager to assert tactical control from kickoff.");
        }

        dto.TacticalRecapSummary = string.Join(" ", sentences);

        // Populate bullets
        if (homeFormStr != null && awayFormStr != null && insights.Count < 4)
        {
            insights.Add($"Form (Last 5): {home} ({homeFormStr}) vs {away} ({awayFormStr})");
        }
        if (h2h?.LastFiveMeetings != null && h2h.LastFiveMeetings.Count > 0 && insights.Count < 4)
        {
            insights.Add($"Head-to-Head: {h2h.HomeWins} {home} wins, {h2h.Draws} draws, {h2h.AwayWins} {away} wins");
        }
        if (!string.IsNullOrWhiteSpace(dto.Venue) && insights.Count < 4)
        {
            insights.Add($"Venue: {dto.Venue}");
        }
        if (!string.IsNullOrWhiteSpace(dto.Referee) && insights.Count < 4)
        {
            insights.Add($"Referee: {dto.Referee}");
        }
    }

    #endregion

    #region 2. Live In-Play Tactical Pulse

    private static void SynthesizeLive(MatchDetailDto dto, string home, string away, int hScore, int aScore, List<string> insights)
    {
        string phase = dto.ElapsedMinute > 0
            ? (dto.ExtraMinute > 0 ? $"{dto.ElapsedMinute}+{dto.ExtraMinute}'" : $"{dto.ElapsedMinute}'")
            : (dto.State ?? "LIVE");
        dto.TacticalRecapBadge = $"LIVE TACTICAL PULSE • {phase}";

        // Extract statistics
        var (hPoss, aPoss, hShots, aShots, hSog, aSog, hXg, aXg, hFouls, aFouls, hCards, aCards) = ExtractStatsTuple(dto);

        var sentences = new List<string>();

        // Goalscorers & Events context
        var goalEvents = dto.Events?.Where(e => string.Equals(e.Type, "Goal", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Minute).ToList() ?? new();
        var redEvents = dto.Events?.Where(e => string.Equals(e.Type, "RedCard", StringComparison.OrdinalIgnoreCase)).ToList() ?? new();

        bool homeLeading = hScore > aScore;
        bool awayLeading = aScore > hScore;
        bool isTied = hScore == aScore;

        if (awayLeading)
        {
            // Away leads
            if (hPoss >= 58 || hShots > aShots || (hXg.HasValue && aXg.HasValue && hXg > aXg))
            {
                sentences.Add($"{away} hold a {hScore}-{aScore} advantage, but {home} are applying sustained territorial pressure with {hPoss}% possession and {hShots} total attempts ({hSog} on target), pressing aggressively for an equalizer.");
            }
            else
            {
                sentences.Add($"{away} lead {hScore}-{aScore} and are controlling the tempo, restricting {home} in the attacking third while dictating transitions.");
            }
        }
        else if (homeLeading)
        {
            // Home leads
            if (aPoss >= 58 || aShots > hShots || (aXg.HasValue && hXg.HasValue && aXg > hXg))
            {
                sentences.Add($"{home} lead {hScore}-{aScore}, but {away} have seized tactical momentum with {aPoss}% possession and {aShots} attempts, ramping up pressure in pursuit of an equalizer.");
            }
            else
            {
                sentences.Add($"{home} hold a commanding {hScore}-{aScore} lead, dictating proceedings with {hPoss}% possession and {hShots} total shots to keep {away} under constant defensive strain.");
            }
        }
        else
        {
            // Tied
            if (hScore == 0 && aScore == 0)
            {
                if (hShots + aShots >= 8)
                {
                    sentences.Add($"An open and energetic contest locked at 0-0, with both sides trading chances ({hShots} shots for {home}, {aShots} for {away}) in search of a decisive breakthrough.");
                }
                else
                {
                    sentences.Add($"A tightly contested tactical duel locked at 0-0, with both sides prioritizing defensive solidity and limiting clear-cut openings ({hShots + aShots} total attempts).");
                }
            }
            else
            {
                sentences.Add($"Level at {hScore}-{aScore} after an eventful battle, with both sides actively pushing for a go-ahead goal.");
            }
        }

        // Active Red Cards
        if (redEvents.Count > 0)
        {
            var homeReds = redEvents.Count(r => IsTeamMatch(r.TeamId, dto.HomeTeam));
            var awayReds = redEvents.Count(r => IsTeamMatch(r.TeamId, dto.AwayTeam));
            if (homeReds > awayReds)
            {
                sentences.Add($"{home} must navigate the remainder of the contest with ten men following a red card.");
            }
            else if (awayReds > homeReds)
            {
                sentences.Add($"{away} are down to ten men after receiving a red card, handing {home} a vital numerical advantage.");
            }
        }

        dto.TacticalRecapSummary = string.Join(" ", sentences);

        // High-signal live bullets
        if (insights.Count < 4)
        {
            insights.Add($"Possession: {home} {hPoss}% – {aPoss}% {away}");
        }
        if (hXg.HasValue && aXg.HasValue && insights.Count < 4)
        {
            insights.Add($"Expected Goals (xG): {home} {hXg.Value:0.00} – {aXg.Value:0.00} {away}");
        }
        if ((hShots > 0 || aShots > 0) && insights.Count < 4)
        {
            insights.Add($"Shots (On Target): {home} {hShots} ({hSog}) – {aShots} ({aSog}) {away}");
        }
        if ((hCards > 0 || aCards > 0) && insights.Count < 4)
        {
            insights.Add($"Discipline: {hCards} cards for {home}, {aCards} for {away}");
        }
    }

    #endregion

    #region 3. Full-Time Match Recap

    private static void SynthesizeFinished(MatchDetailDto dto, string home, string away, int hScore, int aScore, List<string> insights)
    {
        dto.TacticalRecapBadge = "FULL-TIME MATCH RECAP";

        var (hPoss, aPoss, hShots, aShots, hSog, aSog, hXg, aXg, hFouls, aFouls, hCards, aCards) = ExtractStatsTuple(dto);
        var goalEvents = dto.Events?.Where(e => string.Equals(e.Type, "Goal", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Minute).ToList() ?? new();

        int htHome = goalEvents.Count(g => g.Minute <= 45 && IsTeamMatch(g.TeamId, dto.HomeTeam));
        int htAway = goalEvents.Count(g => g.Minute <= 45 && IsTeamMatch(g.TeamId, dto.AwayTeam));

        var sentences = new List<string>();

        bool homeWon = hScore > aScore;
        bool awayWon = aScore > hScore;
        bool isDraw = hScore == aScore;

        // Detect Second-Half Comeback
        bool homeComeback = homeWon && htAway > htHome;
        bool awayComeback = awayWon && htHome > htAway;

        // Detect Late Winner (>= 80')
        var decisiveGoal = goalEvents.LastOrDefault();
        bool hasLateWinner = decisiveGoal != null && decisiveGoal.Minute >= 80 && (homeWon || awayWon);

        if (homeComeback)
        {
            string decPart = hasLateWinner ? $" sealed by a dramatic {decisiveGoal!.Minute}' late decider" : string.Empty;
            sentences.Add($"{home} orchestrated a thrilling second-half turnaround to defeat {away} {hScore}-{aScore}, recovering from a {htHome}-{htAway} halftime deficit{decPart}.");
        }
        else if (awayComeback)
        {
            string decPart = hasLateWinner ? $" sealed by a dramatic {decisiveGoal!.Minute}' late decider" : string.Empty;
            sentences.Add($"{away} mounted an inspiring second-half comeback to overcome {home} {hScore}-{aScore}, overturning a {htHome}-{htAway} halftime deficit{decPart}.");
        }
        else if (homeWon)
        {
            if (hScore - aScore >= 3)
            {
                sentences.Add($"{home} delivered a commanding masterclass to comfortably defeat {away} {hScore}-{aScore}.");
            }
            else if (hasLateWinner)
            {
                sentences.Add($"{home} edged past {away} {hScore}-{aScore} courtesy of a clutch {decisiveGoal!.Minute}' late winner.");
            }
            else
            {
                sentences.Add($"{home} claimed all three points with a well-earned {hScore}-{aScore} victory over {away}.");
            }
        }
        else if (awayWon)
        {
            if (aScore - hScore >= 3)
            {
                sentences.Add($"{away} cruised to an emphatic {hScore}-{aScore} road victory against {home}.");
            }
            else if (hasLateWinner)
            {
                sentences.Add($"{away} snatched a dramatic {hScore}-{aScore} victory over {home} with a decisive {decisiveGoal!.Minute}' late goal.");
            }
            else
            {
                sentences.Add($"{away} secured a hard-fought {hScore}-{aScore} victory away at {home}.");
            }
        }
        else
        {
            if (hScore == 0)
            {
                sentences.Add($"{home} and {away} shared the spoils in a hard-fought 0-0 stalemate, with both defenses holding firm.");
            }
            else
            {
                sentences.Add($"A pulsating contest concluded with {home} and {away} deadlocked in a {hScore}-{aScore} draw.");
            }
        }

        // Key Contributors / Braces
        var topScorer = goalEvents
            .GroupBy(g => g.PlayerName ?? "Goalscorer")
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (topScorer != null && topScorer.Count() >= 2)
        {
            sentences.Add($"{topScorer.Key} proved decisive with a standout {topScorer.Count()}-goal haul.");
        }

        // Statistical Verdict & Player of the Match
        if (hXg.HasValue && aXg.HasValue)
        {
            sentences.Add($"Underlying numbers reflected final xG of {hXg.Value:0.00} to {aXg.Value:0.00} with {hPoss}% possession for {home}.");
        }

        // Fallback: Resolve Player of the Match from lineups if not already provided
        if (dto.PlayerOfTheMatch == null && dto.Lineups != null && dto.Lineups.Count > 0)
        {
            LineupPlayerDto? best = null;
            string bestTeam = home;
            foreach (var lineup in dto.Lineups)
            {
                string tName = IsTeamMatch(lineup.TeamId, dto.AwayTeam) ? away : home;
                var players = (lineup.StartingXI ?? Enumerable.Empty<LineupPlayerDto>())
                    .Concat(lineup.Substitutes ?? Enumerable.Empty<LineupPlayerDto>());
                foreach (var p in players)
                {
                    if (string.IsNullOrWhiteSpace(p.Name)) continue;
                    if (p.IsManOfTheMatch && p.Rating.GetValueOrDefault() >= 6.5)
                    {
                        best = p;
                        bestTeam = tName;
                        break;
                    }
                    if (p.Rating.HasValue && p.Rating.Value >= 7.5)
                    {
                        if (best == null || p.Rating.Value > best.Rating.GetValueOrDefault() ||
                            (p.Rating.Value == best.Rating.GetValueOrDefault() && (p.Goals > best.Goals || p.IsStarter)))
                        {
                            best = p;
                            bestTeam = tName;
                        }
                    }
                }
                if (best != null && best.IsManOfTheMatch) break;
            }

            if (best != null && best.Rating.GetValueOrDefault() >= 7.5)
            {
                string rStr = best.Rating.GetValueOrDefault().ToString("0.0", CultureInfo.InvariantCulture);
                var parts = new List<string> { $"Rating {rStr}" };
                if (best.Goals > 0) parts.Add($"{best.Goals} {(best.Goals == 1 ? "Goal" : "Goals")}");
                if (best.Assists > 0) parts.Add($"{best.Assists} {(best.Assists == 1 ? "Assist" : "Assists")}");
                dto.PlayerOfTheMatch = new PlayerOfTheMatchDto
                {
                    PlayerName = CleanInsightText(best.Name),
                    TeamName = bestTeam,
                    Rating = rStr,
                    SummaryText = string.Join(" • ", parts)
                };
            }
        }

        if (dto.PlayerOfTheMatch != null && !string.IsNullOrWhiteSpace(dto.PlayerOfTheMatch.PlayerName))
        {
            string ratingPart = !string.IsNullOrWhiteSpace(dto.PlayerOfTheMatch.Rating) ? $" ({dto.PlayerOfTheMatch.Rating} rating)" : string.Empty;
            sentences.Add($"Player of the Match honors were awarded to {dto.PlayerOfTheMatch.PlayerName}{ratingPart}.");
        }

        dto.TacticalRecapSummary = string.Join(" ", sentences);

        // Finished Bullets
        if (dto.PlayerOfTheMatch != null && !string.IsNullOrWhiteSpace(dto.PlayerOfTheMatch.PlayerName) && insights.Count < 4)
        {
            string motmText = !string.IsNullOrWhiteSpace(dto.PlayerOfTheMatch.Rating)
                ? $"{dto.PlayerOfTheMatch.PlayerName} (Rating: {dto.PlayerOfTheMatch.Rating})"
                : dto.PlayerOfTheMatch.PlayerName;
            insights.Add($"★ Player of the Match: {motmText}");
        }
        if (hXg.HasValue && aXg.HasValue && insights.Count < 4)
        {
            insights.Add($"Expected Goals (xG): {home} {hXg.Value:0.00} – {aXg.Value:0.00} {away}");
        }
        if ((hShots > 0 || aShots > 0) && insights.Count < 4)
        {
            insights.Add($"Total Shots: {home} {hShots} ({hSog} on target) – {away} {aShots} ({aSog} on target)");
        }
        if (insights.Count < 4)
        {
            insights.Add($"Possession: {home} {hPoss}% – {aPoss}% {away}");
        }
    }

    #endregion

    #region Helpers

    private static (int HPoss, int APoss, int HShots, int AShots, int HSog, int ASog, double? HXg, double? AXg, int HFouls, int AFouls, int HCards, int ACards)
        ExtractStatsTuple(MatchDetailDto dto)
    {
        TeamStatisticsDto? hStat = null;
        TeamStatisticsDto? aStat = null;

        if (dto.Statistics != null && dto.Statistics.Count > 0)
        {
            hStat = dto.Statistics.FirstOrDefault(s => IsTeamMatch(s.TeamId, dto.HomeTeam));
            aStat = dto.Statistics.FirstOrDefault(s => IsTeamMatch(s.TeamId, dto.AwayTeam));

            if (hStat == null && dto.Statistics.Count > 0) hStat = dto.Statistics[0];
            if (aStat == null && dto.Statistics.Count > 1) aStat = dto.Statistics[1];
        }

        int hPoss = hStat?.PossessionPercent ?? hStat?.Possession ?? 50;
        int aPoss = aStat?.PossessionPercent ?? aStat?.Possession ?? (100 - hPoss);
        if (hPoss + aPoss == 0) { hPoss = 50; aPoss = 50; }

        int hShots = hStat?.TotalShots ?? 0;
        int aShots = aStat?.TotalShots ?? 0;
        int hSog = hStat?.ShotsOnGoal ?? 0;
        int aSog = aStat?.ShotsOnGoal ?? 0;
        double? hXg = hStat?.EffectiveXg;
        double? aXg = aStat?.EffectiveXg;
        int hFouls = hStat?.Fouls ?? 0;
        int aFouls = aStat?.Fouls ?? 0;
        int hCards = (hStat?.YellowCards ?? 0) + (hStat?.RedCards ?? 0);
        int aCards = (aStat?.YellowCards ?? 0) + (aStat?.RedCards ?? 0);

        return (hPoss, aPoss, hShots, aShots, hSog, aSog, hXg, aXg, hFouls, aFouls, hCards, aCards);
    }

    private static bool IsTeamMatch(string? id, TeamSummaryDto? team)
    {
        if (string.IsNullOrWhiteSpace(id) || team == null) return false;
        string cleanId = id.Trim();
        return string.Equals(cleanId, team.Id, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrEmpty(team.LegacyId) && string.Equals(cleanId, team.LegacyId, StringComparison.OrdinalIgnoreCase)) ||
               string.Equals(cleanId.Replace("lt_", string.Empty), team.Id.Replace("lt_", string.Empty), StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Regex HtmlTagRegex = new("<.*?>", RegexOptions.Compiled);

    public static string CleanInsightText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        // Strip HTML tags using pre-compiled regex
        string stripped = HtmlTagRegex.Replace(raw, string.Empty);
        stripped = System.Net.WebUtility.HtmlDecode(stripped).Trim();

        // Level 1 White-label check: discard any text referencing scrubbed brand
        // XOR obfuscated runtime decode to prevent Roslyn constant folding into binary metadata
        string scrubbedBrand = GetScrubbedBrandName();
        if (stripped.Contains(scrubbedBrand, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return stripped;
    }

    private static string GetScrubbedBrandName()
    {
        // 0x66, 0x6f, 0x74, 0x6d, 0x6f, 0x62 XOR 0x5A
        byte[] enc = [0x3C, 0x35, 0x2E, 0x37, 0x35, 0x38];
        byte[] dec = new byte[enc.Length];
        for (int i = 0; i < enc.Length; i++) dec[i] = (byte)(enc[i] ^ 0x5A);
        return System.Text.Encoding.ASCII.GetString(dec);
    }

    #endregion
}
