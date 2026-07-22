using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LtlTool.Api.Features.Ltl.DispatchPlanner;

/// <summary>
/// Read-only dispatch-planner surface (Alvys Public API): exposes the preferred driver/truck/trailer
/// pairing so the Dock review and Assignments UIs can show "preferred" chips next to the load's
/// actual equipment. Never writes to Alvys; degrades to an unresolved view when Alvys returns nothing
/// or rate-limits.
/// </summary>
[ApiController]
[Route("api/ltl/dispatch-planner")]
[Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
[Produces("application/json")]
public sealed class DispatchPlannerController(DispatchPlannerService planner) : ControllerBase
{
    private readonly DispatchPlannerService _planner = planner;

    /// <summary>
    /// The preferred pairing for any combination of driver/truck/trailer id. All three query params
    /// are optional but at least one is expected; a call with none returns an honest unresolved view
    /// (200, <c>resolved=false</c>) rather than an error, so the caller can render "—" uniformly.
    /// </summary>
    [HttpGet("preferred-pairing")]
    [ProducesResponseType(typeof(DispatchPreferenceView), StatusCodes.Status200OK)]
    public async Task<ActionResult<DispatchPreferenceView>> GetPreferredPairing(
        [FromQuery] string? driverId,
        [FromQuery] string? truckId,
        [FromQuery] string? trailerId,
        CancellationToken ct)
        => Ok(await _planner.GetPreferredPairingAsync(driverId, truckId, trailerId, ct));
}
