using System.Collections.Concurrent;

namespace LtlTool.Api.Features.Ltl.Consolidation;

/// <summary>
/// A recorded consolidation-plan audit entry. Captures the leadership-facing consolidation
/// value claim (projected combined revenue, mileage, RPM) plus the plan shape so the audit
/// trail is legible without a second Alvys lookup. Every value is either derived from live
/// Alvys reads at recording time or from the dispatcher who authored the plan — nothing is
/// re-derived when the record is read back.
///
/// <para>
/// Anti-failure map 3h: this record is the counter-signal to commission politics. When
/// leadership asks "did consolidation actually save us anything?", the accrued audit rows
/// answer with sourced values, not opinions.
/// </para>
/// </summary>
public sealed class ConsolidationAuditRecord
{
    public required string Id { get; init; }

    /// <summary>Corridor the plan targeted (e.g. <c>LAREDO_TO_DALLAS</c>).</summary>
    public required string CorridorCode { get; init; }

    public required string ParentLoadId { get; init; }
    public string? ParentLoadNumber { get; init; }
    public string? ParentCustomerName { get; init; }

    public required IReadOnlyList<string> SiblingLoadIds { get; init; }
    public required IReadOnlyList<string> SiblingLoadNumbers { get; init; }

    /// <summary>Projected combined revenue at record time. Null-safe.</summary>
    public decimal? CombinedRevenue { get; init; }

    /// <summary>
    /// Parent's customer-facing linehaul miles at record time. Kept for context; NOT the
    /// denominator of <see cref="CombinedRevenuePerMile"/>.
    /// </summary>
    public decimal? LinehaulMiles { get; init; }

    /// <summary>
    /// Parent's driver-facing loaded miles at record time. The actual denominator of
    /// <see cref="CombinedRevenuePerMile"/>.
    /// </summary>
    public decimal? DriverLoadedMiles { get; init; }

    /// <summary>Combined driver trip value at record time — the numerator of the RPM.</summary>
    public decimal? CombinedDriverTripValue { get; init; }

    /// <summary>
    /// Projected combined driver RPM at record time. Corrected 2026-07-18 to use
    /// driver-facing inputs (see <c>docs/ALVYS_API_DECISIONS.md</c>); prior audit entries
    /// pre-dating that PR were inflated billing RPM. Leadership dashboards should treat
    /// audit rows recorded before 2026-07-18 as billing RPM, after as driver RPM.
    /// </summary>
    public decimal? CombinedRevenuePerMile { get; init; }

    /// <summary>Blockers present at recording time. Non-empty audit rows are still recorded
    /// so leadership can see near-misses; the dispatcher's intent to consolidate is
    /// preserved even when the plan was invalidated after preview.</summary>
    public required IReadOnlyList<string> Blockers { get; init; }

    /// <summary>Alvys writeback posture at recording time. Always <c>NotPerformed</c> in
    /// Phase 1 read-only pilot; a future Phase 2 writeback will change this per operation
    /// following the same audit convention used by <c>AssignmentAudit</c>.</summary>
    public string AlvysWriteback { get; init; } = "NotPerformed";

    public required string RecordedBy { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
}

/// <summary>
/// Store for consolidation audit records. Abstracted so a production deployment can swap
/// the in-memory default for a persistent, queryable store (e.g. EF Core) without touching
/// the controller.
/// </summary>
public interface IConsolidationAuditStore
{
    /// <summary>Records a new audit entry and returns it (with server-assigned Id + timestamp).</summary>
    ConsolidationAuditRecord Record(
        ConsolidationPlanResponse plan,
        string recordedBy);

    /// <summary>All audit entries for a single parent load, newest first.</summary>
    IReadOnlyList<ConsolidationAuditRecord> ForParent(string parentLoadIdOrNumber);

    /// <summary>All audit entries recorded, newest first (for the leadership visibility view).</summary>
    IReadOnlyList<ConsolidationAuditRecord> All();
}

/// <summary>
/// Thread-safe in-memory <see cref="IConsolidationAuditStore"/>. Matches the same posture as
/// <see cref="Assignment.InMemoryAssignmentAuditStore"/>: suitable for the first slice and
/// local/UAT; not durable across restarts. Replace with an EF-backed store when Phase 2 lands
/// alongside the Alvys writeback path.
/// </summary>
public sealed class InMemoryConsolidationAuditStore(TimeProvider clock) : IConsolidationAuditStore
{
    private readonly TimeProvider _clock = clock;
    private readonly ConcurrentDictionary<string, List<ConsolidationAuditRecord>> _byParent = new();
    private readonly List<ConsolidationAuditRecord> _all = [];
    private readonly object _gate = new();

    public ConsolidationAuditRecord Record(
        ConsolidationPlanResponse plan,
        string recordedBy)
    {
        var record = new ConsolidationAuditRecord
        {
            Id = Guid.NewGuid().ToString("n"),
            CorridorCode = plan.CorridorCode,
            ParentLoadId = plan.Parent.Id,
            ParentLoadNumber = plan.Parent.LoadNumber,
            ParentCustomerName = plan.Parent.CustomerName,
            SiblingLoadIds = plan.Siblings.Select(s => s.LoadId).ToArray(),
            SiblingLoadNumbers = plan.Siblings.Select(s => s.LoadNumber ?? s.LoadId).ToArray(),
            CombinedRevenue = plan.CombinedRevenue,
            LinehaulMiles = plan.LinehaulMiles,
            DriverLoadedMiles = plan.DriverLoadedMiles,
            CombinedDriverTripValue = plan.CombinedDriverTripValue,
            CombinedRevenuePerMile = plan.CombinedRevenuePerMile,
            Blockers = plan.Blockers.ToArray(),
            RecordedBy = recordedBy,
            RecordedAt = _clock.GetUtcNow(),
        };

        lock (_gate)
        {
            var list = _byParent.GetOrAdd(record.ParentLoadId, _ => []);
            list.Add(record);
            _all.Add(record);
        }
        return record;
    }

    public IReadOnlyList<ConsolidationAuditRecord> ForParent(string parentLoadIdOrNumber)
    {
        lock (_gate)
        {
            // Match by parent load id first, then by load number as a courtesy so the SPA
            // can call with either.
            var byId = _byParent.TryGetValue(parentLoadIdOrNumber, out var list) ? list : [];
            var byNumber = _all
                .Where(r => string.Equals(
                    r.ParentLoadNumber, parentLoadIdOrNumber, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return byId.Concat(byNumber)
                .DistinctBy(r => r.Id)
                .OrderByDescending(r => r.RecordedAt)
                .ToArray();
        }
    }

    public IReadOnlyList<ConsolidationAuditRecord> All()
    {
        lock (_gate)
        {
            return _all
                .OrderByDescending(r => r.RecordedAt)
                .ToArray();
        }
    }
}
