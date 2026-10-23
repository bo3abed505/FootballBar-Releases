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
    private readonly ITickerTimer _timer;
    private readonly Action<string?, string> _displayCallback;
    private readonly Action<string?, string, string?, string?>? _extendedDisplayCallback;
    private readonly Action<string?, string, string?, string?, TrayMatchDto?, bool>? _fullDisplayCallback;
    private readonly TickerRotationState _state = new();
    private TickerConnectionState _currentConnectionState = TickerConnectionState.OnlineFresh;
    private UserPreferences? _currentPreferences;
    private string? _pinnedMatchId;
    private TrayMatchDto? _pinnedMatch;
    private TrayDataDto? _latestTrayData;
    private bool _isDisposed;
    private MatchCenterWindow? _matchCenterWindow;

    public static readonly TimeSpan RotationInterval = TimeSpan.FromSeconds(10);

    public ObservableCollection<TrayMatchDto> TickerMatches { get; } = new();
    public TickerRotationState State => _state;
    public bool IsCycling => _timer.IsRunning;
    public string? PinnedMatchId => _pinnedMatchId;
    public bool IsPinned => !string.IsNullOrEmpty(_pinnedMatchId);
    private readonly Dictionary<string, (int Home, int Away)> _previousScores = new(StringComparer.OrdinalIgnoreCase);
    public string? LastGoalJumpMatchId { get; private set; }

    public TaskbarTickerCoordinator(
        DispatcherQueue dispatcherQueue,
        Action<string?, string> displayCallback,
        Action<string?, string, string?, string?>? extendedDisplayCallback = null,
        Action<string?, string, string?, string?, TrayMatchDto?, bool>? fullDisplayCallback = null)
        : this(new DispatcherQueueTickerTimer(dispatcherQueue), displayCallback, extendedDisplayCallback, fullDisplayCallback)
    {
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

    public async void OpenMatchCenter(string matchId)
    {
        if (string.IsNullOrWhiteSpace(matchId) || _isDisposed) return;

        try
        {
            using var apiClient = new FootballApiClient();
            var response = await apiClient.GetMatchDetailsAsync(matchId);

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
            }
            else
            {
                AppDiagnosticsLogger.Log($"[ERROR][MatchCenter][OpenFailed] Failed to open MatchCenterWindow from ticker: API returned unsuccessful response for match {matchId} (Error: {response.ErrorMessage ?? "null payload"})");
            }
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Log($"[ERROR][MatchCenter][OpenFailed] Failed to open MatchCenterWindow from ticker: {ex.Message}\n{ex.StackTrace}");
            System.Diagnostics.Debug.WriteLine($"[TICKER] Failed to open MatchCenterWindow: {ex.Message}");
        }
    }

    public bool PinMatch(string? matchId = null)
    {
        if (_isDisposed) return false;

        var targetId = matchId ?? _state.CurrentCandidate?.Id;
        if (string.IsNullOrEmpty(targetId)) return false;

        // Check active candidates first, then fall back to latest tray data (allows pinning unfollowed matches)
        TrayMatchDto? match = _state.Candidates.FirstOrDefault(m => string.Equals(m.Id, targetId, StringComparison.Ordinal))
            ?? _latestTrayData?.Matches?.FirstOrDefault(m => string.Equals(m.Id, targetId, StringComparison.Ordinal))
            ?? _pinnedMatch;

        if (match != null)
        {
            _pinnedMatchId = targetId;
            _pinnedMatch = match;
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

    public void ApplySnapshot(TrayDataDto? trayData, TickerConnectionState connectionState, UserPreferences? preferences = null)
    {
        if (_isDisposed) return;

        _currentConnectionState = connectionState;
        _currentPreferences = preferences;
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

                if (_previousScores.TryGetValue(match.Id, out var prevScore))
                {
                    bool isLive = FixtureRelevanceEngine.IsLive(match);
                    bool isFollowed = FixtureRelevanceEngine.EvaluateRelevance(match, teamIds, compIds).IsRelevant;
                    bool isPinned = !string.IsNullOrWhiteSpace(_pinnedMatchId) && (string.Equals(match.Id, _pinnedMatchId, StringComparison.OrdinalIgnoreCase) || match.Id.EndsWith($"_{_pinnedMatchId}") || _pinnedMatchId.EndsWith($"_{match.Id}"));

                    if (isLive && (isFollowed || isPinned) && (currentHome != prevScore.Home || currentAway != prevScore.Away))
                    {
                        goalJumpMatchId ??= match.Id;
                    }
                }

                _previousScores[match.Id] = (currentHome, currentAway);
            }
        }

        IReadOnlyList<TrayMatchDto> candidates;
        if (connectionState == TickerConnectionState.NoCacheOffline)
        {
            candidates = Array.Empty<TrayMatchDto>();
        }
        else if (preferences == null)
        {
            candidates = TrayTickerFormatter.GetTickerCandidates(trayData, null);
        }
        else
        {
            // If carousel rotation is disabled by user preference, do not cycle candidates
            if (!preferences.EnableTickerCarousel)
            {
                candidates = Array.Empty<TrayMatchDto>();
            }
            else
            {
                var teamIds = preferences.GetFollowedTeamIdSet();
                var compIds = preferences.GetFollowedCompetitionIdSet();
                bool hasFollowed = teamIds.Count > 0 || compIds.Count > 0;

                if (!hasFollowed)
                {
                    candidates = Array.Empty<TrayMatchDto>();
                }
                else
                {
                    var followedMatches = trayData?.Matches?
                        .Where(m => (m.KickoffTime.ToLocalTime().Date == DateTime.Today || FixtureRelevanceEngine.IsLive(m)) &&
                                    FixtureRelevanceEngine.EvaluateRelevance(m, teamIds, compIds).IsRelevant)
                        .ToList() ?? new List<TrayMatchDto>();

                    if (followedMatches.Count == 0)
                    {
                        candidates = Array.Empty<TrayMatchDto>();
                    }
                    else
                    {
                        var live = FixtureRelevanceEngine.PrioritizeMatches(followedMatches.Where(FixtureRelevanceEngine.IsLive), preferences);
                        if (live.Count > 0)
                        {
                            candidates = live;
                        }
                        else
                        {
                            var upcoming = FixtureRelevanceEngine.PrioritizeMatches(followedMatches.Where(FixtureRelevanceEngine.IsPrematch), preferences);
                            if (upcoming.Count > 0)
                            {
                                candidates = upcoming;
                            }
                            else
                            {
                                candidates = FixtureRelevanceEngine.PrioritizeMatches(followedMatches, preferences);
                            }
                        }
                    }
                }
            }
        }

        // If a match was explicitly pinned, guarantee it is present in candidates even if unfollowed or date changed
        if (!string.IsNullOrEmpty(_pinnedMatchId))
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
            else
            {
                // Pinned match removed or concluded from snapshot: clear pinned state gracefully
                _pinnedMatchId = null;
                _pinnedMatch = null;
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
        if (!string.IsNullOrEmpty(_pinnedMatchId))
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

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_isDisposed) return;

        if (!_state.CanCycle)
        {
            _timer.Stop();
            return;
        }

        if (!string.IsNullOrEmpty(_pinnedMatchId))
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
    public void SmartMergeTickerMatches(IReadOnlyList<TrayMatchDto> incoming)
    {
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
