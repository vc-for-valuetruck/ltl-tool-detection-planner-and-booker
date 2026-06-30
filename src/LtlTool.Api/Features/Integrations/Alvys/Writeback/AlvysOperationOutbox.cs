using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace LtlTool.Api.Features.Integrations.Alvys.Writeback;

/// <summary>
/// Which channel produced an operation record: a <see cref="DryRun"/> preview or an
/// <see cref="Execute"/> attempt. Idempotency de-duplication applies only to executable records;
/// dry-run previews are recorded for audit but never collapse an execute attempt.
/// </summary>
public enum AlvysOperationChannel
{
    /// <summary>A dry-run preview — payload built/validated, recorded for audit, never executed.</summary>
    DryRun,

    /// <summary>An execute attempt — honours the writeback mode and participates in idempotency.</summary>
    Execute,
}

/// <summary>
/// Lifecycle status of a persisted operation record. The boundary never performs a live Alvys
/// mutation in this phase, so a record never reaches a "sent/succeeded" state — it captures the
/// auditable intent and the disposition the gateway resolved.
/// </summary>
public enum AlvysOperationRecordStatus
{
    /// <summary>Recorded for audit (audit-only) or simulated; nothing was sent to Alvys.</summary>
    Recorded,

    /// <summary>Validation blocked the request; no executable payload was produced.</summary>
    Blocked,

    /// <summary>No documented Alvys mutating endpoint; recorded with the reason it cannot execute.</summary>
    Unsupported,
}

/// <summary>
/// A durable outbox/audit row for an Alvys write-oriented operation request. This is the persistence
/// foundation that lets future Alvys sandbox execution be queued, audited, retried and de-duplicated
/// safely once a documented mutating endpoint exists. In this phase nothing is ever sent to Alvys, so
/// the row records the <i>intent</i>: the operation, the resource it targets, a hash + preview of the
/// payload that <b>would</b> be sent, the idempotency key, the resolved disposition and status, the
/// active writeback mode/posture, the owner, timestamps, attempt count, last error and a correlation id.
///
/// <para>
/// It deliberately stores <b>no secrets</b>: no bearer tokens, no OAuth credentials, no Authorization
/// headers — only the business payload preview the dispatcher already supplied.
/// </para>
/// </summary>
public sealed class AlvysOperationRecord
{
    public required string Id { get; set; }

    /// <summary>The dispatcher who owns this record; every query is scoped to it.</summary>
    public required string OwnerId { get; set; }

    /// <summary>Stable operation code from <see cref="AlvysWriteOperationRegistry"/>.</summary>
    public required string OperationCode { get; set; }

    /// <summary>Whether this row came from a dry-run preview or an execute attempt.</summary>
    public AlvysOperationChannel Channel { get; set; }

    /// <summary>The kind of resource the operation targets (e.g. <c>load</c>, <c>tender</c>, <c>trip</c>).</summary>
    public string? ResourceType { get; set; }

    /// <summary>The business identifier of the targeted resource (load number, tender id, trip id).</summary>
    public string? ResourceId { get; set; }

    /// <summary>
    /// Caller-supplied idempotency key (null when none was provided). For execute records this is
    /// unique per owner: a repeat with an equivalent payload is a no-op replay, a repeat with a
    /// different payload is a conflict.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>SHA-256 hex digest of the canonical payload, used to detect equivalent vs conflicting requests.</summary>
    public required string PayloadHash { get; set; }

    /// <summary>A bounded JSON preview of the payload body that would be sent (never a secret/token).</summary>
    public string? PayloadPreview { get; set; }

    public AlvysWritebackMode Mode { get; set; }
    public AlvysOperationDisposition Disposition { get; set; }
    public AlvysOperationRecordStatus Status { get; set; }

    /// <summary>Optional dispatcher justification carried from the request onto the audit trail.</summary>
    public string? Reason { get; set; }

    /// <summary>The most recent error detail (null while no live execution is attempted).</summary>
    public string? LastError { get; set; }

    /// <summary>How many times this operation has been requested (incremented on idempotent replay).</summary>
    public int AttemptCount { get; set; }

    /// <summary>Correlation id grouping this record with the request/response that produced it.</summary>
    public required string CorrelationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Owner-scoped persistence for the Alvys operation outbox. The seam is abstracted so the durable
/// production store (<see cref="EfAlvysOperationStore"/>, backed by EF Core / <c>AppDbContext</c>) and
/// the in-memory double used by unit tests share one contract. Every method is scoped to an owner so
/// one dispatcher can never read or mutate another's operation history.
/// </summary>
public interface IAlvysOperationStore
{
    /// <summary>Most-recent-first operation history for an owner, capped at <paramref name="limit"/>.</summary>
    IReadOnlyList<AlvysOperationRecord> ListForOwner(string ownerId, int limit);

    /// <summary>A single record by id, scoped to the owner (null when missing or foreign).</summary>
    AlvysOperationRecord? Get(string ownerId, string id);

