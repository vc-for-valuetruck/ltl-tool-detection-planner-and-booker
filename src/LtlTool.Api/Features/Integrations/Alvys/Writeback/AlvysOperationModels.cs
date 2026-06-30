namespace LtlTool.Api.Features.Integrations.Alvys.Writeback;

/// <summary>
/// The result of a live sandbox HTTP call made by <see cref="IAlvysWriteClient"/>. Captures
/// only the fields needed for the outbox record — no auth material, no secret headers.
/// </summary>
public sealed class AlvysWriteCallResult
{
    public required bool IsSuccess { get; init; }
    public required int StatusCode { get; init; }

    /// <summary>ETag returned by the response (null when the operation doesn't produce one).</summary>
    public string? ETag { get; init; }

    /// <summary>Response body JSON (bounded; never includes auth material).</summary>
    public string? Body { get; init; }

    /// <summary>Error detail when <see cref="IsSuccess"/> is false.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Inputs for a write-oriented Alvys operation. A single shape covers every operation; only the
/// fields relevant to the requested operation are read, and the gateway validates that the required
/// ones are present before building a payload.
/// </summary>
public sealed class AlvysOperationRequest
{
    /// <summary>Target load number (create-load-note, load-update).</summary>
    public string? LoadNumber { get; set; }

    /// <summary>Target tender id (tender-accept).</summary>
    public string? TenderId { get; set; }

    /// <summary>Target trip id (trip-stop arrival/departure, trip-assign, trip-dispatch).</summary>
    public string? TripId { get; set; }

    /// <summary>Target stop id (trip-stop arrival/departure).</summary>
    public string? StopId { get; set; }

    /// <summary>Target carrier id (carrier-status-update, trip-assign).</summary>
    public string? CarrierId { get; set; }

    /// <summary>Driver id for trip-assign.</summary>
    public string? DriverId { get; set; }

    /// <summary>Truck id for trip-assign.</summary>
    public string? TruckId { get; set; }

    /// <summary>Trailer id for trip-assign.</summary>
    public string? TrailerId { get; set; }

    /// <summary>Status value for carrier-status-update (e.g. <c>Active</c>, <c>Inactive</c>).</summary>
    public string? Status { get; set; }

    /// <summary>Note body (create-load-note).</summary>
    public string? NoteText { get; set; }

    /// <summary>
    /// Note classification (create-load-note). Must be one of: <c>System</c>, <c>General</c>,
    /// <c>Assignment</c>, <c>Safety</c>. Defaults to <c>General</c> when omitted.
    /// </summary>
    public string? NoteType { get; set; }

    /// <summary>
    /// Stop-to-company links for tender-accept. Each entry maps a tender stop id to the Alvys
    /// company id that should be linked to that stop.
    /// </summary>
    public List<TenderStopCompanyLink>? StopCompanyLinks { get; set; }

    /// <summary>Optional fleet id to assign on tender-accept.</summary>
    public string? FleetId { get; set; }

    /// <summary>Arrival timestamp (trip-stop-arrival).</summary>
    public DateTimeOffset? ArrivedAt { get; set; }

    /// <summary>Departure timestamp (trip-stop-departure).</summary>
    public DateTimeOffset? DepartedAt { get; set; }

    /// <summary>Optimistic-concurrency token for operations that mutate an existing record.</summary>
    public string? Etag { get; set; }

    /// <summary>Scoped field updates (load-update). Keys are field names, values the new values.</summary>
    public Dictionary<string, string?>? Fields { get; set; }

    /// <summary>Optional dispatcher justification captured on the audit trail.</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Optional idempotency key for the execute path. A repeat with the same key and an equivalent
    /// payload is a no-op replay; a repeat with the same key and a different payload is a conflict.
    /// May also be supplied via the <c>Idempotency-Key</c> request header.
    /// </summary>
    public string? IdempotencyKey { get; set; }
}

/// <summary>What ultimately happened (or would happen) for an operation request.</summary>
public enum AlvysOperationDisposition
{
    /// <summary>Validation failed; no payload is eligible and nothing was sent.</summary>
    Blocked,

