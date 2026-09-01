using System;

namespace FootballTray.Core.Models;

/// <summary>
/// Provider-independent representation of a football club or national team.
/// </summary>
public sealed record Team(
    string Id,
    string Name,
    string ShortName,
    string? LogoUrl = null,
    string? Country = null);
