using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using FootballTray.Core.Dto;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Preferences;

namespace FootballTray.Client.Taskbar;

/// <summary>
/// Structured display state emitted to the taskbar ticker UI.
/// Atomic pairing of current match ID (for click routing) and formatted text.
/// </summary>
public sealed record TickerDisplayState(string? FixtureId, string FormattedText, string? HomeTeamLogoUrl = null, string? AwayTeamLogoUrl = null);

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

    public TickerDisplayState GetDisplayState(TickerConnectionState connectionState)
    {
        var text = TrayTickerFormatter.FormatMatchTicker(CurrentCandidate, connectionState);
        return new TickerDisplayState(
            CurrentCandidate?.Id,
            text,
            CurrentCandidate?.HomeTeam?.LogoUrl,
            CurrentCandidate?.AwayTeam?.LogoUrl);
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
    private readonly TickerRotationState _state = new();
    private TickerConnectionState _currentConnectionState = TickerConnectionState.OnlineFresh;
    private bool _isDisposed;

    public static readonly TimeSpan RotationInterval = TimeSpan.FromSeconds(7);

    public TickerRotationState State => _state;
    public bool IsCycling => _timer.IsRunning;

    public TaskbarTickerCoordinator(
        DispatcherQueue dispatcherQueue,
        Action<string?, string> displayCallback,
        Action<string?, string, string?, string?>? extendedDisplayCallback = null)
        : this(new DispatcherQueueTickerTimer(dispatcherQueue), displayCallback, extendedDisplayCallback)
    {
    }

    internal TaskbarTickerCoordinator(
        ITickerTimer timer,
        Action<string?, string> displayCallback,
        Action<string?, string, string?, string?>? extendedDisplayCallback = null)
    {
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        _displayCallback = displayCallback ?? throw new ArgumentNullException(nameof(displayCallback));
        _extendedDisplayCallback = extendedDisplayCallback;

        _timer.Interval = RotationInterval;
        _timer.Tick += OnTimerTick;
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

        IReadOnlyList<TrayMatchDto> candidates;
        if (connectionState == TickerConnectionState.NoCacheOffline)
        {
            candidates = Array.Empty<TrayMatchDto>();
        }
        else
        {
            candidates = TrayTickerFormatter.GetTickerCandidates(trayData, preferences);
        }

        _state.ApplyCandidates(candidates);

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

        _state.Advance();
        EmitCurrentDisplay();
    }

    private void EmitCurrentDisplay()
    {
        var displayState = _state.GetDisplayState(_currentConnectionState);
        _displayCallback(displayState.FixtureId, displayState.FormattedText);
        _extendedDisplayCallback?.Invoke(displayState.FixtureId, displayState.FormattedText, displayState.HomeTeamLogoUrl, displayState.AwayTeamLogoUrl);
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
