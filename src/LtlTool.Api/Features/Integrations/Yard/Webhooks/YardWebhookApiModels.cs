using System.Text.Json;

namespace LtlTool.Api.Features.Integrations.Yard.Webhooks;

/// <summary>
/// Read-only admin projection of a received Yard webhook event for the ops panel. The raw body is
/// included verbatim for inspection/replay; it carries the business payload only — never any auth
/// material or signing secret.
/// </summary>
public sealed record YardWebhookEventView(
    string EventId,
    string EventType,
    long Timestamp,
    string? YardCode,
    string? TractorId,
    string? TrailerId,
    string? DriverId,
    string ProcessingState,
    string? ProcessingError,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ProcessedAt,
    string RawBody)
{
    public static YardWebhookEventView From(YardWebhookEvent evt) => new(
        evt.EventId,
        evt.EventType,
        evt.Timestamp,
        evt.YardCode,
        evt.TractorId,
        evt.TrailerId,
        evt.DriverId,
        evt.ProcessingState.ToString(),
        evt.ProcessingError,
        evt.ReceivedAt,
        evt.ProcessedAt,
        evt.RawBody);
}

/// <summary>
/// The admin listing payload: recent events (newest first), the lifetime total, and an honest snapshot
/// of receiver configuration (whether the receiver is enabled, whether a signing secret is present, and
/// the tolerance window). No secret value is ever included — only whether one is configured.
/// </summary>
public sealed record YardWebhookAdminView(
    IReadOnlyList<YardWebhookEventView> Events,
    int TotalReceived,
    bool Enabled,
    bool SecretConfigured,
    int ToleranceSeconds);

/// <summary>
/// A yard-originated LTL opportunity projected for the dock incoming-opportunity card. Freight lines are
/// deserialized from the stored JSON; every measure is nullable so the UI can render "—" for a value the
/// yard never captured (honest missing state, never fabricated).
/// </summary>
public sealed record YardOpportunityView(
    string Id,
    string DraftId,
    string? YardCode,
    string? ParentLoadId,
    IReadOnlyList<string> SiblingLoadIds,
    IReadOnlyList<YardFreightLine> Freight,
    string? CreatedByStation,
    DateTimeOffset? ScannedAt,
    DateTimeOffset ReceivedAt)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static YardOpportunityView From(YardLtlOpportunity o)
    {
        IReadOnlyList<string> siblings = [];
        IReadOnlyList<YardFreightLine> freight = [];
        try
        {
            siblings = JsonSerializer.Deserialize<List<string>>(o.SiblingLoadIdsJson, Options) ?? [];
        }
        catch (JsonException) { /* honest empty on a corrupt row */ }
        try
        {
            freight = JsonSerializer.Deserialize<List<YardFreightLine>>(o.FreightJson, Options) ?? [];
        }
        catch (JsonException) { /* honest empty on a corrupt row */ }

        return new YardOpportunityView(
            o.Id,
            o.DraftId,
            o.YardCode,
            o.ParentLoadId,
            siblings,
            freight,
            o.CreatedByStation,
            o.ScannedAt,
            o.ReceivedAt);
    }
}
