namespace LtlTool.Api.Features.Ltl.Consolidation;

/// <summary>
/// Durable metadata row for a saved recurring-lane template (Phase 2.5). A template captures the
/// <i>shape</i> of a consolidation a dispatcher expects to run again — its corridor, the customer,
/// the origin/destination labels and a cadence hint — so the planner can surface "this lane again
/// this week" without re-discovering it from scratch.
///
/// <para>
/// A template is <b>internal Value Truck data</b>, not an Alvys artifact: it references a corridor
/// code and a customer name, never a live load id, tender, or trip. Nothing here is read from or
/// written back to Alvys — saving a template is purely a note-to-self about a lane worth watching.
/// </para>
/// </summary>
public sealed class LaneTemplateRecord
{
    public required string Id { get; set; }

    /// <summary>Dispatcher-authored name, e.g. "Verdef Laredo→Dallas weekly".</summary>
    public required string Name { get; set; }

    /// <summary>Corridor this template targets (matches a <see cref="ConsolidationCorridorOptions.Code"/>).</summary>
    public required string CorridorCode { get; set; }

    /// <summary>Customer the recurring lane is for. Null when the template is corridor-only.</summary>
    public string? CustomerName { get; set; }

    /// <summary>Origin place label at save time (e.g. "Laredo, TX"). Descriptive only.</summary>
    public string? OriginLabel { get; set; }

    /// <summary>Destination place label at save time (e.g. "Dallas, TX"). Descriptive only.</summary>
    public string? DestinationLabel { get; set; }

    /// <summary>
    /// Cadence in days between expected repeats (e.g. 7 for weekly). Drives the "this lane again"
    /// surfacing window. Null when the dispatcher saved a lane without committing to a cadence.
    /// </summary>
    public int? CadenceDays { get; set; }

    /// <summary>Free-text note the dispatcher attached. Optional.</summary>
    public string? Notes { get; set; }

    /// <summary>The user (email/upn) who saved the template.</summary>
    public required string CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Read-model of a saved lane template for the templates list.</summary>
public sealed record LaneTemplateView(
    string Id,
    string Name,
    string CorridorCode,
    string? CustomerName,
    string? OriginLabel,
    string? DestinationLabel,
    int? CadenceDays,
    string? Notes,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Client request to save a recurring-lane template. Only the name and corridor are required;
/// customer/labels/cadence/notes are optional descriptive context. No load ids — a template is a
/// lane-shape note, not a pointer to live Alvys freight.
/// </summary>
public sealed class SaveLaneTemplateRequest
{
    public string? Name { get; set; }
    public string? CorridorCode { get; set; }
    public string? CustomerName { get; set; }
    public string? OriginLabel { get; set; }
    public string? DestinationLabel { get; set; }
    public int? CadenceDays { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Filter for listing lane templates by corridor and/or customer.</summary>
public sealed record LaneTemplateQuery(
    string? CorridorCode = null,
    string? CustomerName = null,
    int Max = 100);

/// <summary>Shared mapping helpers so the store, controller and tests stay consistent.</summary>
public static class LaneTemplateMapping
{
    public static LaneTemplateView ToView(LaneTemplateRecord record) =>
        new(
            record.Id,
            record.Name,
            record.CorridorCode,
            record.CustomerName,
            record.OriginLabel,
            record.DestinationLabel,
            record.CadenceDays,
            record.Notes,
            record.CreatedBy,
            record.CreatedAt,
            record.UpdatedAt);
}
