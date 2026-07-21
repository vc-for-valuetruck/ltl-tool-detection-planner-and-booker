using LtlTool.Api.Features.Ltl.Arrivals;
using LtlTool.Api.Features.Ltl.Consolidation;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Dock;

/// <summary>
/// Orchestrates the Dock mode flow (Phase 2.5) by composing the existing, already-tested
/// consolidation + arrivals services. It adds no new consolidation logic of its own: the arrivals
/// come from <see cref="LaredoArrivalsService"/>, the sibling suggestions from
/// <see cref="ConsolidationCandidateService"/>, and the combined plan from
/// <see cref="ConsolidationPlanService"/>. Every method is read-only against Alvys; the one
/// state-changing action — <see cref="CombineAsync"/> — records an internal audit only.
/// </summary>
public sealed class DockService(
    LaredoArrivalsService arrivals,
    ConsolidationCandidateService candidates,
    ConsolidationPlanService plans,
    IConsolidationAuditStore audits,
    DockNotificationService notifications,
    IOptions<ConsolidationOptions> options)
{
    private readonly LaredoArrivalsService _arrivals = arrivals;
    private readonly ConsolidationCandidateService _candidates = candidates;
    private readonly ConsolidationPlanService _plans = plans;
    private readonly IConsolidationAuditStore _audits = audits;
    private readonly DockNotificationService _notifications = notifications;
    private readonly ConsolidationOptions _opts = options.Value;

    /// <summary>
    /// The configured yards a dock worker can pick. Honest projection of static config — never a
    /// place to declare a yard at runtime. Empty when no warehouses are configured.
    /// </summary>
    public DockWarehousesResponse ListWarehouses()
    {
        var warehouses = _opts.Warehouses
            .Select(w => new WarehouseSummary
            {
                Code = w.Code,
                Name = w.Name,
                State = w.State,
                NearbyCities = w.NearbyCities,
            })
            .ToArray();
        return new DockWarehousesResponse { Warehouses = warehouses };
    }

    /// <summary>
    /// Trucks/loads at or inbound to the given warehouse on the given day, reusing the Arrivals
    /// Board. When <paramref name="warehouseCode"/> is unknown the board resolves to an honest empty
    /// list (the service degrades rather than throwing).
    /// </summary>
    public Task<LaredoArrivalsBoard> GetArrivalsAsync(
        string? warehouseCode, DateOnly? date, CancellationToken ct)
        => _arrivals.GetBoardAsync(date, NormalizeWarehouse(warehouseCode), ct);

    /// <summary>
    /// Eligible sibling suggestions for a chosen parent load, reusing the consolidation candidate
    /// service (corridor / timing / customer-allow factor chips, honest missing-dim caveats).
    /// </summary>
    public Task<ConsolidationCandidateResponse> GetCandidatesAsync(
        string parentLoadId, string? corridorCode, CancellationToken ct)
        => _candidates.GetCandidatesAsync(parentLoadId, NormalizeCorridor(corridorCode), ct);

    /// <summary>
    /// Combines a parent + siblings into a consolidation plan preview and records the internal audit
    /// (<c>AlvysWriteback = NotPerformed</c>). Returns the plan (click card + combined economics) and
    /// the audit record. Read-only against Alvys — the audit is the only state written, and it lives
    /// in the internal audit store, never Alvys.
    /// </summary>
    public async Task<DockCombineResponse> CombineAsync(
        DockCombineRequest request, string recordedBy, CancellationToken ct)
    {
        var plan = await BuildPlanAsync(request.ParentLoadId, request.SiblingLoadIds, request.CorridorCode, ct);

        var audit = _audits.Record(plan, recordedBy);

        // Notify the yard's recipients. Non-blocking by construction — NotifyCombineAsync never
        // throws, so a mail failure surfaces as a retry chip and never rolls back the combine/audit.
        var notification = await _notifications.NotifyCombineAsync(request.WarehouseCode, plan, ct);

        return new DockCombineResponse { Plan = plan, Audit = audit, Notification = notification };
    }

    /// <summary>
    /// Records a one-tap Undo of a just-committed combine. Rebuilds the same plan read-only for audit
    /// context and writes an <c>Undo</c> audit entry. Reverses nothing in Alvys — the combine wrote
    /// nothing there — it keeps the leadership trail honest about the retraction.
    /// </summary>
    public async Task<DockUndoResponse> UndoAsync(
        DockUndoRequest request, string recordedBy, CancellationToken ct)
    {
        var plan = await BuildPlanAsync(request.ParentLoadId, request.SiblingLoadIds, request.CorridorCode, ct);
        var audit = _audits.RecordUndo(plan, recordedBy);
        return new DockUndoResponse { Audit = audit };
    }

    /// <summary>
    /// Re-sends the combine notification for a plan (retry chip). Read-only against Alvys; rebuilds
    /// the plan and re-invokes the non-blocking notifier — records no new audit.
    /// </summary>
    public async Task<DockNotificationResult> RenotifyAsync(
        DockCombineRequest request, CancellationToken ct)
    {
        var plan = await BuildPlanAsync(request.ParentLoadId, request.SiblingLoadIds, request.CorridorCode, ct);
        return await _notifications.NotifyCombineAsync(request.WarehouseCode, plan, ct);
    }

    private Task<ConsolidationPlanResponse> BuildPlanAsync(
        string parentLoadId, List<string> siblingLoadIds, string? corridorCode, CancellationToken ct)
        => _plans.BuildAsync(
            new ConsolidationPlanRequest
            {
                ParentLoadId = parentLoadId,
                SiblingLoadIds = siblingLoadIds,
                CorridorCode = NormalizeCorridor(corridorCode),
            },
            ct);

    private string? NormalizeWarehouse(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Trim();

    private static string NormalizeCorridor(string? code) =>
        string.IsNullOrWhiteSpace(code) ? "LAREDO_TO_DALLAS" : code.Trim();
}
