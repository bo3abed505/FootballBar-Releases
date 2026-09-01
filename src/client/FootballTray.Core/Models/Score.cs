namespace FootballTray.Core.Models;

/// <summary>
/// Immutable match score with breakdown for normal time, half time, extra time, and penalties.
/// </summary>
public sealed record Score(
    int Home = 0,
    int Away = 0,
    int? HomeHalfTime = null,
    int? AwayHalfTime = null,
    int? HomeExtraTime = null,
    int? AwayExtraTime = null,
    int? HomePenalties = null,
    int? AwayPenalties = null)
{
    public static Score Empty => new(0, 0);

    public Score WithGoal(bool isHome) =>
        isHome ? this with { Home = Home + 1 } : this with { Away = Away + 1 };

    public Score WithHalfTimeScore(int home, int away) =>
        this with { HomeHalfTime = home, AwayHalfTime = away };

    public Score WithExtraTimeScore(int home, int away) =>
        this with { HomeExtraTime = home, AwayExtraTime = away };

    public Score WithPenaltiesScore(int home, int away) =>
        this with { HomePenalties = home, AwayPenalties = away };

    public string DisplayScore => $"{Home} - {Away}";
}
