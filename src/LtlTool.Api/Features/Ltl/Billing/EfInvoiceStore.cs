using LtlTool.Api.Data;

namespace LtlTool.Api.Features.Ltl.Billing;

/// <summary>
/// Durable EF Core row behind an assembled invoice. Lives in <see cref="AppDbContext"/> (SQL Server
/// in production) so drafts and their edit history survive restarts and back the Invoice Studio list.
/// The load lines (with per-load charges) and edit history are stored as JSON columns, matching the
/// same posture as the assignment-audit and yard-artifact stores. Nothing here touches Alvys —
/// <see cref="AlvysWriteback"/> stays <c>"NotPerformed"</c>.
/// </summary>
public sealed class InvoiceRecord
{
    public required string Id { get; set; }
    public required string InvoiceNumber { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

    public string? CorridorCode { get; set; }
    public string? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? ParentLoadId { get; set; }
    public string? ParentLoadNumber { get; set; }
    public string? Notes { get; set; }

    /// <summary>Serialized <see cref="InvoiceLoadLine"/> list (nvarchar(max)).</summary>
    public required string LoadsJson { get; set; }

    /// <summary>Serialized <see cref="InvoiceEditEvent"/> list (nvarchar(max)).</summary>
    public required string EditHistoryJson { get; set; }

    public decimal InvoiceTotal { get; set; }
    public decimal CombinedRevenue { get; set; }
    public decimal? CombinedDriverTripValue { get; set; }
    public decimal? DriverLoadedMiles { get; set; }
    public decimal? CombinedRevenuePerMile { get; set; }

    /// <summary>Count of loads with no linked BOL — persisted for a cheap list-view badge/filter.</summary>
    public int LoadsMissingBolCount { get; set; }

    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
    public string? FinalizedBy { get; set; }

    public string AlvysWriteback { get; set; } = "NotPerformed";
}

/// <summary>Thin persistence boundary for invoices. Serialization/mapping lives in the service.</summary>
public interface IInvoiceStore
{
    void Add(InvoiceRecord record);
    void Update(InvoiceRecord record);
    InvoiceRecord? Get(string id);
    IReadOnlyList<InvoiceRecord> List(string? parentLoadId, InvoiceStatus? status, int max);
}

/// <summary>EF Core-backed <see cref="IInvoiceStore"/>: the production store. Read-only against Alvys.</summary>
public sealed class EfInvoiceStore(AppDbContext db) : IInvoiceStore
{
    public void Add(InvoiceRecord record)
    {
        db.Invoices.Add(record);
        db.SaveChanges();
    }

    public void Update(InvoiceRecord record)
    {
        db.Invoices.Update(record);
        db.SaveChanges();
    }

    public InvoiceRecord? Get(string id) => db.Invoices.FirstOrDefault(r => r.Id == id);

    public IReadOnlyList<InvoiceRecord> List(string? parentLoadId, InvoiceStatus? status, int max)
    {
        var q = db.Invoices.AsQueryable();
        if (!string.IsNullOrWhiteSpace(parentLoadId))
            q = q.Where(r => r.ParentLoadId == parentLoadId);
        if (status is { } s)
            q = q.Where(r => r.Status == s);

        // Order in memory: SQLite (test double) cannot ORDER BY over DateTimeOffset.
        return q.AsEnumerable()
            .OrderByDescending(r => r.UpdatedAt)
            .Take(Math.Clamp(max, 1, 200))
            .ToArray();
    }
}
