using System.Text.Json;

namespace LtlTool.Api.Features.Integrations.Alvys.Writeback;

/// <summary>What the recorder decided to do with a request, for the controller to map to a response.</summary>
public enum AlvysRecordDisposition
{
    /// <summary>A new audit/outbox record was created.</summary>
    Created,

    /// <summary>
    /// An equivalent executable request with the same idempotency key already existed; no new
    /// executable record was created — the prior one is returned (its attempt count was bumped).
    /// </summary>
    DuplicateReplay,

    /// <summary>
    /// The idempotency key was reused with a <b>different</b> payload; nothing was recorded and the
    /// caller must resolve the conflict. Maps to HTTP 409.
    /// </summary>
    Conflict,
}

/// <summary>
/// The result of recording an operation: the gateway outcome, the persisted record (null only on a
/// conflict), how it was dispositioned for idempotency, and — on conflict — the id of the existing
/// record that the key already belongs to.
/// </summary>
public sealed class AlvysRecordResult
{
    public required AlvysOperationOutcome Outcome { get; init; }
    public required AlvysRecordDisposition Disposition { get; init; }
    public AlvysOperationRecord? Record { get; init; }
    public string? ConflictingRecordId { get; init; }
}

/// <summary>
/// Bridges the pure <see cref="IAlvysWriteGateway"/> (which builds/validates a payload but never
/// persists) and the durable <see cref="IAlvysOperationStore"/>. It turns each dry-run/execute request
/// into an auditable outbox record and enforces idempotency on the execute path:
///
/// <list type="bullet">
/// <item>Dry-run requests are always recorded as <see cref="AlvysOperationChannel.DryRun"/> audit rows.</item>
/// <item>Execute requests with an idempotency key de-duplicate: an equivalent repeat is a no-op replay,
/// a conflicting repeat (same key, different payload) returns a conflict and records nothing.</item>
/// <item>Execute requests without a key are always recorded (no de-duplication is possible).</item>
/// <item>When the gateway signals <see cref="AlvysOperationOutcome.SandboxExecutionEligible"/>,
/// <see cref="RecordExecuteAsync"/> dispatches the live sandbox call via <see cref="IAlvysWriteClient"/>
/// and updates the record with the result.</item>
/// </list>
///
/// Recording stores no secrets: no bearer tokens, no OAuth credentials, no Authorization headers.
/// </summary>
public interface IAlvysOperationRecorder
{
    AlvysRecordResult RecordDryRun(string ownerId, string operationCode, AlvysOperationRequest request);
    AlvysRecordResult RecordExecute(string ownerId, string operationCode, AlvysOperationRequest request);

    /// <summary>
    /// Async variant of <see cref="RecordExecute"/> that dispatches a live sandbox call when the
    /// gateway marks the outcome as <see cref="AlvysOperationOutcome.SandboxExecutionEligible"/>.
    /// </summary>
    Task<AlvysRecordResult> RecordExecuteAsync(
        string ownerId, string operationCode, AlvysOperationRequest request,
        CancellationToken ct = default);
}

