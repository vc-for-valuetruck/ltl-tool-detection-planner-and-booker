namespace LtlTool.Api.Features.Integrations.Alvys.Webhooks;

/// <summary>
/// Lifecycle of a received webhook event. The receiver persists the raw event and acks fast
/// (<see cref="Received"/>); a background processor then advances it to <see cref="Processed"/> or
/// <see cref="Failed"/>. Processing never blocks the HTTP ack, so a slow read model can never cause
/// Alvys to auto-disable the subscription.
/// </summary>
public enum AlvysWebhookProcessingState
{
    /// <summary>Persisted and acked; awaiting background processing.</summary>
    Received,

    /// <summary>Background processing completed — the affected read models were invalidated/refreshed.</summary>
    Processed,

    /// <summary>Background processing failed; the raw event is retained for inspection, not re-acked.</summary>
    Failed,
}

/// <summary>
/// A durable record of one received Alvys webhook delivery. The primary key is the Alvys-supplied
/// event id (<c>X-Alvys-Event-Id</c>), which makes at-least-once delivery idempotent: a duplicate
/// delivery of the same event id is detected on insert and acked without reprocessing.
///
/// <para>
/// The raw body is stored verbatim for audit/replay. No signing secret and no Authorization material
/// is ever stored — only the business payload and the non-secret delivery headers.
/// </para>
/// </summary>
public sealed class AlvysWebhookEvent
{
    /// <summary>The Alvys event id (<c>X-Alvys-Event-Id</c>) — the natural idempotency key / PK.</summary>
    public required string EventId { get; set; }

    /// <summary>The event type (<c>X-Alvys-Event</c>), e.g. <c>load.status.changed</c> / <c>load.changed</c>.</summary>
    public required string EventType { get; set; }

    /// <summary>The unix-seconds timestamp Alvys signed (<c>X-Alvys-Timestamp</c>).</summary>
    public long Timestamp { get; set; }

    /// <summary>The delivery attempt number Alvys reported (<c>X-Alvys-Attempt</c>), when present.</summary>
    public int? Attempt { get; set; }

    /// <summary>The load number extracted from the payload (<c>data.load</c>), when present.</summary>
    public string? LoadNumber { get; set; }

    /// <summary>The verbatim request body, bounded, for audit/replay. Never contains auth material.</summary>
    public required string RawBody { get; set; }

    public AlvysWebhookProcessingState ProcessingState { get; set; } = AlvysWebhookProcessingState.Received;

    /// <summary>Error detail when <see cref="ProcessingState"/> is <see cref="AlvysWebhookProcessingState.Failed"/>.</summary>
    public string? ProcessingError { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}

/// <summary>
/// Tracks the last time the LTL tool learned a given load changed upstream (via webhook). Billing
/// readiness / exceptions consult this so a freshly-changed load can be re-fetched rather than served
/// from a stale cache. This is a freshness pointer only — it holds no Alvys operational values, so it
/// never becomes a competing source of truth (Alvys remains authoritative).
/// </summary>
public sealed class LoadFreshnessRecord
{
    /// <summary>The load number this freshness marker refers to (PK).</summary>
    public required string LoadNumber { get; set; }

    /// <summary>The most recent event type that touched this load.</summary>
    public string? LastEventType { get; set; }

    /// <summary>The id of the most recent webhook event that touched this load.</summary>
    public string? LastEventId { get; set; }

    /// <summary>When the most recent change was observed (from the webhook), UTC.</summary>
    public DateTimeOffset LastChangedAt { get; set; }

    /// <summary>How many webhook events have touched this load (lifetime).</summary>
    public int ChangeCount { get; set; }
}
