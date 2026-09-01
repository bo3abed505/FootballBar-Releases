using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services.Notifications;

/// <summary>
/// Coordinates match alert evaluation, deduplication, formatting, and notification dispatch.
/// Completely decoupled from network polling.
/// </summary>
public sealed class NotificationService
{
    private readonly NotificationEngine _engine;
    private readonly INotificationRenderer _renderer;
    private readonly NotificationDeduplicationStore _store;

    public NotificationEngine Engine => _engine;
    public NotificationDeduplicationStore Store => _store;
    public INotificationRenderer Renderer => _renderer;

    public event Action<NotificationPayload>? NotificationDispatched;

    public NotificationService(
        NotificationEngine? engine = null,
        INotificationRenderer? renderer = null,
        NotificationDeduplicationStore? store = null)
    {
        _store = store ?? engine?.DeduplicationStore ?? new NotificationDeduplicationStore();
        _engine = engine ?? new NotificationEngine(_store);
        _renderer = renderer ?? new WindowsAppNotificationRenderer();
    }

    /// <summary>
    /// Loads deduplication history from disk at startup.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _store.LoadAsync(cancellationToken);
    }

    /// <summary>
    /// Processes snapshot transitions, emits alerts for new events/transitions, and persists deduplication state.
    /// </summary>
    public async Task<List<NotificationPayload>> ProcessTrayUpdateAsync(
        TrayDataDto? previous,
        TrayDataDto? current,
        CancellationToken cancellationToken = default)
    {
        if (current?.Matches == null)
        {
            return [];
        }

        var candidates = _engine.EvaluateUpdates(previous, current);
        var dispatchedPayloads = new List<NotificationPayload>();

        foreach (var candidate in candidates)
        {
            var payload = NotificationPayloadFormatter.Format(candidate);

            // Render alert through injected renderer
            _renderer.RenderNotification(payload);

            // Record key in deduplication store
            _store.MarkNotified(candidate.DeduplicationKey);

            dispatchedPayloads.Add(payload);
            NotificationDispatched?.Invoke(payload);
        }

        if (dispatchedPayloads.Count > 0)
        {
            await _store.SaveAsync(cancellationToken);
        }

        return dispatchedPayloads;
    }
}
