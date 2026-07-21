namespace LtlTool.Api.Features.Integrations.Alvys.Webhooks;

/// <summary>
/// Read-only admin projection of a received webhook event for the ops panel. The raw body is included
/// (verbatim) for inspection/replay; it carries the business payload only — never any auth material or
/// signing secret. Processing state and error let an operator see whether the read-model refresh landed.
/// </summary>
public sealed record AlvysWebhookEventView(
    string EventId,
    string EventType,
    long Timestamp,
    int? Attempt,
    string? LoadNumber,
    string ProcessingState,
    string? ProcessingError,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ProcessedAt,
    string RawBody)
{
    public static AlvysWebhookEventView From(AlvysWebhookEvent evt) => new(
        evt.EventId,
        evt.EventType,
        evt.Timestamp,
        evt.Attempt,
        evt.LoadNumber,
        evt.ProcessingState.ToString(),
        evt.ProcessingError,
        evt.ReceivedAt,
        evt.ProcessedAt,
        evt.RawBody);
}

/// <summary>
/// The admin listing payload: recent events (newest first), the lifetime total, and an honest snapshot
/// of receiver configuration (whether a signing secret is present, the tolerance window, and the
/// upstream auto-disable threshold). No secret value is ever included — only whether one is configured.
/// </summary>
public sealed record AlvysWebhookAdminView(
    IReadOnlyList<AlvysWebhookEventView> Events,
    int TotalReceived,
    bool SecretConfigured,
    int ToleranceSeconds,
    int AutoDisableThreshold);
