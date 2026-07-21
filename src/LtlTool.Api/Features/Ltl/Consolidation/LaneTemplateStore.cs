using LtlTool.Api.Data;

namespace LtlTool.Api.Features.Ltl.Consolidation;

/// <summary>
/// Store for recurring-lane templates (Phase 2.5). Abstracted so tests can use a lightweight
/// double while production persists to <see cref="AppDbContext"/>. Nothing here touches Alvys —
/// templates are internal lane-shape notes.
/// </summary>
public interface ILaneTemplateStore
{
    /// <summary>Persists a new template and returns it.</summary>
    LaneTemplateRecord Add(LaneTemplateRecord record);

    /// <summary>Single template by id, or null when not found.</summary>
    LaneTemplateRecord? Get(string id);

    /// <summary>Templates matching the filter, newest first.</summary>
    IReadOnlyList<LaneTemplateRecord> Query(LaneTemplateQuery query);

    /// <summary>Deletes a template by id. Returns true when a row was removed.</summary>
    bool Delete(string id);
}

/// <summary>
/// EF Core-backed <see cref="ILaneTemplateStore"/>: the production store. Templates survive
/// restarts and are shared across instances because they live in <see cref="AppDbContext"/>
/// (SQL Server in production). Nothing in this store touches Alvys.
/// </summary>
public sealed class EfLaneTemplateStore(AppDbContext db) : ILaneTemplateStore
{
    public LaneTemplateRecord Add(LaneTemplateRecord record)
    {
        db.LaneTemplates.Add(record);
        db.SaveChanges();
        return record;
    }

    public LaneTemplateRecord? Get(string id) =>
        db.LaneTemplates.FirstOrDefault(r => r.Id == id);

    public IReadOnlyList<LaneTemplateRecord> Query(LaneTemplateQuery query)
    {
        var q = db.LaneTemplates.AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.CorridorCode))
            q = q.Where(r => r.CorridorCode == query.CorridorCode);
        if (!string.IsNullOrWhiteSpace(query.CustomerName))
            q = q.Where(r => r.CustomerName == query.CustomerName);

        var max = query.Max <= 0 ? 100 : Math.Min(query.Max, 500);

        // Order newest-first in memory: the filtered set is small and SQLite (used by tests)
        // cannot translate an ORDER BY over DateTimeOffset — mirrors EfYardArtifactStore.
        return q.AsEnumerable()
            .OrderByDescending(r => r.CreatedAt)
            .Take(max)
            .ToArray();
    }

    public bool Delete(string id)
    {
        var record = db.LaneTemplates.FirstOrDefault(r => r.Id == id);
        if (record is null) return false;
        db.LaneTemplates.Remove(record);
        db.SaveChanges();
        return true;
    }
}
