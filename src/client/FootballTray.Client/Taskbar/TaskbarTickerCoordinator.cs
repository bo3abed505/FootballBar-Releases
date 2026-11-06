using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using FootballTray.Core.Dto;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Preferences;
using FootballTray.Client.Views;

namespace FootballTray.Client.Taskbar;

/// <summary>
/// Structured display state emitted to the taskbar ticker UI.
/// Atomic pairing of current match ID (for click routing) and formatted text.
/// </summary>
public sealed record TickerDisplayState(
    string? FixtureId,
    string FormattedText,
    string? HomeTeamLogoUrl = null,
    string? AwayTeamLogoUrl = null,
    TrayMatchDto? Match = null,
    bool IsPinned = false);

/// <summary>
/// Abstraction over ticker interval timer for deterministic testing without UI thread or sleep.
/// </summary>
internal interface ITickerTimer : IDisposable
{
    TimeSpan Interval { get; set; }
    bool IsRunning { get; }
    event EventHandler? Tick;
    void Start();
    void Stop();
}

/// <summary>
/// Production timer wrapper around WinUI DispatcherQueueTimer.
/// </summary>
internal sealed class DispatcherQueueTickerTimer : ITickerTimer
{
    private readonly DispatcherQueueTimer _timer;

    public DispatcherQueueTickerTimer(DispatcherQueue dispatcherQueue)
    {
        if (dispatcherQueue == null) throw new ArgumentNullException(nameof(dispatcherQueue));
        _timer = dispatcherQueue.CreateTimer();
        _timer.IsRepeating = true;
        _timer.Tick += (s, e) => Tick?.Invoke(this, EventArgs.Empty);
    }

    public TimeSpan Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    public bool IsRunning => _timer.IsRunning;

    public event EventHandler? Tick;

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    public void Dispose()
    {
        _timer.Stop();
    }
}

/// <summary>
/// Pure deterministic state engine managing candidate matches and cycling position.
/// Free of any UI, timer, or threading dependencies.
/// </summary>
public sealed class TickerRotationState
{
    private List<TrayMatchDto> _candidates = new();

    public IReadOnlyList<TrayMatchDto> Candidates => _candidates;
    public TrayMatchDto? CurrentCandidate { get; private set; }
    public int CurrentIndex { get; private set; } = -1;
    public bool CanCycle => _candidates.Count >= 2;

    public void ApplyCandidates(IReadOnlyList<TrayMatchDto>? candidates)
    {
        _candidates = candidates != null ? new List<TrayMatchDto>(candidates) : new List<TrayMatchDto>();

        if (_candidates.Count == 0)
        {
            CurrentCandidate = null;
            CurrentIndex = -1;
            return;
        }

        if (CurrentCandidate != null)
        {
            var matchId = CurrentCandidate.Id;
            int foundIndex = _candidates.FindIndex(m => string.Equals(m.Id, matchId, StringComparison.Ordinal));
            if (foundIndex >= 0)
            {
                // CASE A / CASE F: Current fixture still exists: preserve identity, update to fresh DTO, update index
                CurrentCandidate = _candidates[foundIndex];
                CurrentIndex = foundIndex;
                return;
            }
        }

        // CASE B / CASE E (when old current absent): Current fixture removed or wasn't set: select candidate[0]
        CurrentCandidate = _candidates[0];
        CurrentIndex = 0;
    }

    public bool SetCurrentMatch(string matchId)
    {
        if (string.IsNullOrEmpty(matchId) || _candidates.Count == 0)
        {
            return false;
        }

        int foundIndex = _candidates.FindIndex(m => string.Equals(m.Id, matchId, StringComparison.Ordinal));
        if (foundIndex >= 0)
        {
            CurrentIndex = foundIndex;
            CurrentCandidate = _candidates[foundIndex];
            return true;
        }

        return false;
    }

    public bool PromoteToTop(string matchId)
    {
        if (string.IsNullOrEmpty(matchId) || _candidates.Count == 0)
        {
            return false;
        }

        int foundIndex = _candidates.FindIndex(m => string.Equals(m.Id, matchId, StringComparison.Ordinal));
        if (foundIndex >= 0)
        {
            var match = _candidates[foundIndex];
            if (foundIndex > 0)
            {
                _candidates.RemoveAt(foundIndex);
                _candidates.Insert(0, match);
            }
            CurrentIndex = 0;
            CurrentCandidate = match;
            return true;
        }

        return false;
    }

    public TrayMatchDto? Advance()
    {
        if (_candidates.Count == 0)
        {
            CurrentCandidate = null;
            CurrentIndex = -1;
            return null;
        }

        if (_candidates.Count == 1)
        {
            CurrentIndex = 0;
            CurrentCandidate = _candidates[0];
            return CurrentCandidate;
        }

        CurrentIndex = (CurrentIndex + 1) % _candidates.Count;
        CurrentCandidate = _candidates[CurrentIndex];
        return CurrentCandidate;
    }

