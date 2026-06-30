using LtlTool.Api.Features.Integrations.Alvys;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// The pickup→delivery window a candidate's equipment must be free across, plus the assessment of
/// truck/trailer events against it.
/// </summary>
public sealed class EquipmentEventAssessment
{
    /// <summary>
    /// Not-evaluated assessment: no usable window or no fetch attempted. The scorer/validator treat
    /// this as <i>unavailable</i> (excluded from the denominator) rather than asserting availability.
    /// </summary>
    public static readonly EquipmentEventAssessment NotEvaluated = new();

    /// <summary>True only when a window was known and the event search was actually issued.</summary>
    public bool Evaluated { get; init; }

    /// <summary>Human-readable descriptions of repair/maintenance/other events overlapping the window.</summary>
    public IReadOnlyList<string> Conflicts { get; init; } = [];

    public bool HasConflict => Conflicts.Count > 0;
}

/// <summary>
/// A batch of truck/trailer events fetched once for a whole candidate set over a load window.
/// <see cref="Evaluated"/> records whether the fetch was actually issued (a usable window and
/// equipment to query); when false the per-candidate assessment stays not-evaluated.
/// </summary>
public sealed class EquipmentEventBatch
{
    public static readonly EquipmentEventBatch NotEvaluated = new();

    public bool Evaluated { get; init; }
    public IReadOnlyList<AlvysTruckEvent> TruckEvents { get; init; } = [];
    public IReadOnlyList<AlvysTrailerEvent> TrailerEvents { get; init; } = [];
}

/// <summary>
/// Interprets truck/trailer events against a load's pickup/delivery window to explain equipment
/// availability risk. Pure and synchronous — the (batched) event fetch lives in
/// <see cref="MatchService"/>/the controller; this only classifies overlaps and never marks
/// equipment available merely because no events were returned for an un-fetched window.
/// </summary>
public sealed class EquipmentEventAnalyzer(IOptions<LtlOptions> options)
{
    private readonly LtlOptions _options = options.Value;

    /// <summary>
    /// Assess the candidate's truck/trailer events against the load window. <paramref name="evaluated"/>
    /// must be set by the caller to whether the event search was actually issued for a known window;
    /// when false the result is <see cref="EquipmentEventAssessment.NotEvaluated"/>.
    /// </summary>
    public EquipmentEventAssessment Assess(
        DateTimeOffset? windowStart,
        DateTimeOffset? windowEnd,
        IEnumerable<AlvysTruckEvent> truckEvents,
        IEnumerable<AlvysTrailerEvent> trailerEvents,
        bool evaluated)
    {
        if (!evaluated || (windowStart is null && windowEnd is null))
            return EquipmentEventAssessment.NotEvaluated;

        var start = windowStart ?? windowEnd!.Value;
        var end = windowEnd ?? windowStart!.Value;

        var conflicts = new List<string>();

        foreach (var ev in truckEvents)
        {
            if (IsConflict(ev.EventType, ev.StartDate, ev.EndDate, start, end))
                conflicts.Add(Describe("Truck", ev.EventType, ev.Title, ev.StartDate, ev.EndDate));
        }

        foreach (var ev in trailerEvents)
        {
            if (IsConflict(ev.EventType, ev.StartDate, ev.EndDate, start, end))
                conflicts.Add(Describe("Trailer", ev.EventType, ev.Title, ev.StartDate, ev.EndDate));
        }

        return new EquipmentEventAssessment { Evaluated = true, Conflicts = conflicts };
    }

    private bool IsConflict(
        string? eventType, DateTimeOffset? evStart, DateTimeOffset? evEnd,
        DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        if (!IsConflictType(eventType)) return false;
        if (evStart is null && evEnd is null) return false;

        var s = evStart ?? evEnd!.Value;
        var e = evEnd ?? evStart!.Value;

        // Standard interval overlap.
        return s <= windowEnd && e >= windowStart;
    }

    private bool IsConflictType(string? eventType) =>
        !string.IsNullOrWhiteSpace(eventType)
        && _options.EquipmentConflictEventTypes.Any(
            t => eventType.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static string Describe(
        string equipment, string? eventType, string? title,
        DateTimeOffset? start, DateTimeOffset? end)
    {
        var label = !string.IsNullOrWhiteSpace(title) ? title!
            : !string.IsNullOrWhiteSpace(eventType) ? eventType!
            : "event";
        var window = start is { } s
            ? end is { } e ? $" ({s:yyyy-MM-dd}–{e:yyyy-MM-dd})" : $" ({s:yyyy-MM-dd})"
            : string.Empty;
        return $"{equipment} {eventType ?? "event"} \"{label}\"{window} overlaps the load window.";
    }
}
