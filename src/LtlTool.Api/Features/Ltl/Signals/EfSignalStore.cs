using LtlTool.Api.Data;

namespace LtlTool.Api.Features.Ltl.Signals;

/// <summary>
/// EF Core-backed <see cref="ISignalStore"/>: the production store. Signals survive restarts and are
/// shared across instances because they live in <see cref="AppDbContext"/> (SQL Server in production,
/// SQLite in tests). <see cref="AddBatch"/> is a single <c>SaveChanges</c> so a request either records
/// every signal or none — the fail-closed guarantee the ingest service relies on. Nothing here
/// touches Alvys.
/// </summary>
public sealed class EfSignalStore(AppDbContext db) : ISignalStore
{
    public void AddBatch(IReadOnlyList<SignalRecord> records)
    {
        if (records.Count == 0) return;
        db.Signals.AddRange(records);
        db.SaveChanges();
    }

    public SignalRecord? Get(string id) => db.Signals.FirstOrDefault(r => r.Id == id);

    public IReadOnlyList<SignalRecord> Query(SignalQuery query)
    {
        var q = db.Signals.AsQueryable();

        if (query.Status is { } status)
            q = q.Where(r => r.Status == status.ToString());
        if (!string.IsNullOrWhiteSpace(query.SourceType))
            q = q.Where(r => r.SourceType == query.SourceType);
        if (!string.IsNullOrWhiteSpace(query.LoadNumber))
            q = q.Where(r => r.LoadNumber == query.LoadNumber);

        var max = query.Max <= 0 ? 100 : Math.Min(query.Max, 500);

        // Order newest-first in memory: the filtered set is bounded and SQLite (used by tests)
        // cannot translate an ORDER BY over DateTimeOffset. Mirrors EfYardArtifactStore.
        return q.AsEnumerable()
            .OrderByDescending(r => r.CreatedAt)
            .Take(max)
            .ToArray();
    }

    public SignalRecord? UpdateStatus(
        string id, SignalStatus status, string decidedBy, DateTimeOffset decidedAt)
    {
        var record = db.Signals.FirstOrDefault(r => r.Id == id);
        if (record is null) return null;

        record.Status = status.ToString();
        record.DecidedBy = decidedBy;
        record.DecidedAt = decidedAt;
        db.SaveChanges();
        return record;
    }
}
