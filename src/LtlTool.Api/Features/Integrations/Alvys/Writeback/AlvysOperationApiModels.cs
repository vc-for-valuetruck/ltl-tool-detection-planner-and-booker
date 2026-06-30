namespace LtlTool.Api.Features.Integrations.Alvys.Writeback;

/// <summary>
/// Owner-safe API/UI projection of a persisted <see cref="AlvysOperationRecord"/>. It exposes the
/// auditable facts of an operation attempt (no secrets, no owner of another dispatcher) so the SPA can
/// render recent attempts, blocked reasons and dry-run previews as a history.
/// </summary>
public sealed class AlvysOperationRecordView
{
    public required string Id { get; init; }
    public required string OperationCode { get; init; }
    public required AlvysOperationChannel Channel { get; init; }
    public string? ResourceType { get; init; }
    public string? ResourceId { get; init; }
    public string? IdempotencyKey { get; init; }
    public required string PayloadHash { get; init; }
    public string? PayloadPreview { get; init; }
    public required AlvysWritebackMode Mode { get; init; }
    public required AlvysOperationDisposition Disposition { get; init; }
    public required AlvysOperationRecordStatus Status { get; init; }
    public string? Reason { get; init; }
    public string? LastError { get; init; }
    public required int AttemptCount { get; init; }
    public required string CorrelationId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    public static AlvysOperationRecordView From(AlvysOperationRecord r) => new()
    {
        Id = r.Id,
        OperationCode = r.OperationCode,
        Channel = r.Channel,
        ResourceType = r.ResourceType,
        ResourceId = r.ResourceId,
        IdempotencyKey = r.IdempotencyKey,
        PayloadHash = r.PayloadHash,
        PayloadPreview = r.PayloadPreview,
        Mode = r.Mode,
        Disposition = r.Disposition,
        Status = r.Status,
        Reason = r.Reason,
        LastError = r.LastError,
        AttemptCount = r.AttemptCount,
        CorrelationId = r.CorrelationId,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
    };
}

/// <summary>
/// The dry-run/execute response: the gateway <see cref="AlvysOperationOutcome"/> plus the auditable
/// <see cref="Record"/> it produced and whether this was an idempotent <see cref="Replayed"/> request.
/// </summary>
public sealed class AlvysOperationResponse
{
    public required AlvysOperationOutcome Outcome { get; init; }

    /// <summary>The persisted audit/outbox record for this request (null only on a conflict).</summary>
    public AlvysOperationRecordView? Record { get; init; }

    /// <summary>True when an existing executable record was replayed instead of creating a new one.</summary>
    public bool Replayed { get; init; }
}

/// <summary>
/// The 409 body returned when an idempotency key is reused with a different payload. Nothing was
/// recorded; the caller must either reuse the original payload or choose a new key.
/// </summary>
public sealed class AlvysOperationConflict
{
    public required string Message { get; init; }
    public required string IdempotencyKey { get; init; }

    /// <summary>The id of the existing record the key already belongs to.</summary>
    public required string ExistingRecordId { get; init; }
}
