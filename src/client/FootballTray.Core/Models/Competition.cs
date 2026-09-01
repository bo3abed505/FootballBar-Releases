using System;

namespace FootballTray.Core.Models;

/// <summary>
/// Provider-independent representation of a league or tournament competition.
/// </summary>
public sealed record Competition(
    string Id,
    string Name,
    string? Code = null,
    string? Country = null,
    string? LogoUrl = null);
