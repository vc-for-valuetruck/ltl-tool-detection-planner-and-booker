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
}
