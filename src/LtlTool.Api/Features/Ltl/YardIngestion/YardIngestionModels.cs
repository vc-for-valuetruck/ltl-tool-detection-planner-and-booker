using System.Text.Json;

namespace LtlTool.Api.Features.Ltl.YardIngestion;

/// <summary>
/// Readiness of a scheduler projection. Every projection starts <see cref="Provisional"/> the instant
/// it is persisted (scheduler-eligible immediately) and only advances to <see cref="Ready"/> once the
/// required physical milestones — dock completion and security clearance — are observed and no active
/// hold/cancellation is in effect.
/// </summary>
public enum ScheduleReadiness
{
    /// <summary>Persisted and scheduler-eligible, but required milestones are not all satisfied yet.</summary>
    Provisional = 0,

    /// <summary>Dock complete + security cleared + not held/cancelled — safe for confident execution.</summary>
    Ready = 1,
}

/// <summary>Hold/release/cancel state derived from the latest such event by occurrence time.</summary>
public enum ScheduleHoldState
{
    /// <summary>No hold, cancellation, or (yet) an explicit release has been seen.</summary>
    None = 0,

    /// <summary>An active hold blocks execution until released.</summary>
    Held = 1,

    /// <summary>A hold was explicitly released (security clearance signal).</summary>
    Released = 2,

    /// <summary>The freight was cancelled — terminal; the scheduler must drop it.</summary>
    Cancelled = 3,
}

/// <summary>
/// One append-only inbox record: the verbatim record of a single accepted Yard event. The wire fields
/// (<see cref="EventType"/>, <see cref="PayloadJson"/>, ids, timestamps) are never mutated after insert;
/// only the derived classification (<see cref="Category"/>/<see cref="AffectsSchedulerInput"/>) may be
/// healed on a later rebuild when an older build had shelved the type as Unknown. The primary key is a
/// stable dedupe key derived from the envelope <c>eventId</c> plus the source record identity, which
/// makes at-least-once delivery idempotent.
///
/// <para>Internal LTL data — Alvys is never read or written here. Yard remains a peer system; this
/// row is a copy of what Yard pushed over the HTTP contract, not a cross-database read.</para>
/// </summary>
public sealed class YardEventRecord
{
    /// <summary>
    /// Idempotency key / primary key: <c>{eventId}:{sourceSystem}:{sourceRecordType}:{sourceRecordId}</c>.
    /// A duplicate delivery collides here and is acked without a second projection.
    /// </summary>
    public required string DedupeKey { get; set; }

    /// <summary>The Yard-supplied event id (UUID string). Unique across the source system.</summary>
    public required string EventId { get; set; }

    /// <summary>Contract schema version (v1 today).</summary>
    public int SchemaVersion { get; set; }

    /// <summary>The raw event type string Yard sent (preserved verbatim for audit).</summary>
    public required string EventType { get; set; }

    /// <summary>
    /// The classified freight meaning, stored as a readable string. Derived from <see cref="EventType"/>
    /// at ingest; a rebuild/replay re-runs the classifier and heals a stale <c>Unknown</c> (an event an
    /// older build didn't recognize) to its real category. The verbatim wire fields are never rewritten.
    /// </summary>
    public required string Category { get; set; }

    /// <summary>True when this event fed a scheduler projection; false for administrative/unknown.</summary>
    public bool AffectsSchedulerInput { get; set; }

    /// <summary>When the event occurred in the yard (UTC), as reported by Yard.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>When the LTL tool accepted and persisted the event (UTC).</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    public required string SourceSystem { get; set; }
    public required string SourceRecordType { get; set; }
    public required string SourceRecordId { get; set; }
    public required string YardLocationId { get; set; }

    /// <summary>Optional cross-system correlation id for tracing a workflow across apps.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>The verbatim event payload JSON object. <c>nvarchar(max)</c>; never contains secrets.</summary>
    public required string PayloadJson { get; set; }

    /// <summary>
    /// Monotonic per-store ordinal (DB identity). Used as the deterministic tie-breaker when two events
    /// share an <see cref="OccurredAt"/> during replay, so projection rebuilds are order-stable.
    /// </summary>
    public long Sequence { get; set; }
}

/// <summary>
/// The normalized, scheduler-facing projection for one Yard source record (e.g. one appointment or
/// one trailer). Deterministically rebuilt from the immutable event log every time an event for the
/// record is accepted, so it is always consistent with the audit trail and independent of delivery
/// order.
///
/// <para><b>Honest missing state.</b> Freight/equipment/timing fields stay <c>null</c> until a Yard
/// event actually carries them — they are never coerced to 0/false. A scheduler reading this must
/// treat null as "unknown", not "empty".</para>
/// </summary>
public sealed class YardScheduleInput
{
    /// <summary>Primary key: <c>{sourceSystem}:{sourceRecordType}:{sourceRecordId}</c>.</summary>
    public required string Id { get; set; }

    public required string SourceSystem { get; set; }
    public required string SourceRecordType { get; set; }
    public required string SourceRecordId { get; set; }
    public required string YardLocationId { get; set; }

    /// <summary>Always true once persisted — the scheduler may consider this record immediately.</summary>
    public bool SchedulerEligible { get; set; }

    /// <summary>Readiness, stored as a readable string.</summary>
    public required string Readiness { get; set; }

    /// <summary>
    /// Completeness/confidence in [0,1]: the fraction of required readiness milestones
    /// (dock completion, security clearance) observed so far.
    /// </summary>
    public double Completeness { get; set; }

    /// <summary>Hold/release/cancel state, stored as a readable string.</summary>
    public required string HoldState { get; set; }

    public bool DockCompleted { get; set; }
    public bool SecurityCleared { get; set; }

    /// <summary>Whether any unresolved exception event has been raised against this record.</summary>
    public bool HasOpenException { get; set; }

    // Latest-occurrence markers.
    public DateTimeOffset LatestOccurredAt { get; set; }
    public string? LatestEventType { get; set; }
    public string? LatestEventId { get; set; }
    public int EventCount { get; set; }

    // Equipment / dock (latest-writer-wins by occurrence order).
    public string? TruckId { get; set; }
    public string? TrailerId { get; set; }
    public string? DockId { get; set; }

    // Freight (nullable — missing stays missing).
    public double? WeightLbs { get; set; }
    public double? LengthInches { get; set; }
    public double? WidthInches { get; set; }
    public double? HeightInches { get; set; }
    public int? PieceCount { get; set; }

    // Routing / timing.
    public string? OriginLocationId { get; set; }
    public string? DestinationLocationId { get; set; }
    public DateTimeOffset? AppointmentAt { get; set; }

    // Split / consolidation relationship.
    public string? RelationshipType { get; set; }
    public string? ParentSourceRecordId { get; set; }

    /// <summary>Related source record ids for split/consolidation, JSON array. Never null in the DB.</summary>
    public required string RelatedRecordIdsJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The inbound v1 envelope Yard POSTs to <c>/api/v1/yard-events</c>. Bound from JSON.</summary>
public sealed class YardEventEnvelope
{
    public Guid? EventId { get; set; }
    public int? SchemaVersion { get; set; }
    public string? EventType { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
    public string? SourceSystem { get; set; }
    public string? SourceRecordType { get; set; }
    public string? SourceRecordId { get; set; }
    public string? YardLocationId { get; set; }
    public string? CorrelationId { get; set; }

    /// <summary>Free-form event payload. Must be a JSON object per the contract.</summary>
    public JsonElement? Payload { get; set; }
}