    public TrayMatchDto? Step(int direction)
    {
        if (_candidates.Count == 0)
        {
            CurrentCandidate = null;
            CurrentIndex = -1;
            return null;
        }

        if (_candidates.Count == 1)
        {
            CurrentIndex = 0;
            CurrentCandidate = _candidates[0];
            return CurrentCandidate;
        }

        if (direction > 0)
        {
            CurrentIndex = (CurrentIndex + 1) % _candidates.Count;
        }
        else if (direction < 0)
        {
            CurrentIndex = (CurrentIndex - 1 + _candidates.Count) % _candidates.Count;
        }

        CurrentCandidate = _candidates[CurrentIndex];
        return CurrentCandidate;
    }

    public TickerDisplayState GetDisplayState(TickerConnectionState connectionState, bool isPinned = false, UserPreferences? preferences = null)
    {
        var text = TrayTickerFormatter.FormatMatchTicker(CurrentCandidate, connectionState, preferences);
        return new TickerDisplayState(
            CurrentCandidate?.Id,
            text,
            CurrentCandidate?.HomeTeam?.LogoUrl,
            CurrentCandidate?.AwayTeam?.LogoUrl,
            CurrentCandidate,
            isPinned);
    }
}

/// <summary>
/// Production coordinator managing ticker cycling, snapshot refreshes, and atomic display state emissions.
/// </summary>
public sealed class TaskbarTickerCoordinator : IDisposable
{
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly ITickerTimer _timer;
    private readonly Action<string?, string> _displayCallback;
    private readonly Action<string?, string, string?, string?>? _extendedDisplayCallback;
    private readonly Action<string?, string, string?, string?, TrayMatchDto?, bool>? _fullDisplayCallback;
    private readonly TickerRotationState _state = new();
    private TickerConnectionState _currentConnectionState = TickerConnectionState.OnlineFresh;
    private UserPreferences? _currentPreferences;
    private bool _isCarouselEnabled = true;
    private string? _pinnedMatchId;
    private TrayMatchDto? _pinnedMatch;
    private bool _isPinnedFromSnapshot;
    private TrayDataDto? _latestTrayData;
    private bool _isDisposed;
    private MatchCenterWindow? _matchCenterWindow;

    public static readonly TimeSpan RotationInterval = TimeSpan.FromSeconds(10);

