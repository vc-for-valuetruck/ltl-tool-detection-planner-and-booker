using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LtlTool.Api.Features.Integrations.Alvys;

/// <summary>
/// Deserialization converters that tolerate the empirical Alvys wire shapes where the
/// documented schema and the reality diverge. Every converter here maps a permissive
/// wire representation (raw string, empty string, wrapper-or-scalar) into a strict CLR
/// type without throwing so a real Alvys response never crashes a boundary controller
/// with an unrelated deserialization exception. Only used on read paths.
/// </summary>
internal static class AlvysWireConverters
{
    /// <summary>Attach these converters to any <see cref="JsonSerializerOptions"/> used
    /// to deserialize Alvys read responses.</summary>
    public static void AddToOptions(JsonSerializerOptions options)
    {
        options.Converters.Add(new TolerantDecimalConverter());
        options.Converters.Add(new TolerantIntConverter());
        options.Converters.Add(new TolerantDateTimeOffsetConverter());
        options.Converters.Add(new TenderDateTimeConverter());
    }
}

/// <summary>
/// Reads decimals from either a JSON number OR a JSON string (Alvys returns Weight, Rate,
/// Volume, Quantity as quoted strings — sometimes empty). Empty strings become null.
/// </summary>
internal sealed class TolerantDecimalConverter : JsonConverter<decimal?>
{
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null: return null;
            case JsonTokenType.Number: return reader.GetDecimal();
            case JsonTokenType.String:
                var s = reader.GetString();
                if (string.IsNullOrWhiteSpace(s)) return null;
                // Strip trailing unit-suffixes like "53'" for Equipment.Length by taking the leading numeric.
                var trimmed = s.TrimEnd('\'', '"', ' ', 'k', 'K', 'g', 'G', 'l', 'L', 'b', 'B');
                if (decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                    return d;
                return null;
            default: return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value);
    }
}

/// <summary>
/// Reads ints from either a JSON number OR a JSON string. Alvys returns SequenceNumber
/// and similar counters as quoted strings.
/// </summary>
internal sealed class TolerantIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null: return null;
            case JsonTokenType.Number: return reader.GetInt32();
            case JsonTokenType.String:
                var s = reader.GetString();
                if (string.IsNullOrWhiteSpace(s)) return null;
                if (int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var i))
                    return i;
                return null;
            default: return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value);
    }
}

/// <summary>Reads DateTimeOffset from JSON string; empty string becomes null (unwraps
/// to nullable via <see cref="Nullable"/> handling in call sites).</summary>
internal sealed class TolerantDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                return dto;
        }
        return null;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value.Value);
    }
}

/// <summary>
/// Reads <see cref="AlvysTenderDateTime"/> from either the documented wrapper object
/// (<c>{"DateTime":"...","TimeZoneCode":"..."}</c>) OR from a plain string (which Alvys
/// actually returns for tender <c>DateImported</c>). Plain-string form auto-wraps.
/// </summary>
internal sealed class TenderDateTimeConverter : JsonConverter<AlvysTenderDateTime?>
{
    public override AlvysTenderDateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                var s = reader.GetString();
                if (string.IsNullOrWhiteSpace(s)) return null;
                if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                    return new AlvysTenderDateTime { DateTime = dto };
                return null;
            case JsonTokenType.StartObject:
                // Manual object parse to avoid recursion into ourselves.
                DateTimeOffset instant = default;
                string? tz = null;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject) break;
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    var prop = reader.GetString();
                    reader.Read();
                    if (string.Equals(prop, "DateTime", StringComparison.OrdinalIgnoreCase))
                    {
                        if (reader.TokenType == JsonTokenType.String)
                        {
                            var v = reader.GetString();
                            if (!string.IsNullOrWhiteSpace(v) &&
                                DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d))
                                instant = d;
                        }
                    }
                    else if (string.Equals(prop, "TimeZoneCode", StringComparison.OrdinalIgnoreCase))
                    {
                        if (reader.TokenType == JsonTokenType.String) tz = reader.GetString();
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
                return new AlvysTenderDateTime { DateTime = instant, TimeZoneCode = tz };
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, AlvysTenderDateTime? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteString("DateTime", value.DateTime);
        if (value.TimeZoneCode is not null) writer.WriteString("TimeZoneCode", value.TimeZoneCode);
        writer.WriteEndObject();
    }
}
