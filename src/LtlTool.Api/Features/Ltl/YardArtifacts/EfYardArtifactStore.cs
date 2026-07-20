using LtlTool.Api.Data;

namespace LtlTool.Api.Features.Ltl.YardArtifacts;

/// <summary>
/// EF Core-backed <see cref="IYardArtifactStore"/>: the production store. Artifacts survive restarts
/// and are shared across instances because they live in <see cref="AppDbContext"/> (SQL Server in
/// production). Queries are keyed by equipment unit / load number / yard so the arrivals board and
/// load-detail page can surface the artifacts attached to a given truck, trailer or load. Nothing in
/// this store touches Alvys.
/// </summary>
public sealed class EfYardArtifactStore(AppDbContext db) : IYardArtifactStore
{
    public void Add(YardArtifactRecord record)
    {
        db.YardArtifacts.Add(record);
        db.SaveChanges();
    }

    public YardArtifactRecord? Get(string id) =>
        db.YardArtifacts.FirstOrDefault(r => r.Id == id);

    public IReadOnlyList<YardArtifactRecord> Query(YardArtifactQuery query)
    {
        var q = db.YardArtifacts.AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.LoadNumber))
            q = q.Where(r => r.LoadNumber == query.LoadNumber);
        if (!string.IsNullOrWhiteSpace(query.TruckUnit))
            q = q.Where(r => r.TruckUnit == query.TruckUnit);
        if (!string.IsNullOrWhiteSpace(query.TrailerUnit))
            q = q.Where(r => r.TrailerUnit == query.TrailerUnit);
        if (!string.IsNullOrWhiteSpace(query.Yard))
            q = q.Where(r => r.Yard == query.Yard);

        var max = query.Max <= 0 ? 100 : Math.Min(query.Max, 500);

        return q.OrderByDescending(r => r.CreatedAt)
            .Take(max)
            .ToArray();
    }
}
