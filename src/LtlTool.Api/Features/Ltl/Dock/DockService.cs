using LtlTool.Api.Features.Ltl.Arrivals;
using LtlTool.Api.Features.Ltl.Consolidation;
using LtlTool.Api.Features.Ltl.DispatchPlanner;
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
    DispatchPlannerService dispatchPlanner,
    IOptions<ConsolidationOptions> options)
{
    private readonly LaredoArrivalsService _arrivals = arrivals;
    private readonly ConsolidationCandidateService _candidates = candidates;
    private readonly ConsolidationPlanService _plans = plans;
    private readonly IConsolidationAuditStore _audits = audits;
    private readonly DockNotificationService _notifications = notifications;
    private readonly DispatchPlannerService _dispatchPlanner = dispatchPlanner;
    private readonly ConsolidationOptions _opts = options.Value;

    /// <summary>
    /// The configured yards a dock worker can pick. Honest projection of static config — never a
    /// place to declare a yard at runtime. Empty when no warehouses are configured.
    ///
    /// <para>
    /// When a yard config carries an <see cref="ConsolidationWarehouseOptions.AlvysLocationId"/> the
    /// card is enriched with live Alvys location metadata (type + physical address) via the read-only
    /// dispatch-planner service. Any yard whose location does not resolve (unconfigured id, or a
    /// degraded/429 read) degrades to its static name/state — enrichment never blocks the picker.
    /// </para>
    /// </summary>
    public async Task<DockWarehousesResponse> ListWarehousesAsync(CancellationToken ct)
    {
        var locationIds = _opts.Warehouses
            .Select(w => w.AlvysLocationId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList();

        var locations = locationIds.Count == 0
            ? (IReadOnlyDictionary<string, LocationView>)new Dictionary<string, LocationView>()
            : await _dispatchPlanner.GetLocationsAsync(locationIds, ct);

        var warehouses = _opts.Warehouses
            .Select(w =>
            {
                LocationView? loc = null;
                if (!string.IsNullOrWhiteSpace(w.AlvysLocationId))
                    locations.TryGetValue(w.AlvysLocationId!, out loc);
                return new WarehouseSummary
                {
                    Code = w.Code,
                    Name = w.Name,
                    State = w.State,
                    NearbyCities = w.NearbyCities,
                    LocationType = loc?.Type,
                    AddressLabel = loc?.AddressLabel,
                };
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

        // Phase 3 semantics: a plan with hard blockers (parent outside the corridor, a
        // Never-consolidate sibling, an unresolved load) must NOT combine. Fail closed BEFORE any
        // audit is recorded or notification sent — the UI surfaces these blockers at the review step,
        // and this guard is the last line even if the UI is bypassed. Warnings (e.g. a below-target
        // RPM) are not blockers and are allowed through.
        if (plan.Blockers.Count > 0)
            throw new ConsolidationPlanBlockedException(plan);

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

    /// <summary>
    /// Renders the combined BOL packet / dock manifest as a downloadable server-side PDF. Rebuilds the
    /// plan read-only (same path as the combine preview) and hands it to <see cref="BolPacketPdfBuilder"/>.
    /// Records nothing — no audit, no notification, no Alvys writeback. The one-tap "Download PDF"
    /// companion to the existing print-CSS view. Throws <see cref="ConsolidationPlanBlockedException"/>
    /// on a blocked plan so the UI never hands out a manifest for an illegal consolidation.
    /// </summary>
    public async Task<byte[]> BuildBolPacketPdfAsync(
        DockCombineRequest request, CancellationToken ct)
    {
        var plan = await BuildPlanAsync(request.ParentLoadId, request.SiblingLoadIds, request.CorridorCode, ct);
        if (plan.Blockers.Count > 0)
            throw new ConsolidationPlanBlockedException(plan);

        var warehouse = await ResolveWarehouseAsync(request.WarehouseCode, ct);
        return new BolPacketPdfBuilder().Build(plan, warehouse);
    }

    private async Task<WarehouseSummary?> ResolveWarehouseAsync(string? warehouseCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(warehouseCode))
            return null;

        var response = await ListWarehousesAsync(ct);
        return response.Warehouses.FirstOrDefault(
            w => string.Equals(w.Code, warehouseCode.Trim(), StringComparison.OrdinalIgnoreCase));
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