    /// <summary>
    /// The existing <b>executable</b> record for an owner + idempotency key, or null. Used to
    /// detect idempotent replays and payload conflicts before inserting a new executable record.
    /// </summary>
    AlvysOperationRecord? FindExecutableByKey(string ownerId, string idempotencyKey);

    /// <summary>Persists a new record.</summary>
    void Add(AlvysOperationRecord record);

    /// <summary>Persists mutations made to a tracked/known record (e.g. an incremented attempt count).</summary>
    void Update(AlvysOperationRecord record);
}

/// <summary>
/// EF Core-backed <see cref="IAlvysOperationStore"/> — the production store. Records live in
/// <see cref="LtlTool.Api.Data.AppDbContext"/> (SQL Server in production) so the outbox survives
/// restarts and is shared across instances. Nothing here touches Alvys: recording an operation has no
/// effect on the upstream source of truth.
/// </summary>
public sealed class EfAlvysOperationStore(LtlTool.Api.Data.AppDbContext db) : IAlvysOperationStore
{
    public IReadOnlyList<AlvysOperationRecord> ListForOwner(string ownerId, int limit) =>
        // Owner scope is applied in SQL; ordering/limiting is done client-side so the query translates
        // on every provider (SQLite cannot ORDER BY datetimeoffset). The per-owner audit set is small.
        db.AlvysOperations.AsNoTracking()
            .Where(r => r.OwnerId == ownerId)
            .AsEnumerable()
            .OrderByDescending(r => r.CreatedAt)
            .Take(limit)
            .ToArray();

    public AlvysOperationRecord? Get(string ownerId, string id) =>
        db.AlvysOperations.AsNoTracking()
            .FirstOrDefault(r => r.OwnerId == ownerId && r.Id == id);

    public AlvysOperationRecord? FindExecutableByKey(string ownerId, string idempotencyKey) =>
        db.AlvysOperations
            .FirstOrDefault(r =>
                r.OwnerId == ownerId
                && r.Channel == AlvysOperationChannel.Execute
                && r.IdempotencyKey == idempotencyKey);

    public void Add(AlvysOperationRecord record)
    {
        db.AlvysOperations.Add(record);
        db.SaveChanges();
    }

    public void Update(AlvysOperationRecord record)
    {
        db.AlvysOperations.Update(record);
        db.SaveChanges();
    }
}

/// <summary>
/// Thread-safe in-memory <see cref="IAlvysOperationStore"/>. <b>Not durable across restarts</b>, so it
/// is not registered in production — <see cref="EfAlvysOperationStore"/> is. Retained as a fast,
/// dependency-free double for store/recorder unit tests.
/// </summary>
public sealed class InMemoryAlvysOperationStore : IAlvysOperationStore
{
    private readonly ConcurrentDictionary<string, AlvysOperationRecord> _byId = new();
    private readonly object _gate = new();

    public IReadOnlyList<AlvysOperationRecord> ListForOwner(string ownerId, int limit)
    {
        lock (_gate)
        {
            return _byId.Values
                .Where(r => r.OwnerId == ownerId)
                .OrderByDescending(r => r.CreatedAt)
                .ThenByDescending(r => r.Id)
                .Take(limit)
                .Select(Clone)
                .ToArray();
        }
    }

    public AlvysOperationRecord? Get(string ownerId, string id)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(id, out var record) && record.OwnerId == ownerId
                ? Clone(record)
                : null;
        }
    }

    public AlvysOperationRecord? FindExecutableByKey(string ownerId, string idempotencyKey)
    {
        lock (_gate)
        {
            var match = _byId.Values.FirstOrDefault(r =>
                r.OwnerId == ownerId
                && r.Channel == AlvysOperationChannel.Execute
                && r.IdempotencyKey == idempotencyKey);
            return match is null ? null : Clone(match);
        }
    }

    public void Add(AlvysOperationRecord record)
    {
        lock (_gate) _byId[record.Id] = Clone(record);
    }

    public void Update(AlvysOperationRecord record)
    {
        lock (_gate) _byId[record.Id] = Clone(record);
    }

    private static AlvysOperationRecord Clone(AlvysOperationRecord r) => new()
    {
        Id = r.Id,
        OwnerId = r.OwnerId,
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
/// Computes the canonical payload hash used to decide whether two requests under the same idempotency
/// key are equivalent (idempotent replay) or conflicting. The hash is taken over the operation code
/// plus the concrete payload body the gateway built, serialized with sorted keys so property order
/// never changes the digest.
/// </summary>
public static class AlvysPayloadHasher
{
    public static string Hash(string operationCode, IReadOnlyDictionary<string, object?>? body)
    {
        var ordered = (body ?? new Dictionary<string, object?>())
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var canonical = operationCode + "\n" +
            JsonSerializer.Serialize(ordered, CanonicalOptions);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        WriteIndented = false,
    };
}
