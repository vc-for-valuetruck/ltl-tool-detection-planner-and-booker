using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.SavedViews;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the in-memory saved-view store: CRUD round-trips, per-owner isolation and that stored
/// filters are snapshotted (not aliased to the caller's instance).
/// </summary>
public sealed class SavedViewStoreTests
{
    private static InMemorySavedViewStore NewStore() => new(LtlTestFactory.Clock());

    private static SavedViewRequest Request(string name, Action<SavedViewFilters>? configure = null)
    {
        var filters = new SavedViewFilters { Stage = WorkflowStage.Match };
        configure?.Invoke(filters);
        return new SavedViewRequest { Name = name, Description = "desc", Filters = filters };
    }

    [Fact]
    public void Create_then_list_returns_the_view_for_its_owner()
    {
        var store = NewStore();

        var created = store.Create("dispatcher@valuetruck.com", Request("Hot lanes"));

        Assert.False(created.IsBuiltIn);
        Assert.Equal("dispatcher@valuetruck.com", created.OwnerId);
        Assert.Equal("Hot lanes", created.Name);
        Assert.Equal(WorkflowStage.Match, created.Filters.Stage);
        Assert.Equal(LtlTestFactory.Now, created.CreatedAt);

        var listed = store.ListForOwner("dispatcher@valuetruck.com");
        Assert.Single(listed);
        Assert.Equal(created.Id, listed[0].Id);
    }

    [Fact]
    public void Views_are_isolated_per_owner()
    {
        var store = NewStore();
        store.Create("alice@valuetruck.com", Request("Alice view"));

        Assert.Empty(store.ListForOwner("bob@valuetruck.com"));
        Assert.Null(store.Get("bob@valuetruck.com", store.ListForOwner("alice@valuetruck.com")[0].Id));
    }

    [Fact]
    public void Update_replaces_fields_and_preserves_created_at()
    {
        var store = NewStore();
        var created = store.Create("d@valuetruck.com", Request("Original"));

        var updated = store.Update("d@valuetruck.com", created.Id,
            Request("Renamed", f => { f.Stage = WorkflowStage.Bill; f.ReadyToBill = true; }));

        Assert.NotNull(updated);
        Assert.Equal("Renamed", updated!.Name);
        Assert.Equal(WorkflowStage.Bill, updated.Filters.Stage);
        Assert.True(updated.Filters.ReadyToBill);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
        Assert.Equal(created.Id, updated.Id);
    }

    [Fact]
    public void Update_returns_null_for_unknown_or_foreign_view()
    {
        var store = NewStore();
        var created = store.Create("owner@valuetruck.com", Request("Mine"));

        Assert.Null(store.Update("owner@valuetruck.com", "missing", Request("x")));
        Assert.Null(store.Update("intruder@valuetruck.com", created.Id, Request("x")));
    }

    [Fact]
    public void Delete_removes_only_the_owners_view()
    {
        var store = NewStore();
        var created = store.Create("owner@valuetruck.com", Request("Mine"));

        Assert.False(store.Delete("intruder@valuetruck.com", created.Id));
        Assert.True(store.Delete("owner@valuetruck.com", created.Id));
        Assert.False(store.Delete("owner@valuetruck.com", created.Id));
        Assert.Empty(store.ListForOwner("owner@valuetruck.com"));
    }

    [Fact]
    public void Stored_filters_are_snapshotted_not_aliased()
    {
        var store = NewStore();
        var request = Request("Snapshot");
        var created = store.Create("d@valuetruck.com", request);

        // Mutating the caller's instance after create must not change the stored view.
        request.Filters.Stage = WorkflowStage.Billed;

        Assert.Equal(WorkflowStage.Match, store.Get("d@valuetruck.com", created.Id)!.Filters.Stage);
    }
}
