namespace LtlTool.Api.Features.Ltl.Notifications;

/// <summary>
/// Workflow stages that fire a notification trigger (owner spec 2026-07-20,
/// <c>ltl_notifications_spec.md</c>). The Search → Match → Assign → Bill → Billed lifecycle
/// exposes eight points where "multiple people at once" need aligning on a trip.
///
/// <para>
/// First slice emits <see cref="ConsolidationPlanCreated"/> only: it is sourced from the
/// existing in-memory consolidation audit store, so the engine needs no new Alvys read
/// plumbing and the trigger is demoable end-to-end. The remaining stages are declared here so
/// the recipient-group config, feed UI and idempotency keys are stable as later slices wire the
/// Alvys trip-stop / invoice detection. They are NOT fabricated in the meantime.
/// </para>
/// </summary>
public enum NotificationStage
{
    /// <summary>T1 — a consolidation plan was recorded as an internal audit entry.</summary>
    ConsolidationPlanCreated,

    /// <summary>T2 — a click card (dispatch instruction) was generated. Reserved for a later slice.</summary>
    ClickCardGenerated,

    /// <summary>T3 — assignment confirmed in Alvys (driver/truck on the trip). Reserved.</summary>
    AssignmentConfirmed,

    /// <summary>T4 — pickup arrived / departed. Reserved.</summary>
    PickupEvent,

    /// <summary>T5 — delivery arrived / delivered. Reserved.</summary>
    DeliveryEvent,

    /// <summary>T6 — billing-ready (docs + charges complete). Reserved.</summary>
    BillingReady,

    /// <summary>T7 — invoiced. Reserved.</summary>
    Invoiced,

    /// <summary>T8 — exception raised (late pickup/delivery, idle at stop, missing data). Reserved.</summary>
    ExceptionRaised,
}

/// <summary>The transport a recipient is reached on.</summary>
public enum NotificationChannelKind
{
    /// <summary>Always-on in-app notification feed / bell. Works in Demo mode.</summary>
    InApp,

    /// <summary>Microsoft Teams incoming webhook. Config-gated.</summary>
    Teams,

    /// <summary>Email. Config-gated.</summary>
    Email,
}

/// <summary>
/// Honest per-channel delivery state. NEVER coerced to "Delivered" when a channel is not
/// configured or a send failed — the feed must tell the truth about what actually went out.
/// </summary>
public enum NotificationDeliveryState
{
    /// <summary>The recipient was reached on this channel.</summary>
    Delivered,

    /// <summary>Accepted for delivery but not yet confirmed (e.g. transport not wired in this slice).</summary>
    Pending,

    /// <summary>The channel is not configured server-side, so nothing was sent. Honest, not a failure.</summary>
    NotConfigured,

    /// <summary>A configured channel attempted delivery and failed.</summary>
    Failed,
}

/// <summary>A single recipient of a stage notification (name + the channel/address to reach them).</summary>
public sealed class NotificationRecipient
{
    public required string Name { get; init; }
    public NotificationChannelKind Channel { get; init; } = NotificationChannelKind.InApp;

    /// <summary>Channel address (Teams webhook alias, email address). Null for in-app role labels.</summary>
    public string? Address { get; init; }
}

/// <summary>The outcome of dispatching one notification to one channel.</summary>
public sealed class NotificationDelivery
{
    public required NotificationChannelKind Channel { get; init; }
    public required NotificationDeliveryState State { get; init; }

    /// <summary>Recipient names reached (or targeted) on this channel.</summary>
    public required IReadOnlyList<string> Recipients { get; init; }

    /// <summary>Human-readable detail — e.g. "Teams webhook not configured". Never a secret.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// A fired notification trigger. Immutable once recorded. The <see cref="IdempotencyKey"/>
/// (stage + load + occurrence timestamp) guarantees re-polls and restarts never double-fire the
/// same real-world event.
/// </summary>
public sealed class NotificationEvent
{
    public required string Id { get; init; }

    /// <summary>Dedupe key: <c>{Stage}:{loadNumber|planId}:{occurredAt:O}</c>.</summary>
    public required string IdempotencyKey { get; init; }

    public required NotificationStage Stage { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }

    public string? LoadId { get; init; }
    public string? LoadNumber { get; init; }
    public string? PlanId { get; init; }

    /// <summary>SPA route the feed row links to (e.g. <c>/ltl/loads/L-100234</c>). Null when none.</summary>
    public string? LinkPath { get; init; }

    /// <summary>When the underlying real-world event occurred (source timestamp).</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>When the engine fired the trigger.</summary>
    public required DateTimeOffset FiredAt { get; init; }

    /// <summary>Per-channel delivery outcomes (fan-out to "multiple people at once").</summary>
    public required IReadOnlyList<NotificationDelivery> Deliveries { get; init; }
}
