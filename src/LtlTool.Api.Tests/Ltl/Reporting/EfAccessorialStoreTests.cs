using LtlTool.Api.Data;
using LtlTool.Api.Features.Ltl.Reporting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Reporting;

/// <summary>
/// Verifies the durable EF Core accessorial-history store against a real (file-backed SQLite)
/// database: content-keyed capture is idempotent (an unchanged line advances LastSeenAt rather than
/// duplicating), a changed line inserts a new row (history, not overwrite), and records survive
/// being read back through a brand-new <see cref="AppDbContext"/>/store instance.
/// </summary>
public sealed class EfAccessorialStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ltl-accessorials-{Guid.NewGuid():n}.db");
    private readonly string _connectionString;
    private static readonly DateTimeOffset T1 = new(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = T1.AddHours(2);

    public EfAccessorialStoreTests()
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

    [Fact]
    public void Capture_of_the_same_line_twice_advances_LastSeenAt_without_duplicating()
    {
        using (var ctx = NewContext())
        {
            var store = new EfAccessorialStore(ctx);
            var line = new ObservedAccessorialLine(AccessorialEntityType.Customer, "Detention", "2h at pickup", 150m);
            store.Capture("load-1", "L-1001", null, line, T1);
            store.Capture("load-1", "L-1001", null, line, T2);
        }

        using (var ctx = NewContext())
        {
            var store = new EfAccessorialStore(ctx);
            var rows = store.List("load-1", null, 50);
            var row = Assert.Single(rows);
            Assert.Equal(T1, row.FirstSeenAt);
            Assert.Equal(T2, row.LastSeenAt);
        }
    }

    [Fact]
    public void Capture_of_a_changed_amount_inserts_a_new_row_instead_of_overwriting()
    {
        using (var ctx = NewContext())
        {
            var store = new EfAccessorialStore(ctx);
            store.Capture(
                "load-1", "L-1001", null,
                new ObservedAccessorialLine(AccessorialEntityType.Customer, "Detention", "2h", 150m), T1);
            store.Capture(
                "load-1", "L-1001", null,
                new ObservedAccessorialLine(AccessorialEntityType.Customer, "Detention", "2h", 200m), T2);
        }

        using (var ctx = NewContext())
        {
            var store = new EfAccessorialStore(ctx);
            var rows = store.List("load-1", null, 50);
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.Amount == 150m);
            Assert.Contains(rows, r => r.Amount == 200m);
        }
    }

    [Fact]
    public void List_filters_by_entity_type()
    {
        using (var ctx = NewContext())
        {
            var store = new EfAccessorialStore(ctx);
            store.Capture(
                "load-1", "L-1001", null,
                new ObservedAccessorialLine(AccessorialEntityType.Customer, "Detention", null, 150m), T1);
            store.Capture(
                "load-1", "L-1001", "trip-1",
                new ObservedAccessorialLine(AccessorialEntityType.Carrier, "Lumper", null, 75m), T1);
        }

        using (var ctx = NewContext())
        {
            var store = new EfAccessorialStore(ctx);
            var carrierRows = store.List("load-1", AccessorialEntityType.Carrier, 50);
            var row = Assert.Single(carrierRows);
            Assert.Equal("Lumper", row.Type);
        }
    }
}
