using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LtlTool.Api.Features.Ltl.Consolidation;

/// <summary>
/// Consolidation planner API for the Phase 1 Laredo → Dallas pilot.
/// Read-only against Alvys — every response is derived from live loads and static config;
/// nothing writes upstream. See ROADMAP § 2.5 and docs/PILOT_LAREDO_DALLAS.md for scope.
/// </summary>
[ApiController]
[Route("api/ltl/consolidation")]
[Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
[Produces("application/json")]
public sealed class ConsolidationController(ConsolidationCandidateService candidates)
    : ControllerBase
{
    /// <summary>
    /// Returns ranked consolidation candidates for the given seed load along the specified
    /// corridor. Missing seed → 200 with a null Seed and empty candidate list.
    /// Unknown corridor → 400.
    /// </summary>
    [HttpGet("candidates")]
    [ProducesResponseType(typeof(ConsolidationCandidateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ConsolidationCandidateResponse>> GetCandidates(
        [FromQuery] string loadId,
        [FromQuery] string? corridor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(loadId))
        {
            return BadRequest(new { error = "loadId query parameter is required." });
        }

        var corridorCode = string.IsNullOrWhiteSpace(corridor) ? "LAREDO_TO_DALLAS" : corridor;

        try
        {
            var response = await candidates.GetCandidatesAsync(loadId, corridorCode, ct);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
