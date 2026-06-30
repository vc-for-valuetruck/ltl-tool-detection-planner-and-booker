using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Integrations.Alvys.Writeback;
using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LtlTool.Api.Features.Alvys;

/// <summary>
/// Sandbox-gated Alvys <b>operation</b> boundary for the dispatcher SPA. Distinct from the
/// read-only <see cref="AlvysSearchController"/>: this controller exposes the writeback readiness
/// status, the operation catalogue, and dry-run/execute for write-oriented operations.
///
/// <para>
/// In this phase nothing is ever written to Alvys. Every write operation is dry-run/simulation or
/// audit-only, and the responses state that explicitly (mode, disposition, blockers, and the
/// documentation required to enable live sandbox execution). The one read this controller performs
/// is the opt-in readiness <see cref="Probe"/>, which confirms endpoint availability and records a
/// "last successful read" time.
/// </para>
/// </summary>
[ApiController]
[Route("api/alvys/ops")]
[Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
[Produces("application/json")]
public sealed class AlvysOperationsController(
    IAlvysReadinessService readiness,
    IAlvysWriteGateway gateway,
    IAlvysClient alvys,
    IAlvysSyncTracker syncTracker,
    TimeProvider clock) : ControllerBase
{
    /// <summary>The Alvys sandbox/writeback readiness snapshot (no secrets).</summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(AlvysReadinessStatus), StatusCodes.Status200OK)]
    public ActionResult<AlvysReadinessStatus> Status() => Ok(readiness.GetStatus());

    /// <summary>The catalogue of write-oriented operations and their live-execution support.</summary>
    [HttpGet("operations")]
    [ProducesResponseType(typeof(IReadOnlyList<AlvysWriteOperationDescriptor>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<AlvysWriteOperationDescriptor>> Operations()
        => Ok(AlvysWriteOperationRegistry.All);

    /// <summary>
    /// Builds and validates the payload for an operation and returns the preview without ever
    /// sending it to Alvys. Returns 404 when the operation code is unknown.
    /// </summary>
    [HttpPost("{operation}/dry-run")]
    [ProducesResponseType(typeof(AlvysOperationOutcome), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AlvysOperationOutcome> DryRun(
        string operation, [FromBody] AlvysOperationRequest request)
    {
        if (AlvysWriteOperationRegistry.Find(operation) is null) return NotFound();
        return Ok(gateway.DryRun(operation, request));
    }

    /// <summary>
    /// Attempts an operation. Honours the configured writeback mode; in this phase no live Alvys
    /// mutation is performed (every operation resolves to audit-only, simulated or unsupported).
    /// Returns 404 for an unknown operation and 422 when validation blocks the request.
    /// </summary>
    [HttpPost("{operation}/execute")]
    [ProducesResponseType(typeof(AlvysOperationOutcome), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AlvysOperationOutcome), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AlvysOperationOutcome> Execute(
        string operation, [FromBody] AlvysOperationRequest request)
    {
        if (AlvysWriteOperationRegistry.Find(operation) is null) return NotFound();

        var outcome = gateway.Execute(operation, request);
        return outcome.Disposition == AlvysOperationDisposition.Blocked
            ? UnprocessableEntity(outcome)
            : Ok(outcome);
    }

    /// <summary>
    /// Opt-in read-sync readiness probe: issues a single bounded read against Alvys to confirm
    /// endpoint availability and records the result (last successful read time). This is a read,
    /// never a mutation. Returns the refreshed readiness status.
    /// </summary>
    [HttpPost("sync/probe")]
    [ProducesResponseType(typeof(AlvysReadinessStatus), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlvysReadinessStatus>> Probe(CancellationToken ct)
    {
        try
        {
            var users = await alvys.SearchUsersAsync(new UserSearchRequest { Page = 0, PageSize = 1 }, ct);
            syncTracker.Record(
                AlvysSyncOutcome.Success, clock.GetUtcNow(),
                $"Reached users/search (total visible: {users.Total}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            syncTracker.Record(AlvysSyncOutcome.Failure, clock.GetUtcNow(), ex.GetType().Name);
        }

        return Ok(readiness.GetStatus());
    }
}
