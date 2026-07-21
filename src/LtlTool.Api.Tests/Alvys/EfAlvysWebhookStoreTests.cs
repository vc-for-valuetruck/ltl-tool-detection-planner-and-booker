using LtlTool.Api.Data;
using LtlTool.Api.Features.Integrations.Alvys.Webhooks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

/// <summary>
/// Verifies the durable webhook store against a real (file-backed SQLite) database: a duplicate event id
/// is rejected on insert (at-least-once delivery made idempotent), processing upserts the per-load
/// freshness marker and bumps its change count, failures are recorded with a bounded error, and the recent
/// listing is newest-first.
/// </summary>
public sealed class EfAlvysWebhookStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ltl-alvys-webhooks-{Guid.NewGuid():n}.db");
    private readonly string _connectionString;

    public EfAlvysWebhookStoreTests()
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

    private static AlvysWebhookEvent Event(string id, string? loadNumber = "L-100", DateTimeOffset? receivedAt = null) => new()
    {
        EventId = id,
        EventType = "load.changed",
        Timestamp = 1_770_000_000,
        LoadNumber = loadNumber,
        RawBody = "{\"data\":{\"load\":{\"LoadNumber\":\"L-100\"}}}",
        ReceivedAt = receivedAt ?? DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Duplicate_event_id_is_rejected_on_insert()
    {
        using (var ctx = NewContext())
            Assert.True(new EfAlvysWebhookStore(ctx).TryInsertReceived(Event("evt-1")));

        using (var ctx = NewContext())
            Assert.False(new EfAlvysWebhookStore(ctx).TryInsertReceived(Event("evt-1")));

        using (var ctx = NewContext())
            Assert.Equal(1, new EfAlvysWebhookStore(ctx).Count());
    }

    [Fact]
    public void MarkProcessed_upserts_freshness_and_bumps_change_count()
    {
        using (var ctx = NewContext())
        {
            var store = new EfAlvysWebhookStore(ctx);
            store.TryInsertReceived(Event("evt-a", "L-500"));
            store.TryInsertReceived(Event("evt-b", "L-500"));
        }

        var t1 = DateTimeOffset.UtcNow;
        using (var ctx = NewContext())
            new EfAlvysWebhookStore(ctx).MarkProcessed("evt-a", "L-500", "load.changed", t1);

        var t2 = t1.AddMinutes(5);
        using (var ctx = NewContext())
            new EfAlvysWebhookStore(ctx).MarkProcessed("evt-b", "L-500", "load.status.changed", t2);

        using (var ctx = NewContext())
        {
            var store = new EfAlvysWebhookStore(ctx);
            var freshness = store.GetFreshness("L-500");
            Assert.NotNull(freshness);
            Assert.Equal(2, freshness!.ChangeCount);
            Assert.Equal("load.status.changed", freshness.LastEventType);
            Assert.Equal("evt-b", freshness.LastEventId);
            Assert.Equal(AlvysWebhookProcessingState.Processed, store.Get("evt-a")!.ProcessingState);
        }
    }

    [Fact]
    public void MarkFailed_records_bounded_error()
    {
        using (var ctx = NewContext())
            new EfAlvysWebhookStore(ctx).TryInsertReceived(Event("evt-f"));

        var longError = new string('x', 5000);
        using (var ctx = NewContext())
            new EfAlvysWebhookStore(ctx).MarkFailed("evt-f", longError, DateTimeOffset.UtcNow);

        using (var ctx = NewContext())
        {
            var evt = new EfAlvysWebhookStore(ctx).Get("evt-f");
            Assert.Equal(AlvysWebhookProcessingState.Failed, evt!.ProcessingState);
            Assert.Equal(2048, evt.ProcessingError!.Length);
        }
    }

    [Fact]
    public void ListRecent_is_newest_first()
    {
        var baseTime = DateTimeOffset.UtcNow.AddHours(-1);
        using (var ctx = NewContext())
        {
            var store = new EfAlvysWebhookStore(ctx);
            store.TryInsertReceived(Event("old", receivedAt: baseTime));
            store.TryInsertReceived(Event("mid", receivedAt: baseTime.AddMinutes(10)));
            store.TryInsertReceived(Event("new", receivedAt: baseTime.AddMinutes(20)));
        }

        using (var ctx = NewContext())
        {
            var recent = new EfAlvysWebhookStore(ctx).ListRecent(2);
            Assert.Equal(new[] { "new", "mid" }, recent.Select(e => e.EventId).ToArray());
        }
    }

    [Fact]
    public void No_freshness_marker_when_load_number_absent()
    {
        using (var ctx = NewContext())
            new EfAlvysWebhookStore(ctx).TryInsertReceived(Event("evt-nold", loadNumber: null));

        using (var ctx = NewContext())
            new EfAlvysWebhookStore(ctx).MarkProcessed("evt-nold", null, "load.changed", DateTimeOffset.UtcNow);

        using (var ctx = NewContext())
            Assert.Null(new EfAlvysWebhookStore(ctx).GetFreshness("L-100"));
    }
}
