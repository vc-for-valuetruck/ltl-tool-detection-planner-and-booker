using System.Text.Json;
using System.Text.Json.Serialization;

namespace LtlTool.Api.Features.Integrations.Yard.Webhooks;

/// <summary>
/// Parses the JSON body of a Yard webhook into its envelope (and, for <c>LtlDraftCreated</c>, the
/// draft). Tolerant of missing fields — every property is optional and a malformed body degrades to a
/// null parse rather than throwing, so one bad delivery never stalls the receiver.
/// </summary>
public static class YardWebhookPayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Parses the envelope; returns null when the body is not valid JSON.</summary>
    public static YardWebhookEnvelope? TryParse(string rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody)) return null;
        try
        {
            return JsonSerializer.Deserialize<YardWebhookEnvelope>(rawBody, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The full Yard webhook envelope (all fields optional; shape from the shared contract).</summary>
public sealed record YardWebhookEnvelope
{
    [JsonPropertyName("eventId")] public string? EventId { get; init; }
    [JsonPropertyName("eventType")] public string? EventType { get; init; }
    [JsonPropertyName("eventTime")] public DateTimeOffset? EventTime { get; init; }
    [JsonPropertyName("yardCode")] public string? YardCode { get; init; }
    [JsonPropertyName("tractorId")] public string? TractorId { get; init; }
    [JsonPropertyName("trailerId")] public string? TrailerId { get; init; }
    [JsonPropertyName("driverId")] public string? DriverId { get; init; }
    [JsonPropertyName("loadIds")] public IReadOnlyList<string>? LoadIds { get; init; }
    [JsonPropertyName("draft")] public YardDraftEnvelope? Draft { get; init; }
}

/// <summary>The <c>draft</c> block on an <c>LtlDraftCreated</c> event.</summary>
public sealed record YardDraftEnvelope
{
    [JsonPropertyName("draftId")] public string? DraftId { get; init; }
    [JsonPropertyName("parentLoadId")] public string? ParentLoadId { get; init; }
    [JsonPropertyName("siblingLoadIds")] public IReadOnlyList<string>? SiblingLoadIds { get; init; }
    [JsonPropertyName("freight")] public IReadOnlyList<YardFreightLine>? Freight { get; init; }
    [JsonPropertyName("scannedAt")] public DateTimeOffset? ScannedAt { get; init; }
    [JsonPropertyName("createdByStation")] public string? CreatedByStation { get; init; }
}

/// <summary>One freight line in a draft. Every measure is nullable — a missing measure stays null.</summary>
public sealed record YardFreightLine
{
    [JsonPropertyName("loadId")] public string? LoadId { get; init; }
    [JsonPropertyName("pallets")] public int? Pallets { get; init; }
    [JsonPropertyName("pieces")] public int? Pieces { get; init; }
    [JsonPropertyName("weightLbs")] public double? WeightLbs { get; init; }
    [JsonPropertyName("dims")] public YardFreightDims? Dims { get; init; }
    [JsonPropertyName("osd")] public YardFreightOsd? Osd { get; init; }
}

public sealed record YardFreightDims
{
    [JsonPropertyName("lengthIn")] public double? LengthIn { get; init; }
    [JsonPropertyName("widthIn")] public double? WidthIn { get; init; }
    [JsonPropertyName("heightIn")] public double? HeightIn { get; init; }
}

public sealed record YardFreightOsd
{
    [JsonPropertyName("overage")] public bool? Overage { get; init; }
    [JsonPropertyName("shortage")] public bool? Shortage { get; init; }
    [JsonPropertyName("damage")] public bool? Damage { get; init; }
    [JsonPropertyName("notes")] public string? Notes { get; init; }
}
