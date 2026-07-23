using System.Security.Claims;
using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LtlTool.Api.Features.Ltl.DispatchAssist;

/// <summary>
/// Dispatch Assist: "inform and assemble the right driver and truck". Read-only against Alvys.
///
/// <list type="bullet">
///   <item><c>GET  api/ltl/dispatch/recommendations</c> — ranked, explainable driver+truck+trailer
///   candidates for a load (by <c>loadId</c>) or an ad-hoc origin/destination.</item>
///   <item><c>POST api/ltl/dispatch/assemble</c> — records the dispatcher's chosen assembly
///   <b>app-side only</b> (<c>AlvysWriteback = NotPerformed</c>), invokes the Alvys write hook
///   (a no-op until a separate workstream wires the real writer), and fires the notify step.</item>
///   <item><c>GET  api/ltl/dispatch/assemblies</c> — recent app-side assembly records.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/ltl/dispatch")]
[Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
[Produces("application/json")]
public sealed class DispatchAssistController(
    DispatchAssistService dispatch,
    DispatchAssemblyNotifier notifier,
    IDispatchAssemblyWriteback writeback,
    IDispatchAssemblyStore store,
    TimeProvider clock) : ControllerBase
{
    /// <summary>
    /// Ranked candidates for a load (<paramref name="loadId"/>) or an ad-hoc lane. 404 only when a
    /// <paramref name="loadId"/> was supplied but not resolvable in Alvys. <paramref name="top"/>
    /// caps the rows.
    /// </summary>
    [HttpGet("recommendations")]
    [ProducesResponseType(typeof(DispatchRecommendationsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DispatchRecommendationsResponse>> Recommendations(
        [FromQuery] string? loadId,
        [FromQuery] string? originCity,
        [FromQuery] string? originState,
        [FromQuery] string? destinationCity,
        [FromQuery] string? destinationState,
        [FromQuery] int top,
        CancellationToken ct)
    {
        var result = await dispatch.RecommendAsync(
            loadId, originCity, originState, destinationCity, destinationState, top, ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Records a chosen driver+truck+trailer assembly app-side and fires the notify step. Never
    /// writes to Alvys directly — the write hook is a no-op until a separate workstream wires the
    /// gated Alvys writer. Returns the assembly record (with the notify outcome + override banner).
    /// </summary>
    [HttpPost("assemble")]
    [ProducesResponseType(typeof(DispatchAssembly), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DispatchAssembly>> Assemble(
        [FromBody] DispatchAssembleRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DriverId) &&
            string.IsNullOrWhiteSpace(request.TruckId) &&
            string.IsNullOrWhiteSpace(request.TrailerId))
            return BadRequest("At least one of driverId/truckId/trailerId is required.");

        var intended = await dispatch.ResolveNotifyRecipientsAsync(request, ct);

        var loadLabel = request.LoadNumber ?? request.LoadId ?? "(no load)";
        var subject = $"Dispatch assembled for load {loadLabel}";
        var body =
            $"A driver/truck/trailer has been assembled for load {loadLabel}.\n" +
            $"Driver: {request.DriverId ?? "—"}\nTruck: {request.TruckId ?? "—"}\n" +
            $"Trailer: {request.TrailerId ?? "—"}\n" +
            (request.Reasons.Count > 0 ? $"Why: {string.Join("; ", request.Reasons)}\n" : "") +
            "Recorded in the LTL tool (internal). This is not an Alvys booking.";

        var notify = await notifier.NotifyAsync(intended, subject, body, ct);

        var assembly = new DispatchAssembly
        {
            Id = $"asm-{clock.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString()[..4]}",
            RecordedAt = clock.GetUtcNow(),
            RecordedBy = CurrentUser(),
            LoadId = request.LoadId,
            LoadNumber = request.LoadNumber,
            DriverId = request.DriverId,
            TruckId = request.TruckId,
            TrailerId = request.TrailerId,
            Score = request.Score,
            Reasons = request.Reasons,
            Notify = notify,
        };

        // Hand to the Alvys write seam. The default no-op keeps this read-only; a future gated writer
        // would set a different status without changing this controller.
        var pushed = await writeback.PushAsync(assembly, ct);
        var recorded = store.Add(assembly with { AlvysWriteback = pushed.Status });

        return CreatedAtAction(nameof(Assemblies), new { loadId = recorded.LoadId }, recorded);
    }

    /// <summary>Recent app-side assembly records, optionally filtered to one load.</summary>
    [HttpGet("assemblies")]
    [ProducesResponseType(typeof(IReadOnlyList<DispatchAssembly>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<DispatchAssembly>> Assemblies(
        [FromQuery] string? loadId, [FromQuery] int max = 100)
        => Ok(string.IsNullOrWhiteSpace(loadId) ? store.Recent(max) : store.ForLoad(loadId));

    private string CurrentUser() =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.Email)
        ?? User.Identity?.Name
        ?? "unknown";
}
