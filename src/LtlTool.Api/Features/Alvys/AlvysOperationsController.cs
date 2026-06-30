using System.Security.Claims;
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
    IAlvysOperationRecorder recorder,
    IAlvysOperationStore operationStore,
    IAlvysClient alvys,
    IAlvysSyncTracker syncTracker,
    TimeProvider clock) : ControllerBase
{
    /// <summary>Header carrying the idempotency key for the execute path (also honoured in the body).</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>Default and maximum number of history rows returned for the current owner.</summary>
    private const int DefaultHistoryLimit = 50;
    private const int MaxHistoryLimit = 200;

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
    /// Builds and validates the payload for an operation, records the dry-run as an auditable outbox
    /// entry, and returns the preview without ever sending anything to Alvys. Returns 404 when the
    /// operation code is unknown.
    /// </summary>
    [HttpPost("{operation}/dry-run")]
    [ProducesResponseType(typeof(AlvysOperationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AlvysOperationResponse> DryRun(
        string operation, [FromBody] AlvysOperationRequest request)
    {
        if (AlvysWriteOperationRegistry.Find(operation) is null) return NotFound();

        var result = recorder.RecordDryRun(CurrentUser(), operation, request);
        return Ok(ToResponse(result));
    }

    /// <summary>
    /// Attempts an operation. Honours the configured writeback mode; in this phase no live Alvys
    /// mutation is performed (every operation resolves to audit-only, simulated or unsupported). The
    /// attempt is recorded as an auditable outbox entry and de-duplicated by idempotency key.
    /// Returns 404 for an unknown operation, 422 when validation blocks the request, and 409 when an
    /// idempotency key is reused with a conflicting payload.
    /// </summary>
    [HttpPost("{operation}/execute")]
    [ProducesResponseType(typeof(AlvysOperationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AlvysOperationResponse), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(AlvysOperationConflict), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AlvysOperationResponse> Execute(
        string operation, [FromBody] AlvysOperationRequest request)
    {
        if (AlvysWriteOperationRegistry.Find(operation) is null) return NotFound();

        // Header takes precedence over the body so callers can supply the key the RESTful way.
        if (Request.Headers.TryGetValue(IdempotencyKeyHeader, out var header)
            && !string.IsNullOrWhiteSpace(header))
        {
            request.IdempotencyKey = header.ToString();
        }

        var result = recorder.RecordExecute(CurrentUser(), operation, request);

        if (result.Disposition == AlvysRecordDisposition.Conflict)
        {
            return Conflict(new AlvysOperationConflict
            {
                Message =
                    "This idempotency key was already used for a different payload. Reuse the original " +
                    "payload or choose a new idempotency key.",
                IdempotencyKey = request.IdempotencyKey!.Trim(),
                ExistingRecordId = result.ConflictingRecordId!,
            });
        }

        var response = ToResponse(result);
        return result.Outcome.Disposition == AlvysOperationDisposition.Blocked
            ? UnprocessableEntity(response)
            : Ok(response);
    }

    /// <summary>The current owner's operation history (audit/outbox), newest first.</summary>
    [HttpGet("history")]
    [ProducesResponseType(typeof(IReadOnlyList<AlvysOperationRecordView>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<AlvysOperationRecordView>> History([FromQuery] int? limit)
    {
        var take = Math.Clamp(limit ?? DefaultHistoryLimit, 1, MaxHistoryLimit);
        var records = operationStore.ListForOwner(CurrentUser(), take)
            .Select(AlvysOperationRecordView.From)
            .ToArray();
        return Ok(records);
    }

    /// <summary>A single operation history record owned by the current dispatcher. 404 when not found.</summary>
    [HttpGet("history/{id}")]
    [ProducesResponseType(typeof(AlvysOperationRecordView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AlvysOperationRecordView> HistoryItem(string id)
    {
        var record = operationStore.Get(CurrentUser(), id);
        return record is null ? NotFound() : Ok(AlvysOperationRecordView.From(record));
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

    private static AlvysOperationResponse ToResponse(AlvysRecordResult result) => new()
    {
        Outcome = result.Outcome,
        Record = result.Record is null ? null : AlvysOperationRecordView.From(result.Record),
        Replayed = result.Disposition == AlvysRecordDisposition.DuplicateReplay,
    };

    private string CurrentUser() =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.Email)
        ?? User.Identity?.Name
        ?? "unknown";
}
