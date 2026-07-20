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
/// Reads decimals from four empirical Alvys shapes so scalar consumers keep working when
/// the wire diverges from the documented schema:
/// <list type="bullet">
///   <item>JSON number → direct.</item>
///   <item>JSON string (Weight/Rate/Volume/Quantity are quoted, sometimes empty) → parsed,
///         empty becomes null; trailing unit-suffixes like <c>53'</c> or <c>lb</c> stripped.</item>
///   <item>JSON object with a numeric <c>Amount</c> field (money shape:
///         <c>{"Amount":575.0,"Currency":840}</c>) → returns <c>Amount</c>. Applies to
///         Linehaul / FuelSurcharge / CustomerAccessorials / CustomerRate / TotalPaid.</item>
///   <item>JSON object with a numeric <c>Value</c> field (weight/volume shape:
///         <c>{"Value":10000.0,"UnitOfMeasure":"Pounds"}</c>) → returns <c>Value</c>.</item>
///   <item>JSON object with a nested <c>Distance</c> object (mileage shape:
///         <c>{"Distance":{"Value":10.0,"UnitOfMeasure":"Miles"},"Source":"Engine"}</c>) →
///         returns <c>Distance.Value</c>. Applies to CustomerMileage.</item>
/// </list>
/// Any other object shape is skipped and returns null instead of throwing — that keeps
/// consolidation/search endpoints resilient to Alvys shipping new nested fields.
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
            case JsonTokenType.StartObject:
                return ReadFromObject(ref reader);
            default:
                SafeSkip(ref reader);
                return null;
        }
    }

    /// <summary>
    /// Walks a Money/Weight/Volume/Mileage wrapper object and returns the first meaningful
    /// decimal we can extract. Prefers <c>Amount</c> (money), then <c>Value</c>
    /// (weight/volume), then nested <c>Distance.Value</c> (mileage). Unknown properties are
    /// skipped via <see cref="SafeSkip"/> because the streaming JSON reader over an HTTP
    /// response body has <c>isFinalBlock=false</c> and would throw on <see cref="Utf8JsonReader.Skip"/>.
    /// </summary>
    private static decimal? ReadFromObject(ref Utf8JsonReader reader)
    {
        decimal? amount = null;
        decimal? value = null;
        decimal? distanceValue = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            var name = reader.GetString();
            reader.Read();

            if (string.Equals(name, "Amount", StringComparison.OrdinalIgnoreCase))
            {
                amount = ReadScalarDecimal(ref reader);
            }
            else if (string.Equals(name, "Value", StringComparison.OrdinalIgnoreCase))
            {
                value = ReadScalarDecimal(ref reader);
            }
            else if (string.Equals(name, "Distance", StringComparison.OrdinalIgnoreCase)
                     && reader.TokenType == JsonTokenType.StartObject)
            {
                // Nested {"Distance":{"Value":10.0,"UnitOfMeasure":"Miles"}} — walk it directly.
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject) break;
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    var inner = reader.GetString();
                    reader.Read();
                    if (string.Equals(inner, "Value", StringComparison.OrdinalIgnoreCase))
                    {
                        distanceValue = ReadScalarDecimal(ref reader);
                    }
                    else
                    {
                        SafeSkip(ref reader);
                    }
                }
            }
            else
            {
                SafeSkip(ref reader);
            }
        }

        return amount ?? value ?? distanceValue;
    }

    /// <summary>Reads a decimal from a Number or numeric-String token; nulls/anything else → null.</summary>
    private static decimal? ReadScalarDecimal(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number: return reader.GetDecimal();
            case JsonTokenType.String:
                var s = reader.GetString();
                if (string.IsNullOrWhiteSpace(s)) return null;
                if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                    return d;
                return null;
            default:
                SafeSkip(ref reader);
                return null;
        }
    }

    /// <summary>
    /// Streaming-safe token skip. On a partial-block reader (typical for HTTP response
    /// bodies deserialized via ReadFromJsonAsync), <see cref="Utf8JsonReader.Skip"/> throws
    /// <c>InvalidOperationException: Cannot skip tokens on partial JSON</c>. Use
    /// <see cref="Utf8JsonReader.TrySkip"/> and fall back to hand-walking start/end tokens
    /// so an unknown nested object/array in a wrapper (e.g. <c>{Amount, Currency, Meta:{...}}</c>)
    /// doesn't crash the whole load-search deserialization.
    /// </summary>
    private static void SafeSkip(ref Utf8JsonReader reader)
    {
        if (reader.TrySkip()) return;

        // Manual walk: only StartObject/StartArray have nested content. For scalars, TrySkip
        // would only fail on a truncated buffer — not applicable to a whole-body Alvys read.
        if (reader.TokenType != JsonTokenType.StartObject &&
            reader.TokenType != JsonTokenType.StartArray) return;

        var depth = 1;
        while (depth > 0 && reader.Read())
        {
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) depth++;
            else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) depth--;
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