    public ObservableCollection<TrayMatchDto> TickerMatches { get; } = new();
    public TickerRotationState State => _state;
    public string? CurrentMatchId => _state.CurrentCandidate?.Id;
    public bool IsCycling => _timer.IsRunning;
    public string? PinnedMatchId => _pinnedMatchId;
    public bool IsPinned => !string.IsNullOrEmpty(_pinnedMatchId);
    private readonly Dictionary<string, (int Home, int Away)> _previousScores = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int Home, int Away, int HomeRed, int AwayRed)> _previousMatchState = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _preemptionUntil = DateTimeOffset.MinValue;
    private string? _preemptionSummary;
    private TrayMatchDto? _preemptedMatch;

    public bool IsPreempted => DateTimeOffset.UtcNow < _preemptionUntil;
    public string? PreemptionSummary => IsPreempted ? _preemptionSummary : null;
    public TrayMatchDto? PreemptedMatch => IsPreempted ? _preemptedMatch : null;
    private readonly Dictionary<string, TrayMatchDto> _customQueueMatchSnapshots = new(StringComparer.OrdinalIgnoreCase);

    public TrayMatchDto? GetQueuedMatchSnapshot(string matchId)
    {
        if (string.IsNullOrWhiteSpace(matchId)) return null;
        if (_customQueueMatchSnapshots.TryGetValue(matchId, out var direct)) return direct;
        string norm = CanonicalEntityIds.StripLegacyProviderPrefix(matchId);
        if (_customQueueMatchSnapshots.TryGetValue(norm, out var byNorm)) return byNorm;
        return _customQueueMatchSnapshots.Values.FirstOrDefault(m =>
            string.Equals(m.Id, matchId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.LegacyId, matchId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(CanonicalEntityIds.StripLegacyProviderPrefix(m.Id), norm, StringComparison.OrdinalIgnoreCase));
    }

    public TrayMatchDto? GetActiveMatch() => _state.CurrentCandidate;

    public TrayMatchDto? Advance()
    {
        var next = _state.Advance();
        EmitCurrentDisplay();
        return next;
    }

    public void CacheQueueMatchSnapshot(TrayMatchDto match)
    {
        if (match != null && !string.IsNullOrWhiteSpace(match.Id))
        {
            _customQueueMatchSnapshots[match.Id] = match;
            if (!string.IsNullOrWhiteSpace(match.LegacyId))
            {
                _customQueueMatchSnapshots[match.LegacyId] = match;
            }
            string norm = CanonicalEntityIds.StripLegacyProviderPrefix(match.Id);
            _customQueueMatchSnapshots[norm] = match;
        }
    }

    public void ApplyPreferences(UserPreferences? preferences)
    {
        if (preferences == null) return;
        _currentPreferences = preferences;
        _isCarouselEnabled = preferences.EnableTickerCarousel;
        if (preferences.EnableTickerCarousel || string.Equals(preferences.TickerRotationScope, "CustomQueue", StringComparison.OrdinalIgnoreCase))
        {
            _pinnedMatchId = null;
            _pinnedMatch = null;
            _isPinnedFromSnapshot = false;
        }
        else if (!string.IsNullOrEmpty(preferences.ManualPinnedMatchId))
        {
            PinMatch(preferences.ManualPinnedMatchId);
        }
        ApplySnapshot(_latestTrayData, _currentConnectionState, preferences);
    }
    public string? LastGoalJumpMatchId { get; private set; }

    public TaskbarTickerCoordinator(
        DispatcherQueue dispatcherQueue,
        Action<string?, string> displayCallback,
        Action<string?, string, string?, string?>? extendedDisplayCallback = null,
        Action<string?, string, string?, string?, TrayMatchDto?, bool>? fullDisplayCallback = null)
        : this(new DispatcherQueueTickerTimer(dispatcherQueue), displayCallback, extendedDisplayCallback, fullDisplayCallback)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    internal TaskbarTickerCoordinator(
        ITickerTimer timer,
        Action<string?, string> displayCallback,
        Action<string?, string, string?, string?>? extendedDisplayCallback = null,
        Action<string?, string, string?, string?, TrayMatchDto?, bool>? fullDisplayCallback = null)
    {
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        _displayCallback = displayCallback ?? throw new ArgumentNullException(nameof(displayCallback));
        _extendedDisplayCallback = extendedDisplayCallback;
        _fullDisplayCallback = fullDisplayCallback;

        _timer.Interval = RotationInterval;
        _timer.Tick += OnTimerTick;
    }

    public void SubscribeToTicker(TaskbarTicker? ticker)
    {
        if (ticker == null) return;
        ticker.MatchCenterRequested += (sender, matchId) => OpenMatchCenter(matchId);
    }

    public void OpenMatchCenter(TrayMatchDto? match) => OpenMatchCenter(match?.Id ?? string.Empty, match);

    public async void OpenMatchCenter(string matchId, TrayMatchDto? fallbackMatch = null)
    {
        if (string.IsNullOrWhiteSpace(matchId) && fallbackMatch == null) return;
        if (_isDisposed) return;

        string targetId = !string.IsNullOrWhiteSpace(matchId) ? matchId : fallbackMatch?.Id ?? string.Empty;

        // Fallback match from parameter or local collections
        var candidateMatch = fallbackMatch 
            ?? _state.Candidates.FirstOrDefault(m => string.Equals(m.Id, targetId, StringComparison.OrdinalIgnoreCase) || string.Equals(m.LegacyId, targetId, StringComparison.OrdinalIgnoreCase))
            ?? _latestTrayData?.Matches.FirstOrDefault(m => string.Equals(m.Id, targetId, StringComparison.OrdinalIgnoreCase) || string.Equals(m.LegacyId, targetId, StringComparison.OrdinalIgnoreCase))
            ?? App.CurrentMainViewModel?.GetMatchDtoById(targetId);

        if (_matchCenterWindow == null)
        {
            _matchCenterWindow = new MatchCenterWindow();
            _matchCenterWindow.Closed += (s, args) => _matchCenterWindow = null;
        }

        if (candidateMatch != null)
        {
            var fallbackDetail = new MatchDetailDto
            {
                Id = candidateMatch.Id,
                LegacyId = candidateMatch.LegacyId,
                Competition = candidateMatch.Competition ?? new CompetitionSummaryDto(),
                HomeTeam = candidateMatch.HomeTeam ?? new TeamDto(),
                AwayTeam = candidateMatch.AwayTeam ?? new TeamDto(),
                Score = candidateMatch.Score ?? new ScoreDto(),
                State = candidateMatch.State,
                ElapsedMinute = candidateMatch.ElapsedMinute,
                ExtraMinute = candidateMatch.ExtraMinute,
                KickoffTime = candidateMatch.KickoffTime,
                Trust = candidateMatch.Trust
            };
            _matchCenterWindow.Populate(fallbackDetail);
            _matchCenterWindow.Activate();
        }

        try
        {
            using var apiClient = new FootballApiClient();
            var response = await apiClient.GetMatchDetailsAsync(targetId);

            if (response.IsSuccess && response.Envelope?.Data != null)
            {
                var detail = response.Envelope.Data;
                if (_matchCenterWindow == null)
                {
                    _matchCenterWindow = new MatchCenterWindow();
                    _matchCenterWindow.Closed += (s, args) => _matchCenterWindow = null;
                }

                _matchCenterWindow.Populate(detail);
                _matchCenterWindow.Activate();
                App.CurrentMainViewModel?.UpdateMatchFromDetails(detail);
                UpdateMatchFromDetails(detail);
            }
            else if (candidateMatch == null)
            {
                AppDiagnosticsLogger.Log($"[ERROR][MatchCenter][OpenFailed] Failed to open MatchCenterWindow from ticker: API returned unsuccessful response for match {targetId} (Error: {response.ErrorMessage ?? "null payload"})");
            }
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Log($"[ERROR][MatchCenter][OpenFailed] Failed to open MatchCenterWindow from ticker: {ex.Message}\n{ex.StackTrace}");
            System.Diagnostics.Debug.WriteLine($"[TICKER] Failed to open MatchCenterWindow: {ex.Message}");
        }
    }

    public bool PinMatch(string? matchId = null, TrayMatchDto? matchSnapshot = null)
    {
        if (_isDisposed) return false;

        var targetId = matchId ?? _state.CurrentCandidate?.Id;
        if (string.IsNullOrEmpty(targetId)) return false;

        // Check active candidates first, then fall back to latest tray data, matchSnapshot, or existing _pinnedMatch
        TrayMatchDto? match = _state.Candidates.FirstOrDefault(m => string.Equals(m.Id, targetId, StringComparison.Ordinal))
            ?? _latestTrayData?.Matches?.FirstOrDefault(m => string.Equals(m.Id, targetId, StringComparison.Ordinal))
            ?? matchSnapshot
            ?? _pinnedMatch;

        if (match != null)
        {
            _pinnedMatchId = targetId;
            _pinnedMatch = match;
            _isPinnedFromSnapshot = (matchSnapshot != null);
            if (!_state.Candidates.Any(m => string.Equals(m.Id, targetId, StringComparison.Ordinal)))
            {
                var combined = new List<TrayMatchDto>(_state.Candidates);
                combined.Insert(0, match);
                _state.ApplyCandidates(combined);
            }
            _state.SetCurrentMatch(targetId);
            EmitCurrentDisplay();
            return true;
        }

        return false;
    }

    public void UnpinMatch()
    {
        if (_isDisposed) return;
        var unpinnedId = _pinnedMatchId;
        _pinnedMatchId = null;
        _pinnedMatch = null;

        if (!string.IsNullOrEmpty(unpinnedId))
        {
            // Recompute natural candidates without the pinned match override
            var naturalCandidates = _currentConnectionState == TickerConnectionState.NoCacheOffline
                ? (IReadOnlyList<TrayMatchDto>)Array.Empty<TrayMatchDto>()
                : TrayTickerFormatter.GetTickerCandidates(_latestTrayData, _currentPreferences);

            bool isNaturalCandidate = naturalCandidates.Any(m => string.Equals(m.Id, unpinnedId, StringComparison.Ordinal));
            if (!isNaturalCandidate)
            {
                // Unpinned match was only present because it was pinned: purge immediately from rotation
                var remaining = _state.Candidates.Where(m => !string.Equals(m.Id, unpinnedId, StringComparison.Ordinal)).ToList();
                _state.ApplyCandidates(remaining);
                SmartMergeTickerMatches(remaining);
            }
            else
            {
                // It is a natural candidate: sync and retain in rotation
                SmartMergeTickerMatches(_state.Candidates);
            }

            // Notify backend daemon asynchronously to clear pinnedMatchIds immediately (ending 90s burst cycle)
            _ = Task.Run(async () =>
            {
                try
                {
                    using var client = new FootballApiClient();
                    await client.UpdatePinnedMatchAsync(null);
                }
                catch
                {
                    // Ignore background preference notification errors
                }
            });
        }

        EmitCurrentDisplay();
    }

    public bool TogglePin(string? matchId = null)
    {
        if (_isDisposed) return false;

        var targetId = matchId ?? _state.CurrentCandidate?.Id;
        if (IsPinned && (string.IsNullOrEmpty(targetId) || string.Equals(_pinnedMatchId, targetId, StringComparison.Ordinal)))
        {
            UnpinMatch();
            return false;
        }

        return PinMatch(targetId);
    }

    /// <summary>
    /// Immediately updates clock (elapsed minute, extra minute, state) and score from a detailed match payload
    /// into the ticker candidate and pinned match models, and re-renders the taskbar HWND.
    /// </summary>
    public void UpdateMatchFromDetails(MatchDetailDto? detail)
    {
        if (detail == null || string.IsNullOrWhiteSpace(detail.Id) || _isDisposed) return;

        bool isPinnedMatch = !string.IsNullOrEmpty(_pinnedMatchId) &&
            (string.Equals(_pinnedMatchId, detail.Id, StringComparison.OrdinalIgnoreCase) ||
             _pinnedMatchId.EndsWith($"_{detail.Id}", StringComparison.OrdinalIgnoreCase) ||
             detail.Id.EndsWith($"_{_pinnedMatchId}", StringComparison.OrdinalIgnoreCase));

        if (isPinnedMatch && _pinnedMatch != null)
        {
            _pinnedMatch.ElapsedMinute = detail.ElapsedMinute;
            _pinnedMatch.ExtraMinute = detail.ExtraMinute;
            _pinnedMatch.State = detail.State;
            if (detail.Score != null)
            {
                _pinnedMatch.Score ??= new ScoreDto();
                _pinnedMatch.Score.Home = detail.Score.Home;
                _pinnedMatch.Score.Away = detail.Score.Away;
                if (detail.Score.HalfTime != null)
                {
                    _pinnedMatch.Score.HalfTime ??= new ScorePeriodDto();
                    _pinnedMatch.Score.HalfTime.Home = detail.Score.HalfTime.Home;
                    _pinnedMatch.Score.HalfTime.Away = detail.Score.HalfTime.Away;
                }
            }
        }

        bool updatedInCandidates = false;
        foreach (var candidate in _state.Candidates)
        {
            if (string.Equals(candidate.Id, detail.Id, StringComparison.OrdinalIgnoreCase) ||
                candidate.Id.EndsWith($"_{detail.Id}", StringComparison.OrdinalIgnoreCase) ||
                detail.Id.EndsWith($"_{candidate.Id}", StringComparison.OrdinalIgnoreCase))
            {
                candidate.ElapsedMinute = detail.ElapsedMinute;
                candidate.ExtraMinute = detail.ExtraMinute;
                candidate.State = detail.State;
                if (detail.Score != null)
                {
                    candidate.Score ??= new ScoreDto();
                    candidate.Score.Home = detail.Score.Home;
                    candidate.Score.Away = detail.Score.Away;
                }
                updatedInCandidates = true;
            }
        }

        foreach (var match in TickerMatches)
        {
            if (string.Equals(match.Id, detail.Id, StringComparison.OrdinalIgnoreCase) ||
                match.Id.EndsWith($"_{detail.Id}", StringComparison.OrdinalIgnoreCase) ||
                detail.Id.EndsWith($"_{match.Id}", StringComparison.OrdinalIgnoreCase))
            {
                match.ElapsedMinute = detail.ElapsedMinute;
                match.ExtraMinute = detail.ExtraMinute;
                match.State = detail.State;
                if (detail.Score != null)
                {
                    match.Score ??= new ScoreDto();
                    match.Score.Home = detail.Score.Home;
                    match.Score.Away = detail.Score.Away;
                }
            }
        }

        if (isPinnedMatch || updatedInCandidates || string.Equals(_state.CurrentCandidate?.Id, detail.Id, StringComparison.OrdinalIgnoreCase))
        {
            EmitCurrentDisplay();
        }
    }

    public void ApplySnapshot(TrayUpdateEventArgs updateArgs, UserPreferences? preferences = null)
    {
        if (updateArgs == null)
        {
            ApplySnapshot(null, TickerConnectionState.NoCacheOffline, preferences);
            return;
        }

        ApplySnapshot(updateArgs.TrayData, updateArgs.ConnectionState, preferences);
    }

    public void ApplySnapshot(TrayDataDto? trayData, DataFreshnessState freshnessState, UserPreferences? preferences = null)
    {
        ApplySnapshot(trayData, TrayTickerFormatter.MapConnectionState(freshnessState), preferences);
    }

    private IReadOnlyList<TrayMatchDto> ComputeCandidates(TrayDataDto? trayData, UserPreferences? preferences, TickerConnectionState connectionState)
    {
        if (connectionState == TickerConnectionState.NoCacheOffline)
        {
            return Array.Empty<TrayMatchDto>();
        }

        string scope = preferences?.TickerRotationScope ?? "FollowedLive";

        if (string.Equals(scope, "CustomQueue", StringComparison.OrdinalIgnoreCase))
        {
            var queueIds = preferences?.CustomTickerQueueMatchIds ?? _currentPreferences?.CustomTickerQueueMatchIds ?? new List<string>();
            if (queueIds.Count == 0) return Array.Empty<TrayMatchDto>();

            var matches = new List<TrayMatchDto>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var qId in queueIds)
            {
                if (string.IsNullOrWhiteSpace(qId)) continue;
                string norm = CanonicalEntityIds.StripLegacyProviderPrefix(qId);

                TrayMatchDto? found = trayData?.Matches?.FirstOrDefault(m =>
                    string.Equals(m.Id, qId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(m.LegacyId, qId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(CanonicalEntityIds.StripLegacyProviderPrefix(m.Id), norm, StringComparison.OrdinalIgnoreCase));

                found ??= GetQueuedMatchSnapshot(qId);

                if (found != null)
                {
                    if (seen.Add(found.Id))
                    {
                        _customQueueMatchSnapshots[found.Id] = found;
                        _customQueueMatchSnapshots[qId] = found;
                        matches.Add(found);
                    }
                }
                else if (seen.Add(qId))
                {
                    var fallback = new TrayMatchDto
                    {
                        Id = qId,
                        State = "PREMATCH",
                        HomeTeam = new TeamSummaryDto { Name = $"Match {qId}" },
                        AwayTeam = new TeamSummaryDto { Name = "" }
                    };
                    matches.Add(fallback);
                }
            }
            return matches;
        }

        if (trayData?.Matches == null || trayData.Matches.Count == 0)
        {
            return Array.Empty<TrayMatchDto>();
        }

        bool carouselEnabled = preferences != null ? preferences.EnableTickerCarousel : _isCarouselEnabled;
        if (!carouselEnabled)
        {
            return !string.IsNullOrEmpty(_pinnedMatchId)
                ? TrayTickerFormatter.GetTickerCandidates(trayData, preferences ?? _currentPreferences, _pinnedMatchId)
                : Array.Empty<TrayMatchDto>();
        }

        if (preferences == null)
        {
            return TrayTickerFormatter.GetTickerCandidates(trayData, null);
        }

        if (string.Equals(scope, "AllLive", StringComparison.OrdinalIgnoreCase))
        {
            var allLive = trayData.Matches.Where(FixtureRelevanceEngine.IsLive).ToList();
            if (allLive.Count > 0)
            {
                return FixtureRelevanceEngine.PrioritizeMatches(allLive, preferences);
            }
            return Array.Empty<TrayMatchDto>();
        }

        var teamIds = preferences.GetFollowedTeamIdSet();
        var compIds = preferences.GetFollowedCompetitionIdSet();
        bool hasFollowed = teamIds.Count > 0 || compIds.Count > 0;

        if (!hasFollowed)
        {
            return Array.Empty<TrayMatchDto>();
        }

        var followedMatches = trayData.Matches
            .Where(m => (m.KickoffTime.ToLocalTime().Date == DateTime.Today || FixtureRelevanceEngine.IsLive(m)) &&
                        FixtureRelevanceEngine.EvaluateRelevance(m, teamIds, compIds).IsRelevant)
            .ToList();

        if (followedMatches.Count == 0)
        {
            return Array.Empty<TrayMatchDto>();
        }

        if (string.Equals(scope, "AllFollowed", StringComparison.OrdinalIgnoreCase))
        {
            return FixtureRelevanceEngine.PrioritizeMatches(followedMatches, preferences);
        }

        // Default: "FollowedLive"
        var live = FixtureRelevanceEngine.PrioritizeMatches(followedMatches.Where(FixtureRelevanceEngine.IsLive), preferences);
        if (live.Count > 0)
        {
            return live;
        }

        return Array.Empty<TrayMatchDto>();
    }

    public void ApplySnapshot(TrayDataDto? trayData, TickerConnectionState connectionState, UserPreferences? preferences = null)
    {
        if (_isDisposed) return;

        _currentConnectionState = connectionState;
        _currentPreferences = preferences ?? _currentPreferences;
        if (preferences != null)
        {
            _isCarouselEnabled = preferences.EnableTickerCarousel;
            if (string.Equals(preferences.TickerRotationScope, "CustomQueue", StringComparison.OrdinalIgnoreCase))
            {
                _pinnedMatchId = null;
                _pinnedMatch = null;
                _isPinnedFromSnapshot = false;
            }
        }
        _latestTrayData = trayData;

        // Detect if any followed live match experienced a score change since previous poll
        string? goalJumpMatchId = null;
        if (trayData?.Matches != null && preferences != null)
        {
            var teamIds = preferences.GetFollowedTeamIdSet();
            var compIds = preferences.GetFollowedCompetitionIdSet();

            foreach (var match in trayData.Matches)
            {
                if (match == null || string.IsNullOrWhiteSpace(match.Id)) continue;
                int currentHome = match.Score?.Home ?? 0;
                int currentAway = match.Score?.Away ?? 0;

                int currentHomeRed = match.HomeTeam?.RedCards ?? 0;
                int currentAwayRed = match.AwayTeam?.RedCards ?? 0;

                if (_previousMatchState.TryGetValue(match.Id, out var prevState))
                {
                    bool isLive = FixtureRelevanceEngine.IsLive(match);
                    bool isFollowed = FixtureRelevanceEngine.EvaluateRelevance(match, teamIds, compIds).IsRelevant;
                    bool isPinned = !string.IsNullOrWhiteSpace(_pinnedMatchId) && (string.Equals(match.Id, _pinnedMatchId, StringComparison.OrdinalIgnoreCase) || match.Id.EndsWith($"_{_pinnedMatchId}") || _pinnedMatchId.EndsWith($"_{match.Id}"));
                    bool isInCustomQueue = preferences?.CustomTickerQueueMatchIds?.Contains(match.Id) == true;
                    string scope = preferences?.TickerRotationScope ?? _currentPreferences?.TickerRotationScope ?? "FollowedLive";
                    bool isAllLiveScope = string.Equals(scope, "AllLive", StringComparison.OrdinalIgnoreCase);

                    bool isEligibleForPreemption = isFollowed || isPinned || isInCustomQueue || isAllLiveScope;

                    if (isLive && isEligibleForPreemption)
                    {
                        bool isGoal = currentHome > prevState.Home || currentAway > prevState.Away;
                        bool isRedCard = currentHomeRed > prevState.HomeRed || currentAwayRed > prevState.AwayRed;

                        if (isGoal || isRedCard)
                        {
                            bool eventJumpEnabled = preferences?.EnableTickerEventPreemption ?? _currentPreferences?.EnableTickerEventPreemption ?? true;
                            if (eventJumpEnabled)
                            {
                                goalJumpMatchId ??= match.Id;
                            }
                        }
                    }
                }

                _previousScores[match.Id] = (currentHome, currentAway);
                _previousMatchState[match.Id] = (currentHome, currentAway, currentHomeRed, currentAwayRed);
            }
        }

        IReadOnlyList<TrayMatchDto> candidates = ComputeCandidates(trayData, preferences ?? _currentPreferences, connectionState);

        bool isCustomQueue = string.Equals(preferences?.TickerRotationScope ?? _currentPreferences?.TickerRotationScope, "CustomQueue", StringComparison.OrdinalIgnoreCase);
        // If a match was explicitly pinned, guarantee it is present in candidates even if unfollowed or date changed
        if (!isCustomQueue && !string.IsNullOrEmpty(_pinnedMatchId))
        {
            var pinnedInTray = trayData?.Matches?.FirstOrDefault(m => string.Equals(m.Id, _pinnedMatchId, StringComparison.Ordinal));
            if (pinnedInTray != null)
            {
                _pinnedMatch = pinnedInTray;
                if (!candidates.Any(m => string.Equals(m.Id, _pinnedMatchId, StringComparison.Ordinal)))
                {
                    var combined = new List<TrayMatchDto>(candidates);
                    combined.Insert(0, pinnedInTray);
                    candidates = combined;
                }
            }
            else if (_isPinnedFromSnapshot && _pinnedMatch != null)
            {
                // The pinned match was explicitly supplied as a snapshot from another date tab (e.g. Yesterday or Tomorrow). Keep it pinned!
                if (!candidates.Any(m => string.Equals(m.Id, _pinnedMatchId, StringComparison.Ordinal)))
                {
                    var combined = new List<TrayMatchDto>(candidates);
                    combined.Insert(0, _pinnedMatch);
                    candidates = combined;
                }
            }
            else
            {
                // Pinned match removed or concluded from snapshot: clear pinned state gracefully
                _pinnedMatchId = null;
                _pinnedMatch = null;
                _isPinnedFromSnapshot = false;
            }
        }

        _state.ApplyCandidates(candidates);
        SmartMergeTickerMatches(candidates);

        // Carousel Goal-Triggered Jump:
        // If any followed live match experienced a score change, instantly interrupt rotation timer,
        // jump active carousel index to that match, promote it to the top of the queue for its full display cycle,
        // and resume normal rotation thereafter.
        if (!string.IsNullOrEmpty(goalJumpMatchId))
        {
            LastGoalJumpMatchId = goalJumpMatchId;
            if (_state.PromoteToTop(goalJumpMatchId))
            {
                if (_timer.IsRunning)
                {
                    _timer.Stop();
                    _timer.Start();
                }
                else if (_state.CanCycle)
                {
                    _timer.Start();
                }
            }
            else
            {
                var goalMatch = trayData?.Matches?.FirstOrDefault(m => string.Equals(m.Id, goalJumpMatchId, StringComparison.Ordinal));
                if (goalMatch != null)
                {
                    var combined = new List<TrayMatchDto>(_state.Candidates);
                    combined.Insert(0, goalMatch);
                    _state.ApplyCandidates(combined);
                    _state.SetCurrentMatch(goalJumpMatchId);
                    if (_timer.IsRunning)
                    {
                        _timer.Stop();
                        _timer.Start();
                    }
                    else if (_state.CanCycle)
                    {
                        _timer.Start();
                    }
                }
            }
        }

        // If a match was pinned, retain it if still in candidates
        if (!isCustomQueue && !string.IsNullOrEmpty(_pinnedMatchId))
        {
            bool matchStillPresent = _state.Candidates.Any(m => string.Equals(m.Id, _pinnedMatchId, StringComparison.Ordinal));
            if (matchStillPresent)
            {
                _state.SetCurrentMatch(_pinnedMatchId);
            }
        }

        // Timer lifecycle management
        if (_state.CanCycle)
        {
            if (!_timer.IsRunning)
            {
                _timer.Start();
            }
        }
        else
        {
            if (_timer.IsRunning)
            {
                _timer.Stop();
            }
        }

        EmitCurrentDisplay();
    }

    public void PreemptForMajorEvent(TrayMatchDto match, string eventSummary, TimeSpan? duration = null)
    {
        if (match == null || string.IsNullOrWhiteSpace(match.Id) || _isDisposed) return;

        _preemptionUntil = DateTimeOffset.UtcNow.Add(duration ?? TimeSpan.FromSeconds(45));
        _preemptionSummary = eventSummary;
        _preemptedMatch = match;
        LastGoalJumpMatchId = match.Id;

        if (!_state.Candidates.Any(m => string.Equals(m.Id, match.Id, StringComparison.Ordinal)))
        {
            var combined = new List<TrayMatchDto>(_state.Candidates);
            combined.Insert(0, match);
            _state.ApplyCandidates(combined);
            SmartMergeTickerMatches(combined);
        }
        else
        {
            _state.PromoteToTop(match.Id);
        }

        _state.SetCurrentMatch(match.Id);

        if (_timer.IsRunning)
        {
            _timer.Stop();
            _timer.Start();
        }
        else if (_state.CanCycle)
        {
            _timer.Start();
        }

        EmitCurrentDisplay();
    }

    public bool StepByUserScroll(int direction)
    {
        if (_isDisposed) return false;

        // Cancel any active preemption lock when user manually scrolls
        _preemptionUntil = DateTimeOffset.MinValue;
        _preemptedMatch = null;
        _preemptionSummary = null;

        if (_state.Candidates.Count <= 1)
        {
            return false;
        }

        _state.Step(direction);

        // Reset timer interval so user gets full duration to read the scrolled match
        if (_timer.IsRunning)
        {
            _timer.Stop();
            _timer.Start();
        }

        EmitCurrentDisplay();
        return true;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_isDisposed) return;

        if (DateTimeOffset.UtcNow < _preemptionUntil)
        {
            // Major event preemption lock active (hold match on ticker)
            return;
        }

        _preemptedMatch = null;
        _preemptionSummary = null;

        if (!_state.CanCycle)
        {
            _timer.Stop();
            return;
        }

        bool isCustomQueueScope = string.Equals(_currentPreferences?.TickerRotationScope, "CustomQueue", StringComparison.OrdinalIgnoreCase);
        if (!isCustomQueueScope && !string.IsNullOrEmpty(_pinnedMatchId))
        {
            // Carousel rotation is paused while a match is pinned
            return;
        }

        _state.Advance();
        EmitCurrentDisplay();
    }

    private void EmitCurrentDisplay()
    {
        var displayState = _state.GetDisplayState(_currentConnectionState, IsPinned, _currentPreferences);
        if (_fullDisplayCallback != null)
        {
            _fullDisplayCallback(displayState.FixtureId, displayState.FormattedText, displayState.HomeTeamLogoUrl, displayState.AwayTeamLogoUrl, displayState.Match, displayState.IsPinned);
        }
        else if (_extendedDisplayCallback != null)
        {
            _extendedDisplayCallback(displayState.FixtureId, displayState.FormattedText, displayState.HomeTeamLogoUrl, displayState.AwayTeamLogoUrl);
        }
        else
        {
            _displayCallback(displayState.FixtureId, displayState.FormattedText);
        }
    }

    /// <summary>
    /// Performs in-place smart merge on the TickerMatches collection instead of calling .Clear().
    /// Prevents the ticker UI from visually emptying and flashing upon background poll updates.
    /// </summary>
    public void SetCarouselEnabled(bool enabled)
    {
        if (_isDisposed) return;
        if (_dispatcherQueue != null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => SetCarouselEnabled(enabled));
            return;
        }

        _isCarouselEnabled = enabled;
        if (enabled)
        {
            _pinnedMatchId = null;
            _pinnedMatch = null;
            _isPinnedFromSnapshot = false;
        }
        if (_currentPreferences != null)
        {
            _currentPreferences = _currentPreferences with 
            { 
                EnableTickerCarousel = enabled,
                ManualPinnedMatchId = enabled ? null : _currentPreferences.ManualPinnedMatchId
            };
        }

        if (!enabled)
        {
            if (_timer.IsRunning) _timer.Stop();
            if (!IsPinned)
            {
                _state.ApplyCandidates(Array.Empty<TrayMatchDto>());
                SmartMergeTickerMatches(Array.Empty<TrayMatchDto>());
            }
        }
        else
        {
            // Recompute candidates using current tray data or cached candidates
            var candidates = ComputeCandidates(_latestTrayData, _currentPreferences, _currentConnectionState);
            _state.ApplyCandidates(candidates);
            SmartMergeTickerMatches(candidates);
            if (_state.CanCycle && !_timer.IsRunning)
            {
                _timer.Start();
            }
        }

        EmitCurrentDisplay();
    }

    public void SmartMergeTickerMatches(IReadOnlyList<TrayMatchDto> incoming)
    {
        if (_dispatcherQueue != null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => SmartMergeTickerMatches(incoming));
            return;
        }

        var incomingIds = new HashSet<string>(incoming.Where(m => !string.IsNullOrEmpty(m?.Id)).Select(m => m!.Id), StringComparer.Ordinal);

        // 1. Remove matches no longer in candidate list
        for (int i = TickerMatches.Count - 1; i >= 0; i--)
        {
            if (!incomingIds.Contains(TickerMatches[i].Id))
            {
                TickerMatches.RemoveAt(i);
            }
        }

        // 2. Insert or update in-place
        for (int i = 0; i < incoming.Count; i++)
        {
            var item = incoming[i];
            int existingIndex = -1;
            for (int j = 0; j < TickerMatches.Count; j++)
            {
                if (string.Equals(TickerMatches[j].Id, item.Id, StringComparison.Ordinal))
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                if (i <= TickerMatches.Count)
                {
                    TickerMatches.Insert(i, item);
                }
                else
                {
                    TickerMatches.Add(item);
                }
            }
            else
            {
                TickerMatches[existingIndex] = item;
                if (existingIndex != i && i < TickerMatches.Count)
                {
                    TickerMatches.Move(existingIndex, i);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _timer.Tick -= OnTimerTick;
        _timer.Stop();
        _timer.Dispose();
    }
}
