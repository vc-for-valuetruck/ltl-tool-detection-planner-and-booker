using LtlTool.Api.Data;
using LtlTool.Api.Features.Ltl.Reporting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Reporting;

/// <summary>
/// Verifies the durable EF Core assignment-history store against a real (file-backed SQLite)
/// database: an unchanged snapshot never duplicates a row, a genuinely different snapshot (e.g. a
/// reassigned driver) appends a new row so history is preserved, and records survive being read
/// back through a brand-new <see cref="AppDbContext"/>/store instance.
/// </summary>
public sealed class EfLoadAssignmentStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ltl-assignments-{Guid.NewGuid():n}.db");
    private readonly string _connectionString;
    private static readonly DateTimeOffset T1 = new(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = T1.AddHours(3);

    public EfLoadAssignmentStoreTests()
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

    private static ObservedAssignment Snapshot(string driverId = "D1") => new(
        LoadId: "load-1",
        LoadNumber: "L-1001",
        TripId: "trip-1",
        Status: "Dispatched",
        CarrierId: "C1",
        CarrierName: "Acme Carrier",
        Driver1Id: driverId,
        Driver1Name: "Driver One",
        Driver2Id: null,
        Driver2Name: null,
        OwnerOperatorId: null,
        OwnerOperatorName: null,
        TruckId: "TRK1",
        TrailerId: "TRL1",
        DispatcherId: "US1",
        DispatchedBy: "US1",
        CarrierAssignedAt: T1);

    [Fact]
    public void CaptureIfChanged_of_an_identical_snapshot_does_not_duplicate()
    {
        using (var ctx = NewContext())
        {
            var store = new EfLoadAssignmentStore(ctx);
            store.CaptureIfChanged(Snapshot(), T1);
            store.CaptureIfChanged(Snapshot(), T2);
        }

        using (var ctx = NewContext())
        {
            var store = new EfLoadAssignmentStore(ctx);
            var rows = store.ListForLoad("load-1", 50);
            var row = Assert.Single(rows);
            Assert.Equal(T1, row.CapturedAt);
        }
    }

    [Fact]
    public void CaptureIfChanged_of_a_reassigned_driver_appends_a_new_row()
    {
        using (var ctx = NewContext())
        {
            var store = new EfLoadAssignmentStore(ctx);
            store.CaptureIfChanged(Snapshot(driverId: "D1"), T1);
            store.CaptureIfChanged(Snapshot(driverId: "D2"), T2);
        }

        using (var ctx = NewContext())
        {
            var store = new EfLoadAssignmentStore(ctx);
            var rows = store.ListForLoad("load-1", 50);
            Assert.Equal(2, rows.Count);
            // Newest first.
            Assert.Equal("D2", rows[0].Driver1Id);
            Assert.Equal("D1", rows[1].Driver1Id);
        }
    }

    [Fact]
    public void ListRecent_returns_across_loads_newest_first()
    {
        using (var ctx = NewContext())
        {
            var store = new EfLoadAssignmentStore(ctx);
            store.CaptureIfChanged(Snapshot() with { LoadId = "load-1" }, T1);
            store.CaptureIfChanged(Snapshot() with { LoadId = "load-2" }, T2);
        }

        using (var ctx = NewContext())
        {
            var store = new EfLoadAssignmentStore(ctx);
            var rows = store.ListRecent(50);
            Assert.Equal(2, rows.Count);
            Assert.Equal("load-2", rows[0].LoadId);
            Assert.Equal("load-1", rows[1].LoadId);
        }
    }
}
