using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FootballTray.Core.Serialization;

/// <summary>
/// Source-generation compatible JsonConverter for DateTimeOffset that strictly requires an explicit UTC 'Z'
/// or timezone offset (+/-hh:mm). Rejects offset-less timestamps to prevent local machine timezone inference.
/// Always normalizes to UTC upon reading.
/// </summary>
public sealed class UtcDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
{
    private static readonly Regex ExplicitOffsetRegex = new(
        @"(?:[zZ]|[+-]\d{2}(?::?\d{2})?)$",
        RegexOptions.Compiled);

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected string token for publishedAt timestamp, but found {reader.TokenType}.");
        }

        string? raw = reader.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new JsonException("Timestamp string is null, empty, or whitespace.");
        }

        string trimmed = raw.Trim();
        if (!ExplicitOffsetRegex.IsMatch(trimmed))
        {
            throw new JsonException($"Timestamp '{raw}' does not contain an explicit UTC 'Z' or timezone offset. Offset-less timestamps are rejected.");
        }

        if (!DateTimeOffset.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces,
                out var dto))
        {
            throw new JsonException($"Timestamp '{raw}' is not a valid ISO-8601 DateTimeOffset.");
        }

        return dto.ToUniversalTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
    }
}
