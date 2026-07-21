using System.Collections.Concurrent;

namespace LtlTool.Api.Features.Ltl.Assignment;

/// <summary>
/// Typed short reason an override was recorded (Phase 3 reason taxonomy). Slicing the audit by a
/// closed set of reasons lets reporting group overrides without NLP over free text. The free-text
/// <see cref="AssignmentRequest.OverrideReason"/> detail is preserved alongside it.
/// <para><see cref="Unspecified"/> is the safe default and is also how a legacy free-text-only
/// audit row (recorded before the taxonomy existed) reads — the detail is still shown.</para>
/// </summary>
public enum AssignmentReasonType
{
    /// <summary>No typed reason supplied (default; also covers legacy free-text-only rows).</summary>
    Unspecified,
    /// <summary>Customer specifically asked for this driver/equipment.</summary>
    CustomerRequest,
    /// <summary>Recovering a late or failed load; speed over the flagged concern.</summary>
    ServiceRecovery,
    /// <summary>Only viable capacity available for the window.</summary>
    CapacityConstraint,
    /// <summary>Substituting equipment that differs from the load's requested type.</summary>
    EquipmentSubstitution,
    /// <summary>Driver availability / preference drove the choice.</summary>
    DriverAvailability,
    /// <summary>Chosen to reduce deadhead / improve margin.</summary>
    CostOptimization,
    /// <summary>Compliance concern was reviewed and cleared out of band.</summary>
    ComplianceReviewed,
    /// <summary>Reason not covered above — see the free-text detail.</summary>
    Other,
}

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
    /// Typed override reason (Phase 3 taxonomy) captured when assigning over one or more
    /// non-blocking warnings. Defaults to <see cref="AssignmentReasonType.Unspecified"/>.
    /// </summary>
    public AssignmentReasonType? ReasonType { get; set; }

    /// <summary>
    /// Free-text detail behind the override, preserved alongside the typed
    /// <see cref="ReasonType"/> so the acknowledgement stays traceable to a person and a reason.
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

    /// <summary>
    /// Typed override reason (Phase 3 taxonomy). <see cref="AssignmentReasonType.Unspecified"/>
    /// on legacy rows recorded before the taxonomy existed — the free-text
    /// <see cref="OverrideReason"/> detail is still preserved and shown for those.
    /// </summary>
    public AssignmentReasonType ReasonType { get; init; } = AssignmentReasonType.Unspecified;

    /// <summary>Free-text detail behind the override, recorded when assigning over warnings.</summary>
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
/// One proposed assignment in a preflight batch-validate call (Phase 3). Mirrors the per-load
/// <see cref="AssignmentRequest"/> plus the load to resolve, so a dispatcher can pre-screen the top
/// of a worklist in a single round trip. Purely a dry run — nothing is recorded and Alvys is untouched.
/// </summary>
public sealed class AssignmentBatchValidateItem
{
    public string? LoadId { get; set; }
    public string? DriverId { get; set; }
    public string? TruckId { get; set; }
    public string? TrailerId { get; set; }
    public int? MatchScore { get; set; }
    public string? MatchLabel { get; set; }
}

/// <summary>Request wrapper for <c>POST /api/ltl/assign/validate-batch</c>.</summary>
public sealed class AssignmentBatchValidateRequest
{
    public List<AssignmentBatchValidateItem> Items { get; set; } = [];
}

/// <summary>
/// Per-row outcome of a batch validate. <see cref="Found"/> is false when the load could not be
/// resolved upstream (its counts are zero and its issue lists empty). Blockers/warnings carry the
/// full explainable findings so the UI can render the same detail as the single-load validate.
/// </summary>
public sealed class AssignmentBatchValidateRow
{
    public required string LoadId { get; init; }
    public bool Found { get; init; }
    public int BlockerCount { get; init; }
    public int WarningCount { get; init; }
    public bool HasBlockers => BlockerCount > 0;
    public IReadOnlyList<AssignmentIssue> Blockers { get; init; } = [];
    public IReadOnlyList<AssignmentIssue> Warnings { get; init; } = [];
}

/// <summary>Response for <c>POST /api/ltl/assign/validate-batch</c>: one row per requested item.</summary>
public sealed class AssignmentBatchValidateResponse
{
    public IReadOnlyList<AssignmentBatchValidateRow> Rows { get; init; } = [];
}

/// <summary>Filter for the cross-load assignment audit history (Phase 3 /ltl/assignments page).</summary>
/// <param name="RecordedBy">Exact user match (case-insensitive) when set.</param>
/// <param name="Day">Restrict to decisions recorded on this UTC calendar day when set.</param>
/// <param name="ReasonType">Restrict to a single typed override reason when set.</param>
/// <param name="Max">Upper bound on returned rows (defaults applied by the store).</param>
public sealed record AssignmentAuditQuery(
    string? RecordedBy = null,
    DateOnly? Day = null,
    AssignmentReasonType? ReasonType = null,
    int Max = 200);

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

    /// <summary>
    /// Cross-load audit history, newest first, filtered by <paramref name="query"/>. Backs the
    /// filterable /ltl/assignments history page.
    /// </summary>
    IReadOnlyList<AssignmentAudit> Query(AssignmentAuditQuery query);
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
            ReasonType = request.ReasonType ?? AssignmentReasonType.Unspecified,
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

    public IReadOnlyList<AssignmentAudit> Query(AssignmentAuditQuery query)
    {
        lock (_gate)
        {
            var all = _byLoad.Values.SelectMany(list => list);
            return AssignmentAuditQueryFilter.Apply(all, query);
        }
    }
}

/// <summary>
/// Shared post-fetch filter/sort for assignment audit history, so the in-memory and EF stores
/// return identical results for the same <see cref="AssignmentAuditQuery"/>.
/// </summary>
internal static class AssignmentAuditQueryFilter
{
    public static IReadOnlyList<AssignmentAudit> Apply(
        IEnumerable<AssignmentAudit> source, AssignmentAuditQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.RecordedBy))
            source = source.Where(a =>
                string.Equals(a.RecordedBy, query.RecordedBy, StringComparison.OrdinalIgnoreCase));

        if (query.ReasonType is { } reason)
            source = source.Where(a => a.ReasonType == reason);

        if (query.Day is { } day)
            source = source.Where(a => DateOnly.FromDateTime(a.RecordedAt.UtcDateTime) == day);

        var max = query.Max <= 0 ? 200 : Math.Min(query.Max, 1000);

        return source
            .OrderByDescending(a => a.RecordedAt)
            .Take(max)
            .ToArray();
    }
}
