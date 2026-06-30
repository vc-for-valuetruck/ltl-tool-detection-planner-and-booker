using System.Collections.Concurrent;

namespace LtlTool.Api.Features.Ltl.SavedViews;

/// <summary>
/// A serializable snapshot of the workbench filter/sort state behind a saved view. The field set
/// mirrors the subset of <see cref="LtlSearchQuery"/> the dispatcher edits directly in the SPA, so
/// a view round-trips losslessly between the grid and storage. Dates are kept as the raw
/// <c>yyyy-MM-dd</c> strings the date inputs produce — this layer stores the dispatcher's intent and
/// never reinterprets it.
///
/// <para>This is a tool-local construct only: applying or saving a view never touches Alvys.</para>
/// </summary>
public sealed class SavedViewFilters
{
    public string? Keyword { get; set; }
    public string? Customer { get; set; }
    public string? OriginState { get; set; }
    public string? OriginCity { get; set; }
    public string? DestinationState { get; set; }
    public string? DestinationCity { get; set; }
    public string? EquipmentType { get; set; }

    /// <summary>Assignment-state filter (null = any).</summary>
    public AssignmentState? Assignment { get; set; }

    public string? PickupFrom { get; set; }
    public string? PickupTo { get; set; }
    public string? DeliveryFrom { get; set; }
    public string? DeliveryTo { get; set; }

    /// <summary>Billing-readiness badge filter (null = any).</summary>
    public BillingBadge? BillingBadge { get; set; }

    /// <summary>Workflow-stage filter (null = any).</summary>
    public WorkflowStage? Stage { get; set; }

    public bool LtlOnly { get; set; }
    public bool ReadyToBill { get; set; }
    public bool MissingBillingData { get; set; }
    public bool ExceptionsOnly { get; set; }
    public bool BlockedOnly { get; set; }

    public LtlSortField Sort { get; set; } = LtlSortField.PickupDate;
    public bool SortDescending { get; set; }

    /// <summary>Deep copy so stored views are not mutated by a later edit of the same instance.</summary>
    public SavedViewFilters Clone() => (SavedViewFilters)MemberwiseClone();
}

/// <summary>
/// A named, persisted workbench view. Built-in presets (<see cref="IsBuiltIn"/> = true) are shipped
/// by the tool and shared by every dispatcher; user views are owned by the dispatcher who created
/// them (<see cref="OwnerId"/>). Nothing here is written back to Alvys.
/// </summary>
public sealed class SavedView
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required SavedViewFilters Filters { get; init; }

    /// <summary>True for shipped enterprise presets, which cannot be edited or deleted.</summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>The dispatcher who owns a user view; null for shared built-in presets.</summary>
    public string? OwnerId { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>Create/update payload for a dispatcher saved view.</summary>
public sealed class SavedViewRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public SavedViewFilters Filters { get; set; } = new();
}

/// <summary>
/// The saved-view collection for a dispatcher: shared built-in presets and the dispatcher's own
/// views, kept as distinct lists so the UI can clearly separate the two without guessing.
/// </summary>
public sealed class SavedViewCollection
{
    public IReadOnlyList<SavedView> Presets { get; init; } = [];
    public IReadOnlyList<SavedView> Views { get; init; } = [];
}

/// <summary>
/// Persists dispatcher saved views. The seam is intentionally abstracted (mirroring the assignment
/// audit store) so a production deployment can swap the in-memory default for a durable, queryable
/// store (e.g. EF Core via <c>AppDbContext</c>) without touching the controller. All operations are
/// scoped to an owner so one dispatcher never sees or mutates another's views.
/// </summary>
public interface ISavedViewStore
{
    IReadOnlyList<SavedView> ListForOwner(string ownerId);
    SavedView? Get(string ownerId, string id);
    SavedView Create(string ownerId, SavedViewRequest request);
    SavedView? Update(string ownerId, string id, SavedViewRequest request);
    bool Delete(string ownerId, string id);
}

/// <summary>
/// Thread-safe in-memory <see cref="ISavedViewStore"/>. Suitable for this slice and local/UAT;
/// <b>not durable across restarts</b>. Swap for a persistent store for production (the
/// <see cref="SavedView"/> shape maps cleanly onto an EF Core entity).
/// </summary>
public sealed class InMemorySavedViewStore : ISavedViewStore
{
    private readonly ConcurrentDictionary<string, List<SavedView>> _byOwner =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly TimeProvider _clock;

    public InMemorySavedViewStore(TimeProvider clock) => _clock = clock;

    public IReadOnlyList<SavedView> ListForOwner(string ownerId)
    {
        lock (_gate)
        {
            return _byOwner.TryGetValue(ownerId, out var list)
                ? list.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToArray()
                : [];
        }
    }

    public SavedView? Get(string ownerId, string id)
    {
        lock (_gate)
        {
            return _byOwner.TryGetValue(ownerId, out var list)
                ? list.FirstOrDefault(v => v.Id == id)
                : null;
        }
    }

    public SavedView Create(string ownerId, SavedViewRequest request)
    {
        var now = _clock.GetUtcNow();
        var view = new SavedView
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = request.Name!.Trim(),
            Description = NormalizeDescription(request.Description),
            Filters = (request.Filters ?? new SavedViewFilters()).Clone(),
            IsBuiltIn = false,
            OwnerId = ownerId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        lock (_gate)
        {
            var list = _byOwner.GetOrAdd(ownerId, _ => []);
            list.Add(view);
        }

        return view;
    }

    public SavedView? Update(string ownerId, string id, SavedViewRequest request)
    {
        lock (_gate)
        {
            if (!_byOwner.TryGetValue(ownerId, out var list)) return null;
            var index = list.FindIndex(v => v.Id == id);
            if (index < 0) return null;

            var existing = list[index];
            var updated = new SavedView
            {
                Id = existing.Id,
                Name = request.Name!.Trim(),
                Description = NormalizeDescription(request.Description),
                Filters = (request.Filters ?? new SavedViewFilters()).Clone(),
                IsBuiltIn = false,
                OwnerId = ownerId,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = _clock.GetUtcNow(),
            };
            list[index] = updated;
            return updated;
        }
    }

    public bool Delete(string ownerId, string id)
    {
        lock (_gate)
        {
            return _byOwner.TryGetValue(ownerId, out var list) && list.RemoveAll(v => v.Id == id) > 0;
        }
    }

    private static string? NormalizeDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();
}
