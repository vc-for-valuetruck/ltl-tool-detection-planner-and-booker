using System.Text.Json;
using LtlTool.Api.Data;

namespace LtlTool.Api.Features.Ltl.Assignment;

/// <summary>
/// Durable EF Core row behind an <see cref="AssignmentAudit"/>. Lives in <see cref="AppDbContext"/>
/// (SQL Server in production) so assignment decisions survive restarts and are queryable for the
/// /ltl/assignments history page. Nothing in this store touches Alvys — <see cref="AlvysWriteback"/>
/// stays <c>"NotPerformed"</c>, matching the read-only posture of the whole assignment boundary.
/// <para>The validation <see cref="AssignmentAudit.Warnings"/> list is stored as JSON in
/// <see cref="WarningsJson"/>; the typed <see cref="ReasonType"/> is stored as a readable string so
/// the audit table is legible in the database and a legacy free-text row reads as
/// <see cref="AssignmentReasonType.Unspecified"/>.</para>
/// </summary>
public sealed class AssignmentAuditRecord
{
    public required string Id { get; init; }
    public required string LoadId { get; init; }
    public string? DriverId { get; init; }
    public string? TruckId { get; init; }
    public string? TrailerId { get; init; }
    public int? MatchScore { get; init; }
    public string? MatchLabel { get; init; }
    public string? Notes { get; init; }
    public AssignmentReasonType ReasonType { get; init; } = AssignmentReasonType.Unspecified;
    public string? OverrideReason { get; init; }
    public required string WarningsJson { get; init; }
    public required string RecordedBy { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public string AlvysWriteback { get; init; } = "NotPerformed";
}

/// <summary>
/// EF Core-backed <see cref="IAssignmentAuditStore"/>: the production store. Records survive
/// restarts and are shared across instances. Read-only against Alvys.
/// </summary>
public sealed class EfAssignmentAuditStore(AppDbContext db) : IAssignmentAuditStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AssignmentAudit Record(
        string loadId, AssignmentRequest request, string recordedBy,
        IReadOnlyList<AssignmentIssue>? warnings = null)
    {
        var effectiveWarnings = warnings ?? [];
        var record = new AssignmentAuditRecord
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
            WarningsJson = JsonSerializer.Serialize(effectiveWarnings, Json),
            RecordedBy = recordedBy,
            RecordedAt = DateTimeOffset.UtcNow,
        };

        db.AssignmentAudits.Add(record);
        db.SaveChanges();

        return ToAudit(record);
    }

    public IReadOnlyList<AssignmentAudit> ForLoad(string loadId) =>
        db.AssignmentAudits
            .Where(r => r.LoadId == loadId)
            .AsEnumerable()
            .Select(ToAudit)
            .OrderByDescending(a => a.RecordedAt)
            .ToArray();

    public IReadOnlyList<AssignmentAudit> Query(AssignmentAuditQuery query)
    {
        // ReasonType (an exact enum-string match) translates to SQL. RecordedBy is matched
        // case-insensitively in AssignmentAuditQueryFilter.Apply, not here: a SQL `==` prefilter is
        // case-sensitive on SQLite but case-insensitive on SQL Server, so pushing it down would give
        // provider-dependent results. The UTC-day filter and ordering also run in memory (SQLite
        // cannot ORDER BY / date-extract over DateTimeOffset).
        var q = db.AssignmentAudits.AsQueryable();

        if (query.ReasonType is { } reason)
            q = q.Where(r => r.ReasonType == reason);

        return AssignmentAuditQueryFilter.Apply(q.AsEnumerable().Select(ToAudit), query);
    }

    private static AssignmentAudit ToAudit(AssignmentAuditRecord r) => new()
    {
        Id = r.Id,
        LoadId = r.LoadId,
        DriverId = r.DriverId,
        TruckId = r.TruckId,
        TrailerId = r.TrailerId,
        MatchScore = r.MatchScore,
        MatchLabel = r.MatchLabel,
        Notes = r.Notes,
        ReasonType = r.ReasonType,
        OverrideReason = r.OverrideReason,
        Warnings = string.IsNullOrWhiteSpace(r.WarningsJson)
            ? []
            : JsonSerializer.Deserialize<List<AssignmentIssue>>(r.WarningsJson, Json) ?? [],
        RecordedBy = r.RecordedBy,
        RecordedAt = r.RecordedAt,
        AlvysWriteback = r.AlvysWriteback,
    };
}
