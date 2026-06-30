using System.Text.Json;
using LtlTool.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LtlTool.Api.Features.Ltl.SavedViews;

/// <summary>
/// Durable persistence row for a dispatcher saved view. The dispatcher's filter/sort intent is
/// stored as serialized JSON (<see cref="FiltersJson"/>) rather than exploded into columns: the
/// filter set is a tool-local snapshot the SPA round-trips verbatim, so it never participates in
/// relational queries — only the owner scope does. Built-in presets are never persisted here.
/// </summary>
public sealed class SavedViewRecord
{
    public required string Id { get; set; }
    public required string OwnerId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Serialized <see cref="SavedViewFilters"/> — the dispatcher's verbatim filter/sort state.</summary>
    public required string FiltersJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// EF Core-backed <see cref="ISavedViewStore"/>: the production store. User views survive restarts
/// and are shared across instances because they live in <see cref="AppDbContext"/> (SQL Server in
/// production). Every operation is scoped to <c>ownerId</c> so one dispatcher can never read or
/// mutate another's views. Built-in presets are system-defined and are not stored here. Nothing in
/// this store touches Alvys — persisting a view has no effect on the upstream source of truth.
/// </summary>
public sealed class EfSavedViewStore(AppDbContext db, TimeProvider clock) : ISavedViewStore
{
    private static readonly JsonSerializerOptions FilterJsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<SavedView> ListForOwner(string ownerId) =>
        db.SavedViews.AsNoTracking()
            .Where(r => r.OwnerId == ownerId)
            .OrderBy(r => r.Name)
            .AsEnumerable()
            .Select(ToDomain)
            .ToArray();

    public SavedView? Get(string ownerId, string id)
    {
        var record = db.SavedViews.AsNoTracking()
            .FirstOrDefault(r => r.OwnerId == ownerId && r.Id == id);
        return record is null ? null : ToDomain(record);
    }

    public SavedView Create(string ownerId, SavedViewRequest request)
    {
        var now = clock.GetUtcNow();
        var record = new SavedViewRecord
        {
            Id = Guid.NewGuid().ToString("n"),
            OwnerId = ownerId,
            Name = request.Name!.Trim(),
            Description = NormalizeDescription(request.Description),
            FiltersJson = Serialize(request.Filters),
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.SavedViews.Add(record);
        db.SaveChanges();

        return ToDomain(record);
    }

    public SavedView? Update(string ownerId, string id, SavedViewRequest request)
    {
        var record = db.SavedViews.FirstOrDefault(r => r.OwnerId == ownerId && r.Id == id);
        if (record is null) return null;

        record.Name = request.Name!.Trim();
        record.Description = NormalizeDescription(request.Description);
        record.FiltersJson = Serialize(request.Filters);
        record.UpdatedAt = clock.GetUtcNow();

        db.SaveChanges();

        return ToDomain(record);
    }

    public bool Delete(string ownerId, string id)
    {
        var record = db.SavedViews.FirstOrDefault(r => r.OwnerId == ownerId && r.Id == id);
        if (record is null) return false;

        db.SavedViews.Remove(record);
        db.SaveChanges();
        return true;
    }

    private static SavedView ToDomain(SavedViewRecord record) => new()
    {
        Id = record.Id,
        Name = record.Name,
        Description = record.Description,
        Filters = Deserialize(record.FiltersJson),
        IsBuiltIn = false,
        OwnerId = record.OwnerId,
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
    };

    private static string Serialize(SavedViewFilters? filters) =>
        JsonSerializer.Serialize(filters ?? new SavedViewFilters(), FilterJsonOptions);

    private static SavedViewFilters Deserialize(string json) =>
        JsonSerializer.Deserialize<SavedViewFilters>(json, FilterJsonOptions) ?? new SavedViewFilters();

    private static string? NormalizeDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();
}
