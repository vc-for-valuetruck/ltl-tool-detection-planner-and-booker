namespace LtlTool.Api.Features.Ltl.YardIngestion;

/// <summary>
/// The freight-affecting meaning of a Yard event, independent of the exact wire string Yard sends.
/// The v1 contract fixes a canonical <c>eventType</c> vocabulary (documented in
/// <c>docs/YARD_LTL_INGESTION.md</c>), but Yard's producer may emit close variants (dotted vs.
/// kebab, "captured"/"updated" suffixes), so classification normalizes before matching.
///
/// <para><b>Administrative</b> and <b>Unknown</b> events are still persisted to the immutable inbox
/// for audit/replay, but they never create or advance a scheduler projection — they are excluded
/// from scheduler input exactly as the boundary contract requires.</para>
/// </summary>
public enum YardEventCategory
{
    /// <summary>Not a freight-affecting event (gate log, note, visitor scheduling, report, login…).</summary>
    Administrative = 0,

    /// <summary>Recognized as freight-affecting but not a category this pipeline models yet.</summary>
    Unknown = 1,

    Arrival,
    Departure,
    CheckIn,
    LoadStart,
    LoadComplete,
    UnloadStart,
    UnloadComplete,
    TrailerAssignment,
    DockAssignment,
    FreightDimensions,
    FreightWeight,
    Appointment,
    Exception,
    Hold,
    Release,
    Cancellation,
    Split,
    Consolidation,
}

/// <summary>
/// Deterministic, table-driven classifier that maps a Yard <c>eventType</c> string to a
/// <see cref="YardEventCategory"/>. Pure and side-effect free so it is trivially unit-testable and
/// safe to call on the hot ingest path.
/// </summary>
public static class YardEventClassifier
{
    /// <summary>
    /// Canonical + tolerated aliases → category. Keys are already normalized (see <see cref="Normalize"/>).
    /// Kept explicit rather than fuzzy so an unrecognized type fails <em>closed</em> to
    /// <see cref="YardEventCategory.Unknown"/> instead of being silently mis-projected.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, YardEventCategory> Map =
        new Dictionary<string, YardEventCategory>(StringComparer.Ordinal)
        {
            ["arrival"] = YardEventCategory.Arrival,
            ["truck.arrived"] = YardEventCategory.Arrival,
            ["truck.arrival"] = YardEventCategory.Arrival,
            ["gate.arrival"] = YardEventCategory.Arrival,

            ["departure"] = YardEventCategory.Departure,
            ["truck.departed"] = YardEventCategory.Departure,
            ["gate.departure"] = YardEventCategory.Departure,

            ["check.in"] = YardEventCategory.CheckIn,
            ["checkin"] = YardEventCategory.CheckIn,
            ["driver.check.in"] = YardEventCategory.CheckIn,

            ["load.start"] = YardEventCategory.LoadStart,
            ["loading.started"] = YardEventCategory.LoadStart,
            ["load.complete"] = YardEventCategory.LoadComplete,
            ["loading.completed"] = YardEventCategory.LoadComplete,
            ["dock.complete"] = YardEventCategory.LoadComplete,

            ["unload.start"] = YardEventCategory.UnloadStart,
            ["unloading.started"] = YardEventCategory.UnloadStart,
            ["unload.complete"] = YardEventCategory.UnloadComplete,
            ["unloading.completed"] = YardEventCategory.UnloadComplete,

            ["trailer.assignment"] = YardEventCategory.TrailerAssignment,
            ["trailer.assigned"] = YardEventCategory.TrailerAssignment,
            ["dock.assignment"] = YardEventCategory.DockAssignment,
            ["dock.assigned"] = YardEventCategory.DockAssignment,

            ["freight.dimensions"] = YardEventCategory.FreightDimensions,
            ["freight.dimensions.captured"] = YardEventCategory.FreightDimensions,
            ["freight.weight"] = YardEventCategory.FreightWeight,
            ["freight.weight.captured"] = YardEventCategory.FreightWeight,

            ["appointment"] = YardEventCategory.Appointment,
            ["appointment.scheduled"] = YardEventCategory.Appointment,
            ["appointment.updated"] = YardEventCategory.Appointment,

            ["exception"] = YardEventCategory.Exception,
            ["exception.raised"] = YardEventCategory.Exception,

            ["hold"] = YardEventCategory.Hold,
            ["hold.placed"] = YardEventCategory.Hold,
            ["security.hold"] = YardEventCategory.Hold,

            ["release"] = YardEventCategory.Release,
            ["load.released"] = YardEventCategory.Release,
            ["security.release"] = YardEventCategory.Release,

            ["cancellation"] = YardEventCategory.Cancellation,
            ["cancelled"] = YardEventCategory.Cancellation,
            ["canceled"] = YardEventCategory.Cancellation,

            ["split"] = YardEventCategory.Split,
            ["load.split"] = YardEventCategory.Split,
            ["consolidation"] = YardEventCategory.Consolidation,
            ["load.consolidated"] = YardEventCategory.Consolidation,

            // Explicitly administrative wire types Yard is known to emit — mapped so they are
            // documented as excluded rather than merely falling through to Unknown.
            ["gate.log"] = YardEventCategory.Administrative,
            ["note.added"] = YardEventCategory.Administrative,
            ["visitor.scheduled"] = YardEventCategory.Administrative,
            ["report.generated"] = YardEventCategory.Administrative,
            ["user.login"] = YardEventCategory.Administrative,
        };

    /// <summary>Classifies an event type. Null/blank/unrecognized → <see cref="YardEventCategory.Unknown"/>.</summary>
    public static YardEventCategory Classify(string? eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            return YardEventCategory.Unknown;
        return Map.TryGetValue(Normalize(eventType), out var category)
            ? category
            : YardEventCategory.Unknown;
    }

    /// <summary>
    /// True when the category creates or advances a scheduler projection. Administrative and Unknown
    /// events are audited but never become scheduler input.
    /// </summary>
    public static bool AffectsSchedulerInput(YardEventCategory category) =>
        category is not (YardEventCategory.Administrative or YardEventCategory.Unknown);

    /// <summary>
    /// Lower-cases and collapses <c>_</c>, <c>-</c>, whitespace, and <c>/</c> to <c>.</c> so
    /// <c>Truck_Arrived</c>, <c>truck-arrived</c>, and <c>truck.arrived</c> all match one key.
    /// </summary>
    public static string Normalize(string eventType)
    {
        var chars = new char[eventType.Length];
        var length = 0;
        var lastWasDot = false;
        foreach (var raw in eventType.Trim())
        {
            var c = char.ToLowerInvariant(raw);
            if (c is '_' or '-' or ' ' or '/' or '\t' or ':')
                c = '.';
            if (c == '.')
            {
                if (lastWasDot || length == 0) continue; // squash runs / leading separators
                lastWasDot = true;
            }
            else
            {
                lastWasDot = false;
            }
            chars[length++] = c;
        }
        // Trim a trailing separator.
        if (length > 0 && chars[length - 1] == '.') length--;
        return new string(chars, 0, length);
    }
}
