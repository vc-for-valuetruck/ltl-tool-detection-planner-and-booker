using LtlTool.Api.Features.Ltl.Arrivals;
using LtlTool.Api.Features.Ltl.Consolidation;

namespace LtlTool.Api.Features.Ltl.Dock;

/// <summary>
/// Dock mode (Phase 2.5): the dock-worker-facing "easy match loads" flow. When a truck lands at a
/// yard the dock worker decides whether to split or combine loads: the first truck's load is the
/// parent (BOL-controlling) and the other loads riding that truck are siblings. Dock mode is a thin,
/// tablet-first orchestration over the existing consolidation + arrivals services — it reuses the
/// Arrivals Board, the <see cref="ConsolidationCandidateService"/> and the
/// <see cref="ConsolidationPlanService"/> rather than reinventing any consolidation logic.
///
/// <para>
/// Read-only against Alvys: arrivals, candidates and the combined plan are all derived from live
/// Alvys reads or static config, and a combine records an internal audit with
/// <c>AlvysWriteback = NotPerformed</c>. The dispatcher executes the schedule/BOL edits in Alvys
/// manually from the generated click card — nothing here mutates Alvys.
/// </para>
/// </summary>
public sealed class DockWarehousesResponse
{
    /// <summary>The configured yards a dock worker can pick (Laredo / Dallas in the pilot).</summary>
    public required IReadOnlyList<WarehouseSummary> Warehouses { get; init; }
}

/// <summary>
/// Request to combine a parent load with one or more sibling loads at the dock. Mirrors the
/// consolidation plan request shape so the same corridor / customer-policy gates apply. Nothing
/// here is an Alvys write — the response carries a preview plan + an internal audit record.
/// </summary>
public sealed class DockCombineRequest
{
    /// <summary>The BOL-controlling parent load (id or load number).</summary>
    public string ParentLoadId { get; set; } = "";

    /// <summary>The sibling loads riding the same truck (ids or load numbers).</summary>
    public List<string> SiblingLoadIds { get; set; } = [];

    /// <summary>Corridor code; defaults to the pilot LAREDO_TO_DALLAS when omitted.</summary>
    public string? CorridorCode { get; set; }

    /// <summary>
    /// The yard the combine happened at. Drives which <c>Ltl:Dock:NotifyRecipients</c> list is
    /// notified; null/unknown means no yard-specific recipients and notification is disabled.
    /// </summary>
    public string? WarehouseCode { get; set; }
}

/// <summary>
/// Result of a dock combine: the full consolidation plan preview (click card + combined driver-RPM
/// economics, trailer fit, accessorial pre-checks, blockers) plus the internal audit record the
/// combine wrote. The SPA renders the BOL packet / dock manifest and the Alvys click card from the
/// <see cref="Plan"/>; the <see cref="Audit"/> is the leadership-visible record that a combine
/// happened. <see cref="ConsolidationAuditRecord.AlvysWriteback"/> is always <c>NotPerformed</c>.
/// </summary>
public sealed class DockCombineResponse
{
    public required ConsolidationPlanResponse Plan { get; init; }
    public required ConsolidationAuditRecord Audit { get; init; }

    /// <summary>
    /// Outcome of the combine-summary notification. Always present — its <see cref="DockNotificationResult.State"/>
    /// is <c>Disabled</c> when no recipients are configured for the yard, so the SPA can render an
    /// honest retry chip only when a configured send actually failed. Never blocks the combine.
    /// </summary>
    public required DockNotificationResult Notification { get; init; }
}

/// <summary>
/// Honest outcome of the dock combine notification. <see cref="State"/> mirrors the underlying
/// notification delivery state (<c>Delivered</c>/<c>Pending</c>/<c>NotConfigured</c>/<c>Failed</c>)
/// plus a Dock-specific <c>Disabled</c> when the yard has no configured recipients. The SPA shows a
/// retry chip for <c>Failed</c>. Recipient addresses are echoed so the UI can name who was targeted;
/// they are already server-side config, never a secret derived at request time.
/// </summary>
public sealed class DockNotificationResult
{
    public required string State { get; init; }
    public required IReadOnlyList<string> Recipients { get; init; }
    public string? Detail { get; init; }
}

/// <summary>
/// Request to record that a just-committed dock combine was undone by the worker (one-tap Undo). The
/// combine wrote nothing to Alvys, so an undo reverses nothing there either — it records a second,
/// leadership-visible audit entry marking the retraction. Mirrors <see cref="DockCombineRequest"/> so
/// the same plan is rebuilt read-only for the audit context.
/// </summary>
public sealed class DockUndoRequest
{
    public string ParentLoadId { get; set; } = "";
    public List<string> SiblingLoadIds { get; set; } = [];
    public string? CorridorCode { get; set; }
}

/// <summary>Result of an undo: the retraction audit record (<c>AlvysWriteback = NotPerformed</c>).</summary>
public sealed class DockUndoResponse
{
    public required ConsolidationAuditRecord Audit { get; init; }
}

/// <summary>
/// Fire-and-forget body for the dock combine effectiveness metric (Phase 2.5). Carries only
/// status-level context — warehouse, sibling count, tap count, and time-to-combine in milliseconds
/// (parent tap → docs rendered). No plan body, no PII.
/// </summary>
public sealed class DockCombineMetricRequest
{
    public string? WarehouseCode { get; set; }
    public int? SiblingCount { get; set; }
    public int? TapCount { get; set; }
    public long? TimeToCombineMs { get; set; }
}
