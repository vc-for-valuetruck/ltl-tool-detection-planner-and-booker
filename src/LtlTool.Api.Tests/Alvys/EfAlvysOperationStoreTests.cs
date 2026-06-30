using LtlTool.Api.Data;
using LtlTool.Api.Features.Integrations.Alvys.Writeback;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

/// <summary>
/// Verifies the durable EF Core operation store against a real (file-backed SQLite) database:
/// records persist across context instances, history is owner-scoped and newest-first, and the
/// executable idempotency lookup is owner + channel scoped — proving the data is in the database,
/// not process memory.
/// </summary>
public sealed class EfAlvysOperationStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ltl-alvys-ops-{Guid.NewGuid():n}.db");
    private readonly string _connectionString;

    public EfAlvysOperationStoreTests()
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

    private static AlvysOperationRecord Record(
        string owner,
        string id,
        DateTimeOffset createdAt,
        AlvysOperationChannel channel = AlvysOperationChannel.Execute,
        string? idempotencyKey = null) => new()
    {
        Id = id,
        OwnerId = owner,
        OperationCode = "create-load-note",
        Channel = channel,
        ResourceType = "load",
        ResourceId = "L100",
        IdempotencyKey = idempotencyKey,
        PayloadHash = "hash-" + id,
        PayloadPreview = "{\"Description\":\"x\"}",
        Mode = AlvysWritebackMode.Simulation,
        Disposition = AlvysOperationDisposition.Simulated,
        Status = AlvysOperationRecordStatus.Recorded,
        AttemptCount = 1,
        CorrelationId = "corr-" + id,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
    };

    [Fact]
    public void Records_survive_a_new_context_instance()
    {
        var created = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        using (var writeCtx = NewContext())
        {
            new EfAlvysOperationStore(writeCtx).Add(Record("d@vt.com", "r1", created));
        }

        using var readCtx = NewContext();
        var reloaded = new EfAlvysOperationStore(readCtx).Get("d@vt.com", "r1");

        Assert.NotNull(reloaded);
        Assert.Equal("create-load-note", reloaded!.OperationCode);
        Assert.Equal(AlvysOperationChannel.Execute, reloaded.Channel);
        Assert.Equal("L100", reloaded.ResourceId);
    }

    [Fact]
    public void History_is_owner_scoped_and_newest_first()
    {
        using (var ctx = NewContext())
        {
            var store = new EfAlvysOperationStore(ctx);
            store.Add(Record("alice@vt.com", "a1", new DateTimeOffset(2026, 6, 30, 9, 0, 0, TimeSpan.Zero)));
            store.Add(Record("alice@vt.com", "a2", new DateTimeOffset(2026, 6, 30, 11, 0, 0, TimeSpan.Zero)));
            store.Add(Record("bob@vt.com", "b1", new DateTimeOffset(2026, 6, 30, 10, 0, 0, TimeSpan.Zero)));
        }

        using var readCtx = NewContext();
        var aliceStore = new EfAlvysOperationStore(readCtx);

        var alice = aliceStore.ListForOwner("alice@vt.com", 50);
        Assert.Equal(2, alice.Count);
        Assert.Equal("a2", alice[0].Id); // newest first
        Assert.Equal("a1", alice[1].Id);

        Assert.Null(aliceStore.Get("alice@vt.com", "b1"));
        Assert.Single(aliceStore.ListForOwner("bob@vt.com", 50));
    }

    [Fact]
    public void List_honours_the_limit()
    {
        using (var ctx = NewContext())
        {
            var store = new EfAlvysOperationStore(ctx);
            for (var i = 0; i < 5; i++)
                store.Add(Record("d@vt.com", $"r{i}", new DateTimeOffset(2026, 6, 30, i, 0, 0, TimeSpan.Zero)));
        }

        using var readCtx = NewContext();
        Assert.Equal(2, new EfAlvysOperationStore(readCtx).ListForOwner("d@vt.com", 2).Count);
    }

    [Fact]
    public void FindExecutableByKey_is_owner_and_channel_scoped()
    {
        var t = new DateTimeOffset(2026, 6, 30, 8, 0, 0, TimeSpan.Zero);
        using (var ctx = NewContext())
        {
            var store = new EfAlvysOperationStore(ctx);
            store.Add(Record("d@vt.com", "exec", t, AlvysOperationChannel.Execute, "key-1"));
            // A dry-run with the same key must NOT be returned as an executable match.
            store.Add(Record("d@vt.com", "dry", t, AlvysOperationChannel.DryRun, "key-2"));
        }

        using var readCtx = NewContext();
        var readStore = new EfAlvysOperationStore(readCtx);

        Assert.Equal("exec", readStore.FindExecutableByKey("d@vt.com", "key-1")!.Id);
        Assert.Null(readStore.FindExecutableByKey("d@vt.com", "key-2")); // dry-run channel, not executable
        Assert.Null(readStore.FindExecutableByKey("other@vt.com", "key-1")); // foreign owner
    }

    [Fact]
    public void Recorder_idempotency_round_trips_on_the_durable_store()
    {
        var write = Microsoft.Extensions.Options.Options.Create(
            new AlvysWriteOptions { Mode = AlvysWritebackMode.Simulation });
        var alvys = Microsoft.Extensions.Options.Options.Create(new LtlTool.Api.Features.Integrations.Alvys.AlvysOptions());
        var gateway = new AlvysWriteGateway(write, alvys);
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));

        string firstId;
        using (var ctx = NewContext())
        {
            var recorder = new AlvysOperationRecorder(gateway, new EfAlvysOperationStore(ctx), clock, new NoOpAlvysWriteClient());
            var first = recorder.RecordExecute("d@vt.com", "create-load-note",
                new AlvysOperationRequest { LoadNumber = "L1", NoteText = "n", IdempotencyKey = "dur-key" });
            firstId = first.Record!.Id;
        }

        // New context: the replay must find the persisted executable record, not create a second.
        using (var ctx = NewContext())
        {
            var recorder = new AlvysOperationRecorder(gateway, new EfAlvysOperationStore(ctx), clock, new NoOpAlvysWriteClient());
            var replay = recorder.RecordExecute("d@vt.com", "create-load-note",
                new AlvysOperationRequest { LoadNumber = "L1", NoteText = "n", IdempotencyKey = "dur-key" });

            Assert.Equal(AlvysRecordDisposition.DuplicateReplay, replay.Disposition);
            Assert.Equal(firstId, replay.Record!.Id);
        }

        using (var ctx = NewContext())
        {
            Assert.Single(new EfAlvysOperationStore(ctx).ListForOwner("d@vt.com", 50));
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
