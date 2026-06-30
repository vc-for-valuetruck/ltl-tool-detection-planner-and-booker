using LtlTool.Api.Data;
using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.SavedViews;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the durable EF Core saved-view store against a real (file-backed SQLite) database:
/// CRUD round-trips, per-owner isolation, filter snapshotting, and — critically — that user views
/// survive being read back through a brand-new <see cref="AppDbContext"/>/store instance, proving the
/// data is persisted rather than held in process memory.
/// </summary>
public sealed class EfSavedViewStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ltl-saved-views-{Guid.NewGuid():n}.db");
    private readonly string _connectionString;

    public EfSavedViewStoreTests()
    {
        _connectionString = $"Data Source={_dbPath}";
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options);

    private EfSavedViewStore NewStore(AppDbContext ctx) => new(ctx, LtlTestFactory.Clock());

    private static SavedViewRequest Request(string name, Action<SavedViewFilters>? configure = null)
    {
        var filters = new SavedViewFilters { Stage = WorkflowStage.Match };
        configure?.Invoke(filters);
        return new SavedViewRequest { Name = name, Description = "desc", Filters = filters };
    }

    [Fact]
    public void Create_then_list_returns_the_view_for_its_owner()
    {
        using var ctx = NewContext();
        var store = NewStore(ctx);

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
    public void User_views_survive_a_new_store_and_context_instance()
    {
        string id;

        // Write through one context, then dispose it entirely.
        using (var writeCtx = NewContext())
        {
            id = NewStore(writeCtx).Create("dispatcher@valuetruck.com",
                Request("Persisted", f => { f.Stage = WorkflowStage.Bill; f.ReadyToBill = true; })).Id;
        }

        // Read back through a brand-new context + store: data must come from the database, not memory.
        using var readCtx = NewContext();
        var reloaded = NewStore(readCtx).Get("dispatcher@valuetruck.com", id);

        Assert.NotNull(reloaded);
        Assert.Equal("Persisted", reloaded!.Name);
        Assert.Equal(WorkflowStage.Bill, reloaded.Filters.Stage);
        Assert.True(reloaded.Filters.ReadyToBill);
        Assert.Equal(LtlTestFactory.Now, reloaded.CreatedAt);
    }

    [Fact]
    public void Views_are_isolated_per_owner_across_instances()
    {
        using (var ctx = NewContext())
        {
            NewStore(ctx).Create("alice@valuetruck.com", Request("Alice view"));
        }

        using var readCtx = NewContext();
        var store = NewStore(readCtx);

        Assert.Empty(store.ListForOwner("bob@valuetruck.com"));
        var aliceId = store.ListForOwner("alice@valuetruck.com")[0].Id;
        Assert.Null(store.Get("bob@valuetruck.com", aliceId));
    }

    [Fact]
    public void Update_replaces_fields_and_preserves_created_at()
    {
        using var ctx = NewContext();
        var store = NewStore(ctx);
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
        using var ctx = NewContext();
        var store = NewStore(ctx);
        var created = store.Create("owner@valuetruck.com", Request("Mine"));

        Assert.Null(store.Update("owner@valuetruck.com", "missing", Request("x")));
        Assert.Null(store.Update("intruder@valuetruck.com", created.Id, Request("x")));
    }

    [Fact]
    public void Delete_removes_only_the_owners_view()
    {
        using var ctx = NewContext();
        var store = NewStore(ctx);
        var created = store.Create("owner@valuetruck.com", Request("Mine"));

        Assert.False(store.Delete("intruder@valuetruck.com", created.Id));
        Assert.True(store.Delete("owner@valuetruck.com", created.Id));
        Assert.False(store.Delete("owner@valuetruck.com", created.Id));
        Assert.Empty(store.ListForOwner("owner@valuetruck.com"));
    }

    [Fact]
    public void Stored_filters_are_snapshotted_not_aliased()
    {
        using var ctx = NewContext();
        var store = NewStore(ctx);
        var request = Request("Snapshot");
        var created = store.Create("d@valuetruck.com", request);

        // Mutating the caller's instance after create must not change the stored view.
        request.Filters.Stage = WorkflowStage.Billed;

        Assert.Equal(WorkflowStage.Match, store.Get("d@valuetruck.com", created.Id)!.Filters.Stage);
    }
}
