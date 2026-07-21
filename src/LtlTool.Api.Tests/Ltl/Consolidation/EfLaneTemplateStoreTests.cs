using LtlTool.Api.Data;
using LtlTool.Api.Features.Ltl.Consolidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Consolidation;

/// <summary>
/// Verifies the durable EF Core lane-template store (Phase 2.5) against a real (file-backed SQLite)
/// database: templates survive being read back through a brand-new <see cref="AppDbContext"/>/store
/// instance, and the corridor/customer filters plus newest-first ordering behave. Internal Value
/// Truck data — Alvys is never involved.
/// </summary>
public sealed class EfLaneTemplateStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ltl-lane-templates-{Guid.NewGuid():n}.db");
    private readonly string _connectionString;

    public EfLaneTemplateStoreTests()
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

    private static LaneTemplateRecord Record(
        string id,
        string corridor = "LAREDO_TO_DALLAS",
        string? customer = null,
        int? cadence = null,
        DateTimeOffset? created = null) => new()
    {
        Id = id,
        Name = $"Template {id}",
        CorridorCode = corridor,
        CustomerName = customer,
        OriginLabel = "Laredo, TX",
        DestinationLabel = "Dallas, TX",
        CadenceDays = cadence,
        Notes = "note",
        CreatedBy = "dispatch@valuetruck.com",
        CreatedAt = created ?? DateTimeOffset.UtcNow,
        UpdatedAt = created ?? DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Records_survive_a_new_store_and_context_instance()
    {
        using (var writeCtx = NewContext())
        {
            new EfLaneTemplateStore(writeCtx).Add(Record("t1", customer: "Masonite", cadence: 7));
        }

        using var readCtx = NewContext();
        var reloaded = new EfLaneTemplateStore(readCtx).Get("t1");

        Assert.NotNull(reloaded);
        Assert.Equal("LAREDO_TO_DALLAS", reloaded!.CorridorCode);
        Assert.Equal("Masonite", reloaded.CustomerName);
        Assert.Equal(7, reloaded.CadenceDays);
        Assert.Equal("Laredo, TX", reloaded.OriginLabel);
    }

    [Fact]
    public void Query_filters_by_corridor_and_customer()
    {
        using (var ctx = NewContext())
        {
            var store = new EfLaneTemplateStore(ctx);
            store.Add(Record("t1", corridor: "LAREDO_TO_DALLAS", customer: "Masonite"));
            store.Add(Record("t2", corridor: "LAREDO_TO_DALLAS", customer: "Acme"));
            store.Add(Record("t3", corridor: "EL_PASO_TO_DALLAS", customer: "Masonite"));
        }

        using var readCtx = NewContext();
        var store2 = new EfLaneTemplateStore(readCtx);

        Assert.Equal(2, store2.Query(new LaneTemplateQuery(CorridorCode: "LAREDO_TO_DALLAS")).Count);
        Assert.Equal(2, store2.Query(new LaneTemplateQuery(CustomerName: "Masonite")).Count);
        Assert.Single(store2.Query(new LaneTemplateQuery(
            CorridorCode: "LAREDO_TO_DALLAS", CustomerName: "Masonite")));
        Assert.Empty(store2.Query(new LaneTemplateQuery(CorridorCode: "NOPE")));
    }

    [Fact]
    public void Query_orders_newest_first_and_honors_max()
    {
        var now = DateTimeOffset.UtcNow;
        using (var ctx = NewContext())
        {
            var store = new EfLaneTemplateStore(ctx);
            store.Add(Record("old", created: now));
            store.Add(Record("new", created: now.AddHours(1)));
        }

        using var readCtx = NewContext();
        var store2 = new EfLaneTemplateStore(readCtx);

        var all = store2.Query(new LaneTemplateQuery());
        Assert.Equal("new", all[0].Id);

        var capped = store2.Query(new LaneTemplateQuery(Max: 1));
        Assert.Single(capped);
        Assert.Equal("new", capped[0].Id);
    }

    [Fact]
    public void Delete_removes_a_row_and_reports_whether_it_matched()
    {
        using (var ctx = NewContext())
        {
            new EfLaneTemplateStore(ctx).Add(Record("t1"));
        }

        using var ctx2 = NewContext();
        var store = new EfLaneTemplateStore(ctx2);
        Assert.True(store.Delete("t1"));
        Assert.False(store.Delete("t1"));
        Assert.Null(store.Get("t1"));
    }
}
