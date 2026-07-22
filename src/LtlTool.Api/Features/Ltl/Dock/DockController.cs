using System.Security.Claims;
using LtlTool.Api.Features.Ltl.Arrivals;
using LtlTool.Api.Features.Ltl.Consolidation;
using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LtlTool.Api.Features.Ltl.Dock;

/// <summary>
/// Dock mode API (Phase 2.5): the dock-worker-facing "easy match loads" flow. Every endpoint is a
/// thin pass-through to <see cref="DockService"/>, which itself composes the already-tested arrivals
/// and consolidation services. Read-only against Alvys — the one state-changing action
/// (<see cref="Combine"/>) records an internal audit only (<c>AlvysWriteback = NotPerformed</c>).
/// </summary>
[ApiController]
[Route("api/ltl/dock")]
[Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
[Produces("application/json")]
public sealed class DockController(DockService dock, ILogger<DockController> logger) : ControllerBase
{
    private readonly DockService _dock = dock;
    private readonly ILogger<DockController> _logger = logger;

    /// <summary>
    /// The configured yards a dock worker can pick (Laredo / Dallas in the pilot). Honest projection
    /// of static config; empty when no warehouses are configured.
    /// </summary>
    [HttpGet("warehouses")]
    [ProducesResponseType(typeof(DockWarehousesResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DockWarehousesResponse>> GetWarehouses(CancellationToken ct) =>
        Ok(await _dock.ListWarehousesAsync(ct));

    /// <summary>
    /// Trucks/loads at or inbound to the given warehouse on the given day, reusing the Arrivals Board.
    /// Unknown warehouse degrades to an honest empty board rather than throwing.
    /// </summary>
    [HttpGet("arrivals")]
    [ProducesResponseType(typeof(LaredoArrivalsBoard), StatusCodes.Status200OK)]
    public Task<LaredoArrivalsBoard> GetArrivals(
        [FromQuery] string? warehouse,
        [FromQuery] DateOnly? date,
        CancellationToken ct)
        => _dock.GetArrivalsAsync(warehouse, date, ct);

    /// <summary>
    /// Eligible sibling suggestions for a chosen parent load, via the consolidation candidate service.
    /// 400 on missing parentLoadId or unknown corridor.
    /// </summary>
    [HttpGet("candidates")]
    [ProducesResponseType(typeof(ConsolidationCandidateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ConsolidationCandidateResponse>> GetCandidates(
        [FromQuery] string parentLoadId,
        [FromQuery] string? corridor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(parentLoadId))
        {
            return BadRequest(new { error = "parentLoadId query parameter is required." });
        }

        try
        {
            return Ok(await _dock.GetCandidatesAsync(parentLoadId, corridor, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Combines a parent + siblings into a consolidation plan preview and records the internal audit.
    /// Returns the plan (click card + combined economics) and the audit record. Read-only against
    /// Alvys — the audit is the only state written. 400 on missing parent/siblings/unknown corridor.
    /// </summary>
    [HttpPost("combine")]
    [ProducesResponseType(typeof(DockCombineResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ConsolidationPlanResponse), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DockCombineResponse>> Combine(
        [FromBody] DockCombineRequest request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await _dock.CombineAsync(request, CurrentUser(), ct));
        }
        catch (ConsolidationPlanBlockedException ex)
        {
            // Blocked plan: record nothing, return the plan so the UI can surface its blockers.
            return UnprocessableEntity(ex.Plan);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Records a one-tap Undo of a just-committed combine. Writes a retraction audit entry
    /// (<c>Action = Undo</c>, <c>AlvysWriteback = NotPerformed</c>). Reverses nothing in Alvys — the
    /// combine wrote nothing there. 400 on missing parent/siblings/unknown corridor.
    /// </summary>
    [HttpPost("undo")]
    [ProducesResponseType(typeof(DockUndoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DockUndoResponse>> Undo(
        [FromBody] DockUndoRequest request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await _dock.UndoAsync(request, CurrentUser(), ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Re-sends the combine notification for a plan (retry chip). Read-only against Alvys; records no
    /// new audit. Returns the honest notification outcome. 400 on missing parent/siblings/unknown corridor.
    /// </summary>
    [HttpPost("notify")]
    [ProducesResponseType(typeof(DockNotificationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DockNotificationResult>> Renotify(
        [FromBody] DockCombineRequest request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await _dock.RenotifyAsync(request, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Downloads the combined BOL packet / dock manifest as a server-side PDF (the "Download PDF"
    /// companion to the print-CSS view). Rebuilds the plan read-only and renders it — records no
    /// audit, sends no notification, writes nothing to Alvys. 422 with the plan on a blocked plan,
    /// 400 on missing parent/siblings/unknown corridor.
    /// </summary>
    [HttpPost("bol-packet.pdf")]
    [Produces("application/pdf")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ConsolidationPlanResponse), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DownloadBolPacket(
        [FromBody] DockCombineRequest request,
        CancellationToken ct)
    {
        try
        {
            var pdf = await _dock.BuildBolPacketPdfAsync(request, ct);
            var name = string.IsNullOrWhiteSpace(request.ParentLoadId)
                ? "bol-packet"
                : $"bol-packet-{request.ParentLoadId}";
            return File(pdf, "application/pdf", $"{name}.pdf");
        }
        catch (ConsolidationPlanBlockedException ex)
        {
            return UnprocessableEntity(ex.Plan);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Effectiveness metrics (Phase 2.5, consistent with the #140 instrumentation): time-to-combine
    /// (parent tap → docs rendered) and tap count per combine. Fire-and-forget, status-only — no plan
    /// body, no PII — so leadership can see how minimal-tap the dock flow actually is in the field.
    /// </summary>
    [HttpPost("metrics/combine")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult RecordCombineMetric([FromBody] DockCombineMetricRequest? request)
    {
        _logger.LogInformation(
            "Dock metric: combine warehouse={Warehouse} siblings={SiblingCount} taps={TapCount} timeToCombineMs={TimeToCombineMs}",
            string.IsNullOrWhiteSpace(request?.WarehouseCode) ? "unknown" : request!.WarehouseCode,
            request?.SiblingCount ?? 0,
            request?.TapCount ?? 0,
            request?.TimeToCombineMs ?? 0);
        return NoContent();
    }

    private string CurrentUser() =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.Email)
        ?? User.Identity?.Name
        ?? "unknown";
}
