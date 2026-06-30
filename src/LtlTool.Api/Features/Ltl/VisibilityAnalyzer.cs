using LtlTool.Api.Features.Integrations.Alvys;

namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// Turns raw inbound/outbound Alvys visibility-history events into the LTL decision-support view:
/// a noteworthy-event timeline for the detail drawer and explicit exception flags for failed/errored
/// tracking shares. Pure and synchronous — the per-load history fetch lives in
/// <see cref="LtlLoadService"/>; this only interprets what was fetched and never fabricates events.
/// </summary>
public sealed class VisibilityAnalyzer
{
    /// <summary>Status values on a visibility event that mean the share did not succeed.</summary>
    private static readonly HashSet<string> FailureStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Failed", "Error", "Errored", "Rejected",
    };

    /// <summary>
    /// Event-type tokens (case-insensitive substring) kept in the detail timeline because they
    /// carry operational meaning (appointment/arrival/departure/delivery milestones).
    /// </summary>
    private static readonly string[] NoteworthyEventTypes =
        ["Appointment", "Arrival", "Arrived", "Departure", "Departed", "Delivery", "Delivered", "Pickup", "Picked"];

    /// <summary>
    /// Build the visibility context (timeline + evaluated flag) from the fetched inbound/outbound
    /// histories. <paramref name="inbound"/>/<paramref name="outbound"/> are the raw bare arrays.
    /// </summary>
    public VisibilityContext BuildContext(
        IReadOnlyList<AlvysVisibilityHistoryEvent> inbound,
        IReadOnlyList<AlvysVisibilityHistoryEvent> outbound)
    {
        var events = Project("Inbound", inbound)
            .Concat(Project("Outbound", outbound))
            .Where(e => e.IsFailure || IsNoteworthy(e.EventType))
            .OrderByDescending(e => e.SharedAt ?? DateTimeOffset.MinValue)
            .ToList();

        return new VisibilityContext { Evaluated = true, Events = events };
    }

    /// <summary>
    /// Derive exception flags from the fetched visibility histories: every failed/errored share
    /// becomes a non-blocking operational risk carrying the load number, event type, shared time,
    /// destination, reason and error text where available.
    /// </summary>
    public IReadOnlyList<LtlExceptionFlag> DeriveExceptions(
        string loadNumber,
        IReadOnlyList<AlvysVisibilityHistoryEvent> inbound,
        IReadOnlyList<AlvysVisibilityHistoryEvent> outbound)
    {
        var exceptions = new List<LtlExceptionFlag>();

        foreach (var (direction, source) in new[] { ("Inbound", inbound), ("Outbound", outbound) })
        {
            foreach (var ev in source)
            {
                if (!IsFailure(ev)) continue;
                exceptions.Add(new LtlExceptionFlag
                {
                    Code = "VISIBILITY_FAILED",
                    Message = BuildFailureMessage(direction, loadNumber, ev),
                    BlocksBilling = false,
                });
            }
        }

        return exceptions;
    }

    private IEnumerable<VisibilityEventView> Project(
        string direction, IReadOnlyList<AlvysVisibilityHistoryEvent> events) =>
        events.Select(ev => new VisibilityEventView
        {
            Direction = direction,
            EventType = ev.EventType,
            Status = ev.Status,
            SharedAt = ev.SharedAt,
            Destination = ev.Destination,
            Reason = ev.Reason,
            Error = ev.Error,
            IsFailure = IsFailure(ev),
        });

    private static bool IsFailure(AlvysVisibilityHistoryEvent ev) =>
        (ev.Status is not null && FailureStatuses.Contains(ev.Status))
        || !string.IsNullOrWhiteSpace(ev.Error);

    private static bool IsNoteworthy(string? eventType) =>
        !string.IsNullOrWhiteSpace(eventType)
        && NoteworthyEventTypes.Any(t => eventType.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static string BuildFailureMessage(
        string direction, string loadNumber, AlvysVisibilityHistoryEvent ev)
    {
        var parts = new List<string>
        {
            $"{direction} visibility {Describe(ev.Status, "share")} failed for load {loadNumber}",
        };

        if (!string.IsNullOrWhiteSpace(ev.EventType)) parts.Add($"event '{ev.EventType}'");
        if (ev.SharedAt is { } sharedAt) parts.Add($"shared {sharedAt:yyyy-MM-dd HH:mm}Z");
        if (!string.IsNullOrWhiteSpace(ev.Destination)) parts.Add($"to {ev.Destination}");
        if (!string.IsNullOrWhiteSpace(ev.Reason)) parts.Add($"reason: {ev.Reason}");
        if (!string.IsNullOrWhiteSpace(ev.Error)) parts.Add($"error: {ev.Error}");

        return string.Join("; ", parts) + ".";
    }

    private static string Describe(string? status, string fallback) =>
        string.IsNullOrWhiteSpace(status) ? fallback : status!;
}
