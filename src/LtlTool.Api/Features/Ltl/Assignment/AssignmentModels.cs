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

    /// <summary>
    /// Dispatcher acknowledgement when assigning over one or more non-blocking warnings. Captured
    /// on the audit so an override is traceable to a person and a reason.
    /// </summary>
    public string? OverrideReason { get; set; }
}

/// <summary>Severity of an assignment-validation finding.</summary>
public enum AssignmentIssueSeverity
{
    /// <summary>Hard rule violation — the internal assignment is refused.</summary>
    Block,
    /// <summary>Soft concern — the assignment is allowed but the warning is recorded.</summary>
    Warn,
}

/// <summary>A single explainable assignment-validation finding.</summary>
public sealed class AssignmentIssue
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public required AssignmentIssueSeverity Severity { get; init; }
}

/// <summary>
/// Outcome of validating a proposed internal assignment against the normalized load and the
/// resolved fleet candidate. Blockers refuse the assignment; warnings are allowed through (and
/// recorded on the audit) so the dispatcher keeps control while the decision stays traceable.
/// </summary>
public sealed class AssignmentValidationResult
{
    public IReadOnlyList<AssignmentIssue> Issues { get; init; } = [];

    public IEnumerable<AssignmentIssue> Blockers =>
        Issues.Where(i => i.Severity == AssignmentIssueSeverity.Block);

    public IEnumerable<AssignmentIssue> Warnings =>
        Issues.Where(i => i.Severity == AssignmentIssueSeverity.Warn);

    public bool HasBlockers => Issues.Any(i => i.Severity == AssignmentIssueSeverity.Block);
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

    /// <summary>Dispatcher acknowledgement recorded when assigning over warnings.</summary>
    public string? OverrideReason { get; init; }

    /// <summary>
    /// Non-blocking validation warnings present at decision time (e.g. equipment mismatch, tight
    /// window, missing billing data). Captured so the override is fully explainable after the fact.
    /// </summary>
    public IReadOnlyList<AssignmentIssue> Warnings { get; init; } = [];

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
    AssignmentAudit Record(
        string loadId, AssignmentRequest request, string recordedBy,
        IReadOnlyList<AssignmentIssue>? warnings = null);
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

    public AssignmentAudit Record(
        string loadId, AssignmentRequest request, string recordedBy,
        IReadOnlyList<AssignmentIssue>? warnings = null)
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
            OverrideReason = request.OverrideReason,
            Warnings = warnings ?? [],
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