    /// <summary>
    /// Writeback is disabled — the payload is recorded for audit only and is never sent to Alvys.
    /// </summary>
    AuditOnly,

    /// <summary>
    /// Simulation/dry-run — the payload was built and validated for preview, but not sent.
    /// </summary>
    Simulated,

    /// <summary>
    /// The operation is not yet supported for live execution (no documented mutating endpoint).
    /// Nothing was sent; the response documents what is required to enable it.
    /// </summary>
    Unsupported,

    /// <summary>
    /// Sandbox writeback is enabled and the operation/payload is eligible to execute against the
    /// Alvys sandbox. (Reached only when an operation becomes <see cref="AlvysLiveSupport.Supported"/>
    /// and sandbox configuration is ready.)
    /// </summary>
    SandboxExecuted,

    /// <summary>
    /// A live sandbox execution was attempted but Alvys rejected it (non-2xx response or transport
    /// error). Nothing durable changed upstream; the failure is surfaced to the caller.
    /// </summary>
    SandboxFailed,
}

/// <summary>A single validation finding on an operation request.</summary>
public sealed class AlvysOperationIssue
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// The dry-run preview of the request that <i>would</i> be sent to Alvys. The body is the concrete
/// JSON payload; the target is described in human terms rather than a fabricated URL, because the
/// upstream route/verb is not documented in this phase.
/// </summary>
public sealed class AlvysOperationPayload
{
    public required string OperationCode { get; init; }

    /// <summary>Human description of the upstream target (no guessed path is emitted).</summary>
    public required string TargetDescription { get; init; }

    /// <summary>Whether an ETag/concurrency token is required and was supplied.</summary>
    public bool RequiresEtag { get; init; }
    public bool EtagSupplied { get; init; }

    /// <summary>The concrete request body that would be sent, for dispatcher preview.</summary>
    public required IReadOnlyDictionary<string, object?> Body { get; init; }
}

/// <summary>
/// The outcome of a dry-run or execute request. This is the API/UI contract that makes the safety
/// posture explicit: it always says which <see cref="Mode"/> is active, what the
/// <see cref="Disposition"/> was, whether anything was <see cref="Executed"/> (always false in this
/// phase), the payload that would be sent, any validation issues, the blockers preventing live
/// execution, and the documentation needed to enable it.
/// </summary>
public sealed class AlvysOperationOutcome
{
    public required string OperationCode { get; init; }
    public required string Title { get; init; }
    public required AlvysWritebackMode Mode { get; init; }
    public required AlvysOperationDisposition Disposition { get; init; }

    /// <summary>Always <c>false</c> in this phase — no live Alvys mutation is performed.</summary>
    public bool Executed { get; init; }

    /// <summary>
    /// True when the operation is Supported and the sandbox is fully configured; the recorder
    /// should dispatch a live call via <see cref="IAlvysWriteClient"/> and update this outcome.
    /// Always false for dry-run and for Unsupported/blocked operations.
    /// </summary>
    public bool SandboxExecutionEligible { get; init; }

    /// <summary>A one-line, dispatcher-facing summary of the disposition.</summary>
    public required string Message { get; init; }

    /// <summary>The dry-run payload preview (null only when validation blocked construction).</summary>
    public AlvysOperationPayload? Payload { get; init; }

    public IReadOnlyList<AlvysOperationIssue> Validation { get; init; } = [];

    /// <summary>Reasons live sandbox execution did not (or could not) happen.</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];

    /// <summary>What is required to enable live sandbox execution, when unsupported.</summary>
    public string? RequiredToEnable { get; init; }
}

/// <summary>
/// Maps a tender stop to the Alvys company that should be linked to it on acceptance.
/// Required by the <c>tender-accept</c> operation (<c>POST /tenders/{tenderId}/accept</c>).
/// </summary>
public sealed class TenderStopCompanyLink
{
    public required string StopId { get; set; }
    public required string CompanyId { get; set; }
}
