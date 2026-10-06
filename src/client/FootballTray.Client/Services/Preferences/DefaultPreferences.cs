using System;
using System.Collections.Generic;
using FootballTray.Client.Services.Notifications;

namespace FootballTray.Client.Services.Preferences;

/// <summary>
/// Factory providing deterministic default preferences for initial launch or corruption recovery.
/// All default followed entities use the canonical provider IDs matching API-Football and TARGET_COMPETITION_IDS.
/// </summary>
public static class DefaultPreferences
{
    public static readonly FollowedTeam Barcelona = new(
        id: CanonicalEntityIds.Barcelona,
        name: "FC Barcelona",
        shortName: "BAR",
        country: "Spain",
        logoUrl: "https://media.api-sports.io/football/teams/529.png");

    public static readonly FollowedTeam AlAhly = new(
        id: CanonicalEntityIds.AlAhly,
        name: "Al Ahly SC",
        shortName: "AHL",
        country: "Egypt",
        logoUrl: "https://media.api-sports.io/football/teams/1029.png");

    public static readonly FollowedCompetition PremierLeague = new(
        id: CanonicalEntityIds.PremierLeague,
        name: "Premier League",
        code: "PL",
        country: "England",
        logoUrl: "https://media.api-sports.io/football/leagues/39.png");

    public static readonly FollowedCompetition LaLiga = new(
        id: CanonicalEntityIds.LaLiga,
        name: "La Liga",
        code: "ESP_L1",
        country: "Spain",
        logoUrl: "https://media.api-sports.io/football/leagues/140.png");

    public static readonly FollowedCompetition EgyptianPremierLeague = new(
        id: CanonicalEntityIds.EgyptianPremierLeague,
        name: "Egyptian Premier League",
        code: "EGY_PL",
        country: "Egypt",
        logoUrl: "https://media.api-sports.io/football/leagues/233.png");

    public static readonly FollowedCompetition ChampionsLeague = new(
        id: CanonicalEntityIds.ChampionsLeague,
        name: "UEFA Champions League",
        code: "CL",
        country: "World",
        logoUrl: "https://media.api-sports.io/football/leagues/2.png");

    public static readonly FollowedCompetition AfricaCupOfNations = new(
        id: CanonicalEntityIds.AfricaCupOfNations,
        name: "CAF Africa Cup of Nations",
        code: "AFCON",
        country: "Africa",
        logoUrl: "https://media.api-sports.io/football/leagues/6.png");

    public static readonly FollowedCompetition WorldCup = new(
        id: CanonicalEntityIds.WorldCup,
        name: "FIFA World Cup",
        code: "WC",
        country: "World",
        logoUrl: "https://media.api-sports.io/football/leagues/1.png");

    public static UserPreferences CreateDefault()
    {
        return new UserPreferences
        {
            PreferencesVersion = UserPreferences.CurrentSchemaVersion,
            FollowedTeams = new List<FollowedTeam>(),
            FollowedCompetitions = new List<FollowedCompetition>(),
            Notifications = new NotificationPreferences
            {
                GlobalEnabled = true,
                EnabledTypes = Enum.GetValues<NotificationType>().ToList()
            },
            SoundMode = NotificationSoundMode.Custom,
            AudioVolume = 0.2,
            IsTickerVisible = true,
            TickerOffsetX = 0,
            TargetScreenDeviceName = null,
            IsRunAtStartupEnabled = false,
            IsLoggingEnabled = true,
            IsDebugLoggingEnabled = false,
            IsKickoffReminderEnabled = true,
            KickoffReminderOffsetMinutes = 15
        };
    }
}
