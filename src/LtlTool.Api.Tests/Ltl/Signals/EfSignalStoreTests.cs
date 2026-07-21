using LtlTool.Api.Data;
using LtlTool.Api.Features.Ltl.Signals;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Signals;

/// <summary>
/// Verifies the durable EF Core signal store against a real (file-backed SQLite) database: a batch
/// survives being read back through a brand-new context/store instance, the query filters return only
/// matching rows, and accept/reject transitions persist. Internal data — Alvys is never involved.
/// </summary>
public sealed class EfSignalStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ltl-signals-{Guid.NewGuid():n}.db");
    private readonly string _connectionString;

    public EfSignalStoreTests()
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

    private static SignalRecord Record(
        string id, SignalType type = SignalType.AccessorialEvidence, string? load = null,
        string sourceType = "note", SignalStatus status = SignalStatus.Pending,
        DateTimeOffset? created = null) => new()
    {
        Id = id,
        SourceType = sourceType,
        SourceId = "src-1",
        SignalType = type.ToString(),
        Confidence = 1.0,
        EvidenceQuote = "Driver was detained 3 hours.",
        SuggestedSurface = LtlSurface.BillingWorklistBadge.ToString(),
        Summary = "detention evidence",
        LoadNumber = load,
        Status = status.ToString(),
        IngestedBy = "dispatcher@vt.com",
        CreatedAt = created ?? LtlTestFactory.Now,
    };

    [Fact]
    public void Batch_survives_a_new_store_and_context_instance()
    {
        using (var writeCtx = NewContext())
        {
            new EfSignalStore(writeCtx).AddBatch([Record("s1", load: "L100"), Record("s2", load: "L200")]);
        }

        using var readCtx = NewContext();
        var reloaded = new EfSignalStore(readCtx).Get("s1");

        Assert.NotNull(reloaded);
        Assert.Equal("L100", reloaded!.LoadNumber);
        Assert.Equal(SignalType.AccessorialEvidence.ToString(), reloaded.SignalType);
        Assert.Equal("Driver was detained 3 hours.", reloaded.EvidenceQuote);
    }

    [Fact]
    public void Empty_batch_is_a_no_op()
    {
        using var ctx = NewContext();
        var store = new EfSignalStore(ctx);
        store.AddBatch([]);
        Assert.Empty(store.Query(new SignalQuery()));
    }

    [Fact]
    public void Query_filters_by_status_source_type_and_load()
    {
        using (var ctx = NewContext())
        {
            var store = new EfSignalStore(ctx);
            store.AddBatch(
            [
                Record("s1", load: "L100", status: SignalStatus.Pending),
                Record("s2", load: "L200", status: SignalStatus.Accepted, sourceType: "email"),
                Record("s3", load: "L100", status: SignalStatus.Rejected),
            ]);
        }

        using var readCtx = NewContext();
        var store2 = new EfSignalStore(readCtx);

        Assert.Single(store2.Query(new SignalQuery(Status: SignalStatus.Accepted)));
        Assert.Single(store2.Query(new SignalQuery(SourceType: "email")));
        Assert.Equal(2, store2.Query(new SignalQuery(LoadNumber: "L100")).Count);
        Assert.Empty(store2.Query(new SignalQuery(LoadNumber: "L999")));
    }

    [Fact]
    public void Query_orders_newest_first_and_honors_max()
    {
        using (var ctx = NewContext())
        {
            var store = new EfSignalStore(ctx);
            store.AddBatch(
            [
                Record("old", created: LtlTestFactory.Now),
                Record("new", created: LtlTestFactory.Now.AddHours(1)),
            ]);
        }

        using var readCtx = NewContext();
        var store2 = new EfSignalStore(readCtx);

        var all = store2.Query(new SignalQuery());
        Assert.Equal("new", all[0].Id);

        var capped = store2.Query(new SignalQuery(Max: 1));
        Assert.Single(capped);
        Assert.Equal("new", capped[0].Id);
    }

    [Fact]
    public void UpdateStatus_transitions_and_stamps_the_decider()
    {
        using (var ctx = NewContext())
        {
            new EfSignalStore(ctx).AddBatch([Record("s1")]);
        }

        var decidedAt = LtlTestFactory.Now.AddHours(2);
        using (var ctx = NewContext())
        {
            var updated = new EfSignalStore(ctx).UpdateStatus("s1", SignalStatus.Accepted, "dispatcher@vt.com", decidedAt);
            Assert.NotNull(updated);
            Assert.Equal(SignalStatus.Accepted.ToString(), updated!.Status);
        }

        using var readCtx = NewContext();
        var reloaded = new EfSignalStore(readCtx).Get("s1");
        Assert.Equal(SignalStatus.Accepted.ToString(), reloaded!.Status);
        Assert.Equal("dispatcher@vt.com", reloaded.DecidedBy);
        Assert.Equal(decidedAt, reloaded.DecidedAt);
    }

    [Fact]
    public void UpdateStatus_returns_null_for_unknown_id()
    {
        using var ctx = NewContext();
        Assert.Null(new EfSignalStore(ctx).UpdateStatus("nope", SignalStatus.Rejected, "u@vt.com", LtlTestFactory.Now));
    }
}
