using LtlTool.Api.Data;
using LtlTool.Api.Features.Ltl.Bol;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Bol;

/// <summary>
/// Verifies the durable EF Core BOL-suggestion store against a real (file-backed SQLite) database:
/// suggestions survive being read back through a brand-new context/store instance (persistence, not
/// in-process memory), the load/status filters return only matching rows, and an accept/reject
/// transition stamps who/when. Internal data — Alvys is never involved.
/// </summary>
public sealed class EfBolSuggestionStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ltl-bol-suggestions-{Guid.NewGuid():n}.db");
    private readonly string _connectionString;

    public EfBolSuggestionStoreTests()
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

    private static BolFieldSuggestionRecord Record(
        string id, string load = "L-1", BolField field = BolField.PalletCount,
        BolSuggestionStatus status = BolSuggestionStatus.Pending, DateTimeOffset? created = null) => new()
    {
        Id = id,
        LoadNumber = load,
        DocumentId = "doc-1",
        DocumentName = "BOL.pdf",
        Field = field.ToString(),
        Value = "12",
        Confidence = 0.9,
        EvidenceQuote = "Pallet count: 12",
        ExtractorName = "deterministic-regex",
        Status = status.ToString(),
        CreatedBy = "dispatch@valuetruck.com",
        CreatedAt = created ?? LtlTestFactory.Now,
    };

    [Fact]
    public void Records_survive_a_new_store_and_context_instance()
    {
        using (var writeCtx = NewContext())
            new EfBolSuggestionStore(writeCtx).AddBatch([Record("s1")]);

        using var readCtx = NewContext();
        var reloaded = new EfBolSuggestionStore(readCtx).Get("s1");

        Assert.NotNull(reloaded);
        Assert.Equal("L-1", reloaded!.LoadNumber);
        Assert.Equal(BolField.PalletCount.ToString(), reloaded.Field);
        Assert.Equal("12", reloaded.Value);
        Assert.Equal(BolSuggestionStatus.Pending.ToString(), reloaded.Status);
    }

    [Fact]
    public void Empty_batch_is_a_no_op()
    {
        using var ctx = NewContext();
        var store = new EfBolSuggestionStore(ctx);

        store.AddBatch([]);

        Assert.Empty(store.Query(new BolSuggestionQuery()));
    }

    [Fact]
    public void Query_filters_by_load_number_and_status()
    {
        using (var ctx = NewContext())
        {
            var store = new EfBolSuggestionStore(ctx);
            store.AddBatch([Record("s1", load: "L-1", status: BolSuggestionStatus.Pending)]);
            store.AddBatch([Record("s2", load: "L-2", status: BolSuggestionStatus.Accepted)]);
        }

        using var readCtx = NewContext();
        var store2 = new EfBolSuggestionStore(readCtx);

        Assert.Single(store2.Query(new BolSuggestionQuery(LoadNumber: "L-1")));
        Assert.Single(store2.Query(new BolSuggestionQuery(Status: BolSuggestionStatus.Accepted)));
        Assert.Empty(store2.Query(new BolSuggestionQuery(LoadNumber: "L-999")));
    }

    [Fact]
    public void Query_orders_newest_first_and_honors_max()
    {
        using (var ctx = NewContext())
        {
            var store = new EfBolSuggestionStore(ctx);
            store.AddBatch([Record("old", created: LtlTestFactory.Now)]);
            store.AddBatch([Record("new", created: LtlTestFactory.Now.AddHours(1))]);
        }

        using var readCtx = NewContext();
        var store2 = new EfBolSuggestionStore(readCtx);

        Assert.Equal("new", store2.Query(new BolSuggestionQuery())[0].Id);

        var capped = store2.Query(new BolSuggestionQuery(Max: 1));
        Assert.Single(capped);
        Assert.Equal("new", capped[0].Id);
    }

    [Fact]
    public void UpdateStatus_stamps_the_decider_and_persists()
    {
        using (var ctx = NewContext())
            new EfBolSuggestionStore(ctx).AddBatch([Record("s1")]);

        using (var ctx = NewContext())
        {
            var updated = new EfBolSuggestionStore(ctx).UpdateStatus(
                "s1", BolSuggestionStatus.Accepted, "ops@valuetruck.com", LtlTestFactory.Now.AddHours(2));
            Assert.NotNull(updated);
            Assert.Equal(BolSuggestionStatus.Accepted.ToString(), updated!.Status);
        }

        using var readCtx = NewContext();
        var reloaded = new EfBolSuggestionStore(readCtx).Get("s1");
        Assert.Equal(BolSuggestionStatus.Accepted.ToString(), reloaded!.Status);
        Assert.Equal("ops@valuetruck.com", reloaded.DecidedBy);
        Assert.NotNull(reloaded.DecidedAt);
    }

    [Fact]
    public void UpdateStatus_returns_null_for_an_unknown_id()
    {
        using var ctx = NewContext();
        Assert.Null(new EfBolSuggestionStore(ctx).UpdateStatus(
            "nope", BolSuggestionStatus.Rejected, "u", LtlTestFactory.Now));
    }
}
