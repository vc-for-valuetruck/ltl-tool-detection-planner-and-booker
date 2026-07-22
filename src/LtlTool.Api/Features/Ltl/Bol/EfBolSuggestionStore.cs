using LtlTool.Api.Data;

namespace LtlTool.Api.Features.Ltl.Bol;

/// <summary>
/// EF Core-backed <see cref="IBolSuggestionStore"/>: the production store. Suggestions survive
/// restarts and are shared across instances because they live in <see cref="AppDbContext"/>
/// (SQL Server in production, SQLite in tests). <see cref="AddBatch"/> is a single <c>SaveChanges</c>
/// so a read either records every suggestion or none — the fail-closed guarantee the read service
/// relies on. Nothing here touches Alvys.
/// </summary>
public sealed class EfBolSuggestionStore(AppDbContext db) : IBolSuggestionStore
{
    public void AddBatch(IReadOnlyList<BolFieldSuggestionRecord> records)
    {
        if (records.Count == 0) return;
        db.BolFieldSuggestions.AddRange(records);
        db.SaveChanges();
    }

    public BolFieldSuggestionRecord? Get(string id) =>
        db.BolFieldSuggestions.FirstOrDefault(r => r.Id == id);

    public IReadOnlyList<BolFieldSuggestionRecord> Query(BolSuggestionQuery query)
    {
        var q = db.BolFieldSuggestions.AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.LoadNumber))
            q = q.Where(r => r.LoadNumber == query.LoadNumber);
        if (query.Status is { } status)
            q = q.Where(r => r.Status == status.ToString());

        var max = query.Max <= 0 ? 200 : Math.Min(query.Max, 500);

        // Order newest-first in memory: the filtered set is bounded and SQLite (used by tests)
        // cannot translate an ORDER BY over DateTimeOffset. Mirrors EfSignalStore.
        return q.AsEnumerable()
            .OrderByDescending(r => r.CreatedAt)
            .Take(max)
            .ToArray();
    }

    public BolFieldSuggestionRecord? UpdateStatus(
        string id, BolSuggestionStatus status, string decidedBy, DateTimeOffset decidedAt)
    {
        var record = db.BolFieldSuggestions.FirstOrDefault(r => r.Id == id);
        if (record is null) return null;

        record.Status = status.ToString();
        record.DecidedBy = decidedBy;
        record.DecidedAt = decidedAt;
        db.SaveChanges();
        return record;
    }
}
