namespace LtlTool.Api.Features.Integrations.Yard.Webhooks;

/// <summary>The Yard webhook event types the LTL receiver understands (see the shared Yard↔LTL contract).</summary>
public static class YardEventTypes
{
    public const string TruckArrived = "TruckArrived";
    public const string LoadReleased = "LoadReleased";
    public const string LtlDraftCreated = "LtlDraftCreated";
}

/// <summary>
/// Lifecycle of a received Yard webhook event. The receiver persists the raw event and acks fast
/// (<see cref="Received"/>); a background processor then advances it to <see cref="Processed"/> or
/// <see cref="Failed"/>. Processing never blocks the HTTP ack.
/// </summary>
public enum YardWebhookProcessingState
{
    /// <summary>Persisted and acked; awaiting background processing.</summary>
    Received,

    /// <summary>Background processing completed — cache invalidated / opportunity persisted / fanned out.</summary>
    Processed,

    /// <summary>Background processing failed; the raw event is retained for inspection.</summary>
    Failed,
}

/// <summary>
/// A durable record of one received Yard webhook delivery. The primary key is the Yard-supplied event
/// id (<c>X-Yard-Event-Id</c>), which makes at-least-once delivery idempotent: a duplicate delivery of
/// the same id is detected on insert and acked without reprocessing.
///
/// <para>
/// The raw body is stored verbatim for audit/replay. No signing secret and no Authorization material is
/// ever stored — only the business payload and the non-secret delivery headers. No photo bytes and no
/// driver PII beyond ids cross this boundary.
/// </para>
/// </summary>
public sealed class YardWebhookEvent
{
    /// <summary>The Yard event id (<c>X-Yard-Event-Id</c>) — the natural idempotency key / PK.</summary>
    public required string EventId { get; set; }

    /// <summary>The event type (<c>X-Yard-Event</c> / payload <c>eventType</c>), e.g. <c>TruckArrived</c>.</summary>
    public required string EventType { get; set; }

    /// <summary>The unix-seconds timestamp the Yard signed (<c>X-Yard-Timestamp</c>).</summary>
    public long Timestamp { get; set; }

    /// <summary>The yard code from the payload (<c>yardCode</c>), when present.</summary>
    public string? YardCode { get; set; }

    /// <summary>Tractor id from the payload, when present.</summary>
    public string? TractorId { get; set; }

    /// <summary>Trailer id from the payload, when present.</summary>
    public string? TrailerId { get; set; }

    /// <summary>Driver id from the payload, when present.</summary>
    public string? DriverId { get; set; }

    /// <summary>The verbatim request body, bounded, for audit/replay. Never contains auth material.</summary>
    public required string RawBody { get; set; }

    public YardWebhookProcessingState ProcessingState { get; set; } = YardWebhookProcessingState.Received;

    /// <summary>Error detail when <see cref="ProcessingState"/> is <see cref="YardWebhookProcessingState.Failed"/>.</summary>
    public string? ProcessingError { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}

/// <summary>
/// A yard-originated LTL consolidation opportunity, persisted from an <c>LtlDraftCreated</c> webhook. It
/// is <b>not</b> a source of operational truth — it is an inbound suggestion the dock surfaces as a card
/// so a dispatcher can act on it inside the tool's own Alvys-backed flow. Freight lines are stored as
/// JSON (the shape is defined by the shared contract); null fields stay null and are surfaced as "—".
/// </summary>
public sealed class YardLtlOpportunity
{
    /// <summary>Surrogate key. The yard draft id is unique but the webhook event id anchors idempotency.</summary>
    public required string Id { get; set; }

    /// <summary>The webhook event id that created this opportunity (dedupe anchor).</summary>
    public required string EventId { get; set; }

    /// <summary>The Yard draft id (<c>draft.draftId</c>).</summary>
    public required string DraftId { get; set; }

    /// <summary>The yard code the draft originated from.</summary>
    public string? YardCode { get; set; }

    /// <summary>Parent load id (<c>draft.parentLoadId</c>) — the consolidation parent.</summary>
    public string? ParentLoadId { get; set; }

    /// <summary>Sibling load ids (<c>draft.siblingLoadIds</c>), stored as JSON.</summary>
    public required string SiblingLoadIdsJson { get; set; }

    /// <summary>Freight summary lines (<c>draft.freight</c>), stored verbatim as JSON.</summary>
    public required string FreightJson { get; set; }

    /// <summary>The dock station that created the draft (<c>draft.createdByStation</c>).</summary>
    public string? CreatedByStation { get; set; }

    /// <summary>When the yard scanned the freight (<c>draft.scannedAt</c>), if present.</summary>
    public DateTimeOffset? ScannedAt { get; set; }

    /// <summary>When the LTL tool received / persisted this opportunity (UTC).</summary>
    public DateTimeOffset ReceivedAt { get; set; }
}
