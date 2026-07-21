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
public sealed class DockController(DockService dock) : ControllerBase
{
    private readonly DockService _dock = dock;

    /// <summary>
    /// The configured yards a dock worker can pick (Laredo / Dallas in the pilot). Honest projection
    /// of static config; empty when no warehouses are configured.
    /// </summary>
    [HttpGet("warehouses")]
    [ProducesResponseType(typeof(DockWarehousesResponse), StatusCodes.Status200OK)]
    public ActionResult<DockWarehousesResponse> GetWarehouses() => Ok(_dock.ListWarehouses());

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
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DockCombineResponse>> Combine(
        [FromBody] DockCombineRequest request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await _dock.CombineAsync(request, CurrentUser(), ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private string CurrentUser() =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.Email)
        ?? User.Identity?.Name
        ?? "unknown";
}