/// <inheritdoc cref="IAlvysOperationRecorder"/>
public sealed class AlvysOperationRecorder(
    IAlvysWriteGateway gateway,
    IAlvysOperationStore store,
    TimeProvider clock,
    IAlvysWriteClient writeClient) : IAlvysOperationRecorder
{
    /// <summary>Upper bound on the stored payload preview so a row can never grow unbounded.</summary>
    private const int MaxPreviewLength = 4000;

    public AlvysRecordResult RecordDryRun(string ownerId, string operationCode, AlvysOperationRequest request)
    {
        var outcome = gateway.DryRun(operationCode, request);
        var record = Persist(ownerId, operationCode, request, outcome, AlvysOperationChannel.DryRun);
        return new AlvysRecordResult
        {
            Outcome = outcome,
            Disposition = AlvysRecordDisposition.Created,
            Record = record,
        };
    }

    public AlvysRecordResult RecordExecute(string ownerId, string operationCode, AlvysOperationRequest request)
    {
        var outcome = gateway.Execute(operationCode, request);
        var key = NormalizeKey(request.IdempotencyKey);
        var hash = ComputeExecuteHash(operationCode, request, outcome);

        if (key is not null)
        {
            var existing = store.FindExecutableByKey(ownerId, key);
            if (existing is not null)
            {
                if (!string.Equals(existing.PayloadHash, hash, StringComparison.Ordinal))
                {
                    return new AlvysRecordResult
                    {
                        Outcome = outcome,
                        Disposition = AlvysRecordDisposition.Conflict,
                        ConflictingRecordId = existing.Id,
                    };
                }

                existing.AttemptCount += 1;
                existing.UpdatedAt = clock.GetUtcNow();
                store.Update(existing);
                return new AlvysRecordResult
                {
                    Outcome = outcome,
                    Disposition = AlvysRecordDisposition.DuplicateReplay,
                    Record = existing,
                };
            }
        }

        var record = Persist(ownerId, operationCode, request, outcome, AlvysOperationChannel.Execute, key, hash);
        return new AlvysRecordResult
        {
            Outcome = outcome,
            Disposition = AlvysRecordDisposition.Created,
            Record = record,
        };
    }

    public async Task<AlvysRecordResult> RecordExecuteAsync(
        string ownerId, string operationCode, AlvysOperationRequest request,
        CancellationToken ct = default)
    {
        var outcome = gateway.Execute(operationCode, request);
        var key = NormalizeKey(request.IdempotencyKey);
        var hash = ComputeExecuteHash(operationCode, request, outcome);

        if (key is not null)
        {
            var existing = store.FindExecutableByKey(ownerId, key);
            if (existing is not null)
            {
                if (!string.Equals(existing.PayloadHash, hash, StringComparison.Ordinal))
                {
                    return new AlvysRecordResult
                    {
                        Outcome = outcome,
                        Disposition = AlvysRecordDisposition.Conflict,
                        ConflictingRecordId = existing.Id,
                    };
                }

                existing.AttemptCount += 1;
                existing.UpdatedAt = clock.GetUtcNow();

                // A prior attempt that never reached Alvys (failed write) may be re-sent under the
                // same key once the request is eligible again — idempotent retry after a transient
                // failure. A previously successful record is just replayed, never re-sent.
                if (existing.Status == AlvysOperationRecordStatus.Blocked
                    && outcome.SandboxExecutionEligible && outcome.Payload is not null)
                {
                    var replayedOutcome = await DispatchAndApplyAsync(operationCode, request, outcome, existing, ct);
                    return new AlvysRecordResult
                    {
                        Outcome = replayedOutcome,
                        Disposition = AlvysRecordDisposition.DuplicateReplay,
                        Record = existing,
                    };
                }

                store.Update(existing);
                return new AlvysRecordResult
                {
                    Outcome = outcome,
                    Disposition = AlvysRecordDisposition.DuplicateReplay,
                    Record = existing,
                };
            }
        }

        var record = Persist(ownerId, operationCode, request, outcome, AlvysOperationChannel.Execute, key, hash);

        // Dispatch the live sandbox call when the gateway signals eligibility.
        if (outcome.SandboxExecutionEligible && outcome.Payload is not null)
        {
            outcome = await DispatchAndApplyAsync(operationCode, request, outcome, record, ct);
        }

        return new AlvysRecordResult
        {
            Outcome = outcome,
            Disposition = AlvysRecordDisposition.Created,
            Record = record,
        };
    }

    /// <summary>
    /// Issues the live sandbox call, updates the persisted record's status/error, and returns an
    /// outcome that reflects the actual result (success → SandboxExecuted/Executed; failure →
    /// SandboxFailed so the controller surfaces a non-2xx to the caller).
    /// </summary>
    private async Task<AlvysOperationOutcome> DispatchAndApplyAsync(
        string operationCode, AlvysOperationRequest request, AlvysOperationOutcome outcome,
        AlvysOperationRecord record, CancellationToken ct)
    {
        var op = AlvysWriteOperationRegistry.Find(operationCode)!;
        var callResult = await writeClient.ExecuteAsync(op, request, outcome.Payload!, ct);

        record.UpdatedAt = clock.GetUtcNow();
        if (callResult.IsSuccess)
        {
            record.Status = AlvysOperationRecordStatus.Recorded;
            record.LastError = null;
            store.Update(record);
            return WithExecution(outcome, executed: true, AlvysOperationDisposition.SandboxExecuted,
                "Sandbox execution succeeded — sent to the Alvys sandbox.");
        }

        record.Status = AlvysOperationRecordStatus.Blocked;
        record.LastError = callResult.Error;
        store.Update(record);
        return WithExecution(outcome, executed: false, AlvysOperationDisposition.SandboxFailed,
            $"Sandbox execution failed: {callResult.Error ?? "the Alvys sandbox rejected the request"}.");
    }

    /// <summary>Clones an outcome overriding only the post-execution fields.</summary>
    private static AlvysOperationOutcome WithExecution(
        AlvysOperationOutcome o, bool executed, AlvysOperationDisposition disposition, string message) => new()
    {
        OperationCode = o.OperationCode,
        Title = o.Title,
        Mode = o.Mode,
        Disposition = disposition,
        Executed = executed,
        SandboxExecutionEligible = o.SandboxExecutionEligible,
        Message = message,
        Payload = o.Payload,
        Validation = o.Validation,
        Blockers = o.Blockers,
        RequiredToEnable = o.RequiredToEnable,
    };

    /// <summary>
    /// Canonical idempotency hash for an execute request. Includes the resource identity (every
    /// path/URL identifier) alongside the payload body, so the same key + same body targeting a
    /// different tender/trip/stop/carrier/load is never mistaken for a duplicate.
    /// </summary>
    private static string ComputeExecuteHash(
        string operationCode, AlvysOperationRequest request, AlvysOperationOutcome outcome) =>
        AlvysPayloadHasher.Hash(
            $"{operationCode}@{ResourceIdentity(operationCode, request)}", outcome.Payload?.Body);

    /// <summary>Canonical string of every path identifier an operation targets.</summary>
    private static string ResourceIdentity(string operationCode, AlvysOperationRequest request)
    {
        var op = AlvysWriteOperationRegistry.Find(operationCode);
        return op?.Kind switch
        {
            AlvysWriteOperationKind.CreateLoadNote => $"load:{request.LoadNumber}",
            AlvysWriteOperationKind.LoadUpdate => $"load:{request.LoadNumber}",
            AlvysWriteOperationKind.TenderAccept => $"tender:{request.TenderId}",
            AlvysWriteOperationKind.TripStopArrival => $"trip:{request.TripId}/stop:{request.StopId}",
            AlvysWriteOperationKind.TripStopDeparture => $"trip:{request.TripId}/stop:{request.StopId}",
            AlvysWriteOperationKind.TripAssign => $"trip:{request.TripId}",
            AlvysWriteOperationKind.TripDispatch => $"trip:{request.TripId}",
            AlvysWriteOperationKind.CarrierStatusUpdate => $"carrier:{request.CarrierId}",
            _ => string.Empty,
        };
    }

    private AlvysOperationRecord Persist(
        string ownerId,
        string operationCode,
        AlvysOperationRequest request,
        AlvysOperationOutcome outcome,
        AlvysOperationChannel channel,
        string? idempotencyKey = null,
        string? payloadHash = null)
    {
        var now = clock.GetUtcNow();
        var (resourceType, resourceId) = ResolveResource(operationCode, request);

        var record = new AlvysOperationRecord
        {
            Id = Guid.NewGuid().ToString("n"),
            OwnerId = ownerId,
            OperationCode = operationCode,
            Channel = channel,
            ResourceType = resourceType,
            ResourceId = resourceId,
            IdempotencyKey = idempotencyKey,
            PayloadHash = payloadHash ?? ComputeExecuteHash(operationCode, request, outcome),
            PayloadPreview = BuildPreview(outcome.Payload?.Body),
            Mode = outcome.Mode,
            Disposition = outcome.Disposition,
            Status = ToStatus(outcome.Disposition),
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            LastError = null,
            AttemptCount = 1,
            CorrelationId = Guid.NewGuid().ToString("n"),
            CreatedAt = now,
            UpdatedAt = now,
        };

        store.Add(record);
        return record;
    }

    private static AlvysOperationRecordStatus ToStatus(AlvysOperationDisposition disposition) => disposition switch
    {
        AlvysOperationDisposition.Blocked => AlvysOperationRecordStatus.Blocked,
        AlvysOperationDisposition.SandboxFailed => AlvysOperationRecordStatus.Blocked,
        AlvysOperationDisposition.Unsupported => AlvysOperationRecordStatus.Unsupported,
        _ => AlvysOperationRecordStatus.Recorded,
    };

    /// <summary>Maps an operation + request to the resource it targets, for history/filtering.</summary>
    private static (string? Type, string? Id) ResolveResource(string operationCode, AlvysOperationRequest request)
    {
        var op = AlvysWriteOperationRegistry.Find(operationCode);
        return op?.Kind switch
        {
            AlvysWriteOperationKind.CreateLoadNote => ("load", request.LoadNumber),
            AlvysWriteOperationKind.LoadUpdate => ("load", request.LoadNumber),
            AlvysWriteOperationKind.TenderAccept => ("tender", request.TenderId),
            AlvysWriteOperationKind.TripStopArrival => ("trip", request.TripId),
            AlvysWriteOperationKind.TripStopDeparture => ("trip", request.TripId),
            AlvysWriteOperationKind.TripAssign => ("trip", request.TripId),
            AlvysWriteOperationKind.TripDispatch => ("trip", request.TripId),
            AlvysWriteOperationKind.CarrierStatusUpdate => ("carrier", request.CarrierId),
            _ => (null, null),
        };
    }

    private static string? BuildPreview(IReadOnlyDictionary<string, object?>? body)
    {
        if (body is null || body.Count == 0) return null;
        var json = JsonSerializer.Serialize(body);
        return json.Length <= MaxPreviewLength ? json : json[..MaxPreviewLength];
    }

    private static string? NormalizeKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : key.Trim();
}
