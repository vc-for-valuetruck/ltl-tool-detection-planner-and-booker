using LtlTool.Api.Data;
using LtlTool.Api.Features.Ltl.YardArtifacts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the durable EF Core yard-artifact store against a real (file-backed SQLite) database:
/// records survive being read back through a brand-new <see cref="AppDbContext"/>/store instance
/// (proving persistence, not in-process memory), and the equipment/load/yard query filters return
/// only the matching artifacts. Internal data — Alvys is never involved.
/// </summary>
public sealed class EfYardArtifactStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ltl-yard-artifacts-{Guid.NewGuid():n}.db");
    private readonly string _connectionString;

    public EfYardArtifactStoreTests()
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

    private static YardArtifactRecord Record(
        string id, string yard = "LAREDO", string? truck = null, string? trailer = null,
        string? load = null, DateTimeOffset? created = null) => new()
    {
        Id = id,
        Yard = yard,
        TruckUnit = truck,
        TrailerUnit = trailer,
        LoadNumber = load,
        SubmittedBy = "dock@valuetruck.com",
        CapturedAt = LtlTestFactory.Now,
        CreatedAt = created ?? LtlTestFactory.Now,
        Status = YardInspectionStatus.Passed,
        PassedItems = 3,
        FailedItems = 0,
        NaItems = 1,
        VerifiedPalletCount = 10,
        VerifiedLengthInches = 48,
        VerifiedWidthInches = 40,
        VerifiedHeightInches = 60,
        InspectionJson = "{}",
        FilesJson = "[]",
    };

    [Fact]
    public void Records_survive_a_new_store_and_context_instance()
    {
        using (var writeCtx = NewContext())
        {
            new EfYardArtifactStore(writeCtx).Add(Record("a1", truck: "T1", load: "L100"));
        }

        using var readCtx = NewContext();
        var reloaded = new EfYardArtifactStore(readCtx).Get("a1");

        Assert.NotNull(reloaded);
        Assert.Equal("LAREDO", reloaded!.Yard);
        Assert.Equal("T1", reloaded.TruckUnit);
        Assert.Equal("L100", reloaded.LoadNumber);
        Assert.Equal(YardInspectionStatus.Passed, reloaded.Status);
        Assert.Equal(10, reloaded.VerifiedPalletCount);
    }

    [Fact]
    public void Query_filters_by_load_truck_trailer_and_yard()
    {
        using (var ctx = NewContext())
        {
            var store = new EfYardArtifactStore(ctx);
            store.Add(Record("a1", truck: "T1", load: "L100"));
            store.Add(Record("a2", trailer: "TR9", load: "L200"));
            store.Add(Record("a3", yard: "DALLAS", truck: "T5"));
        }

        using var readCtx = NewContext();
        var store2 = new EfYardArtifactStore(readCtx);

        Assert.Single(store2.Query(new YardArtifactQuery(LoadNumber: "L100")));
        Assert.Single(store2.Query(new YardArtifactQuery(TruckUnit: "T1")));
        Assert.Single(store2.Query(new YardArtifactQuery(TrailerUnit: "TR9")));
        Assert.Single(store2.Query(new YardArtifactQuery(Yard: "DALLAS")));
        Assert.Empty(store2.Query(new YardArtifactQuery(LoadNumber: "L999")));
    }

    [Fact]
    public void Query_orders_newest_first_and_honors_max()
    {
        using (var ctx = NewContext())
        {
            var store = new EfYardArtifactStore(ctx);
            store.Add(Record("old", truck: "T1", created: LtlTestFactory.Now));
            store.Add(Record("new", truck: "T1", created: LtlTestFactory.Now.AddHours(1)));
        }

        using var readCtx = NewContext();
        var store2 = new EfYardArtifactStore(readCtx);

        var all = store2.Query(new YardArtifactQuery(TruckUnit: "T1"));
        Assert.Equal("new", all[0].Id);

        var capped = store2.Query(new YardArtifactQuery(TruckUnit: "T1", Max: 1));
        Assert.Single(capped);
        Assert.Equal("new", capped[0].Id);
    }
}
