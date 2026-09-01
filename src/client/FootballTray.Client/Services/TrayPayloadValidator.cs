using System;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services;

/// <summary>
/// Provides strict structural validation for client-consumed Tray data envelopes.
/// Rejects malformed or incomplete server responses before they reach the local cache or UI.
/// </summary>
public static class TrayPayloadValidator
{
    /// <summary>
    /// Validates the structural integrity of the ApiResponseEnvelope and its match collection.
    /// </summary>
    public static bool IsValid(ApiResponseEnvelope<TrayDataDto>? envelope, out string? errorMessage)
    {
        if (envelope == null)
        {
            errorMessage = "Response envelope is null.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(envelope.SchemaVersion))
        {
            errorMessage = "Envelope schemaVersion is missing or empty.";
            return false;
        }

        if (envelope.Data == null)
        {
            errorMessage = "Envelope data payload is null.";
            return false;
        }

        if (envelope.Data.Matches == null)
        {
            errorMessage = "Envelope matches collection is null.";
            return false;
        }

        foreach (var match in envelope.Data.Matches)
        {
            if (string.IsNullOrWhiteSpace(match.Id))
            {
                errorMessage = "Match has an empty or null Id.";
                return false;
            }

            if (match.HomeTeam == null || (string.IsNullOrWhiteSpace(match.HomeTeam.Name) && string.IsNullOrWhiteSpace(match.HomeTeam.ShortName) && string.IsNullOrWhiteSpace(match.HomeTeam.Id)))
            {
                errorMessage = $"Match '{match.Id}' has an invalid or missing HomeTeam.";
                return false;
            }

            if (match.AwayTeam == null || (string.IsNullOrWhiteSpace(match.AwayTeam.Name) && string.IsNullOrWhiteSpace(match.AwayTeam.ShortName) && string.IsNullOrWhiteSpace(match.AwayTeam.Id)))
            {
                errorMessage = $"Match '{match.Id}' has an invalid or missing AwayTeam.";
                return false;
            }

            if (match.Score == null)
            {
                errorMessage = $"Match '{match.Id}' has a null Score object.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(match.State))
            {
                errorMessage = $"Match '{match.Id}' has an empty or null State.";
                return false;
            }

            if (match.RecentEvents != null)
            {
                foreach (var ev in match.RecentEvents)
                {
                    if (string.IsNullOrWhiteSpace(ev.Id) || string.IsNullOrWhiteSpace(ev.Type) || string.IsNullOrWhiteSpace(ev.TeamId))
                    {
                        errorMessage = $"Match '{match.Id}' contains an invalid event structure.";
                        return false;
                    }
                }
            }
        }

        errorMessage = null;
        return true;
    }
}
