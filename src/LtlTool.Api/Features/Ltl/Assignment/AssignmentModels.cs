using System.Collections.Concurrent;

namespace LtlTool.Api.Features.Ltl.Assignment;

/// <summary>Request to record an internal (non-Alvys) assignment decision for a load.</summary>
public sealed class AssignmentRequest
{
    public string? DriverId { get; set; }
    public string? TruckId { get; set; }
    public string? TrailerId { get; set; }

    /// <summary>The match score at decision time, captured for the audit trail.</summary>
    public int? MatchScore { get; set; }

    /// <summary>The match label text at decision time (e.g. "Good Match").</summary>
    public string? MatchLabel { get; set; }

    /// <summary>Optional free-text justification from the dispatcher.</summary>
    public string? Notes { get; set; }
}

/// <summary>
/// An immutable record of an internal assignment decision. This is the audit boundary: the
/// decision is captured locally for traceability, and is explicitly <b>not</b> written back to
/// Alvys in this phase. <see cref="AlvysWriteback"/> documents that boundary in the payload.
/// </summary>
public sealed class AssignmentAudit
{
    public required string Id { get; init; }
    public required string LoadId { get; init; }
    public string? DriverId { get; init; }
    public string? TruckId { get; init; }
    public string? TrailerId { get; init; }
    public int? MatchScore { get; init; }
    public string? MatchLabel { get; init; }
    public string? Notes { get; init; }

    /// <summary>The authenticated user who recorded the decision.</summary>
    public required string RecordedBy { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }

    /// <summary>
    /// Always <c>"NotPerformed"</c> in this phase. The Alvys integration is read-only; pushing
    /// trip/driver assignment upstream is a deliberate future boundary (see README).
    /// </summary>
    public string AlvysWriteback { get; init; } = "NotPerformed";
}

/// <summary>
/// Stores internal assignment-decision audit records. The audit boundary is intentionally
/// abstracted so a production deployment can swap the in-memory default for a persistent,
/// queryable store (e.g. EF Core) without touching the controller — and so the future Alvys
/// writeback can be introduced in one place behind this seam.
/// </summary>
public interface IAssignmentAuditStore
{
    AssignmentAudit Record(string loadId, AssignmentRequest request, string recordedBy);
    IReadOnlyList<AssignmentAudit> ForLoad(string loadId);
}

/// <summary>
/// Thread-safe in-memory <see cref="IAssignmentAuditStore"/>. Suitable for the first slice and
/// local/UAT; not durable across restarts. Replace with a persistent store for production.
/// </summary>
public sealed class InMemoryAssignmentAuditStore : IAssignmentAuditStore
{
    private readonly ConcurrentDictionary<string, List<AssignmentAudit>> _byLoad = new();
    private readonly object _gate = new();

    public AssignmentAudit Record(string loadId, AssignmentRequest request, string recordedBy)
    {
        var audit = new AssignmentAudit
        {
            Id = Guid.NewGuid().ToString("n"),
            LoadId = loadId,
            DriverId = request.DriverId,
            TruckId = request.TruckId,
            TrailerId = request.TrailerId,
            MatchScore = request.MatchScore,
            MatchLabel = request.MatchLabel,
            Notes = request.Notes,
            RecordedBy = recordedBy,
            RecordedAt = DateTimeOffset.UtcNow,
        };

        lock (_gate)
        {
            var list = _byLoad.GetOrAdd(loadId, _ => []);
            list.Add(audit);
        }

        return audit;
    }

    public IReadOnlyList<AssignmentAudit> ForLoad(string loadId)
    {
        lock (_gate)
        {
            return _byLoad.TryGetValue(loadId, out var list)
                ? list.ToArray()
                : [];
        }
    }
}
