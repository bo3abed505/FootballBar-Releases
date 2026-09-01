using System;
using System.Collections.Generic;

namespace FootballTray.Client.Services.Notifications;

public sealed record NotificationActivationResult
{
    public string? Action { get; init; }
    public string? FixtureId { get; init; }
    public string? EventUid { get; init; }
    public bool IsValid { get; init; }
}

/// <summary>
/// Safely parses Windows notification activation arguments back into structured navigation parameters.
/// Supports both query-string and IDictionary arguments.
/// </summary>
public static class NotificationActivationHandler
{
    public static NotificationActivationResult Parse(IDictionary<string, string>? arguments) => ParseDictionary(arguments);

    public static NotificationActivationResult Parse(string? activationArguments) => ParseQueryString(activationArguments);

    public static NotificationActivationResult ParseDictionary(IDictionary<string, string>? arguments)
    {
        if (arguments == null || arguments.Count == 0)
        {
            return new NotificationActivationResult { IsValid = false };
        }

        try
        {
            arguments.TryGetValue("action", out var action);
            arguments.TryGetValue("fixtureId", out var fixtureId);
            arguments.TryGetValue("eventUid", out var eventUid);

            bool isValid = !string.IsNullOrWhiteSpace(action) && !string.IsNullOrWhiteSpace(fixtureId);

            return new NotificationActivationResult
            {
                Action = action,
                FixtureId = fixtureId,
                EventUid = eventUid,
                IsValid = isValid
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NOTIFICATIONS] Malformed activation arguments dictionary: {ex.Message}");
            return new NotificationActivationResult { IsValid = false };
        }
    }

    public static NotificationActivationResult ParseQueryString(string? activationArguments)
    {
        if (string.IsNullOrWhiteSpace(activationArguments))
        {
            return new NotificationActivationResult { IsValid = false };
        }

        try
        {
            var parts = activationArguments.Split('&', StringSplitOptions.RemoveEmptyEntries);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var part in parts)
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2)
                {
                    map[Uri.UnescapeDataString(kv[0].Trim())] = Uri.UnescapeDataString(kv[1].Trim());
                }
            }

            return ParseDictionary(map);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NOTIFICATIONS] Malformed activation arguments string: {ex.Message}");
            return new NotificationActivationResult { IsValid = false };
        }
    }
}
