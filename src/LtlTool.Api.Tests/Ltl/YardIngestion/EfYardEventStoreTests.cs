using LtlTool.Api.Data;
using LtlTool.Api.Features.Ltl.YardIngestion;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.YardIngestion;

/// <summary>
/// Verifies the durable EF Core Yard event store against a real (file-backed) SQLite database: an
/// append persists the event and rebuilds the projection in one unit of work, a duplicate dedupe key
/// is an idempotent no-op that returns the existing projection, out-of-order appends converge to the
/// same projection, the query filters return only matching rows, and replay rebuilds deterministically
/// without a new event. Internal data — Alvys is never involved.
/// </summary>
public sealed class EfYardEventStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ltl-yard-{Guid.NewGuid():n}.db");
    private readonly string _connectionString;
    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 8, 0, 0, TimeSpan.Zero);

    public EfYardEventStoreTests()
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

    private static YardEventRecord Event(
        YardEventCategory category,
        string eventId,
        string? payloadJson = null,
        int minutesAfterT0 = 0,
        string sourceRecordId = "R1")
    {
        var system = "yard-control";
        var type = "appointment";
        return new YardEventRecord
        {
            DedupeKey = $"{eventId}:{system}:{type}:{sourceRecordId}",
            EventId = eventId,
            SchemaVersion = 1,
            EventType = category.ToString(),
            Category = category.ToString(),
            AffectsSchedulerInput = YardEventClassifier.AffectsSchedulerInput(category),
            OccurredAt = T0.AddMinutes(minutesAfterT0),
            SourceSystem = system,
            SourceRecordType = type,
            SourceRecordId = sourceRecordId,
            YardLocationId = "YARD-A",
            PayloadJson = payloadJson ?? "{}",
        };
    }

    [Fact]
    public void Append_persists_event_and_projection_and_survives_a_new_context()
    {
        using (var ctx = NewContext())
        {
            var result = new EfYardEventStore(ctx).Append(
                Event(YardEventCategory.Arrival, "e1", "{\"truckId\":\"T-1\"}"), T0);
            Assert.Equal(YardAppendStatus.Accepted, result.Status);
            Assert.NotNull(result.Projection);
        }

        using var readCtx = NewContext();
        var store = new EfYardEventStore(readCtx);
        var projection = store.GetProjection("yard-control", "appointment", "R1");

        Assert.NotNull(projection);
        Assert.True(projection!.SchedulerEligible);
        Assert.Equal("T-1", projection.TruckId);
        Assert.Single(store.ListEvents(100));
    }

    [Fact]
    public void Duplicate_dedupe_key_is_an_idempotent_no_op()
    {
        using (var ctx = NewContext())
        {
            new EfYardEventStore(ctx).Append(Event(YardEventCategory.Arrival, "e1"), T0);
        }

        using (var ctx = NewContext())
        {
            var again = new EfYardEventStore(ctx).Append(Event(YardEventCategory.Arrival, "e1"), T0.AddMinutes(5));
            Assert.Equal(YardAppendStatus.Duplicate, again.Status);
            Assert.NotNull(again.Projection);
        }

        using var readCtx = NewContext();
        // Only one inbox row despite two deliveries of the same dedupe key.
        Assert.Single(new EfYardEventStore(readCtx).ListEvents(100));
    }

    [Fact]
    public void Out_of_order_appends_converge_to_ready()
    {
        using (var ctx = NewContext())
        {
            var store = new EfYardEventStore(ctx);
            // Deliver the release first (occurred later), then the load-complete (occurred earlier).
            store.Append(Event(YardEventCategory.Release, "e-rel", minutesAfterT0: 40), T0.AddMinutes(1));
            store.Append(Event(YardEventCategory.LoadComplete, "e-dock", minutesAfterT0: 30), T0.AddMinutes(2));
        }

        using var readCtx = NewContext();
        var projection = new EfYardEventStore(readCtx).GetProjection("yard-control", "appointment", "R1");

        Assert.NotNull(projection);
        Assert.True(projection!.DockCompleted);
        Assert.True(projection.SecurityCleared);
        Assert.Equal(ScheduleReadiness.Ready.ToString(), projection.Readiness);
        Assert.Equal(2, projection.EventCount);
    }

    [Fact]
    public void Administrative_only_record_persists_events_but_has_no_projection()
    {
        using (var ctx = NewContext())
        {
            new EfYardEventStore(ctx).Append(Event(YardEventCategory.Administrative, "e-admin"), T0);
        }

        using var readCtx = NewContext();
        var store = new EfYardEventStore(readCtx);
        Assert.Null(store.GetProjection("yard-control", "appointment", "R1"));
        Assert.Single(store.ListEvents(100));
    }

    [Fact]
    public void QueryProjections_filters_by_readiness_and_schedulable_only()
    {
        using (var ctx = NewContext())
        {
            var store = new EfYardEventStore(ctx);
            // R1 → Ready. R2 → Cancelled (not schedulable).
            store.Append(Event(YardEventCategory.LoadComplete, "e1", minutesAfterT0: 10, sourceRecordId: "R1"), T0);
            store.Append(Event(YardEventCategory.Release, "e2", minutesAfterT0: 20, sourceRecordId: "R1"), T0);
            store.Append(Event(YardEventCategory.Cancellation, "e3", minutesAfterT0: 10, sourceRecordId: "R2"), T0);
        }

        using var readCtx = NewContext();
        var store2 = new EfYardEventStore(readCtx);

        Assert.Single(store2.QueryProjections(new YardScheduleInputQuery(Readiness: ScheduleReadiness.Ready)));
        var schedulable = store2.QueryProjections(new YardScheduleInputQuery(SchedulableOnly: true));
        Assert.Single(schedulable);
        Assert.Equal("yard-control:appointment:R1", schedulable[0].Id);
    }

    [Fact]
    public void ReplayRecord_rebuilds_the_same_projection_without_a_new_event()
    {
        using (var ctx = NewContext())
        {
            var store = new EfYardEventStore(ctx);
            store.Append(Event(YardEventCategory.LoadComplete, "e1", minutesAfterT0: 10), T0);
            store.Append(Event(YardEventCategory.Release, "e2", minutesAfterT0: 20), T0);
        }

        using var replayCtx = NewContext();
        var replayed = new EfYardEventStore(replayCtx).ReplayRecord("yard-control", "appointment", "R1");

        Assert.NotNull(replayed);
        Assert.Equal(ScheduleReadiness.Ready.ToString(), replayed!.Readiness);
        // Replay did not add an inbox event.
        using var readCtx = NewContext();
        Assert.Equal(2, new EfYardEventStore(readCtx).ListEvents(100).Count);
    }

    // Regression for the live Yard→LTL E2E defect: an older build classified the real producer wire
    // type `yard.truck.arrived` as Unknown, so the row persisted with AffectsSchedulerInput=false and no
    // projection. After the classifier fix, replaying that already-stored row must heal the derived
    // classification off the verbatim EventType and produce the scheduler projection — without a new
    // event and without touching the wire fields.
    [Fact]
    public void ReplayRecord_heals_a_legacy_unknown_arrival_and_projects_it()
    {
        const string sys = "value-truck-yard";
        const string type = "truck-visit";
        const string recordId = "e2e00000-0000-4000-8000-000000000001";

        using (var seedCtx = NewContext())
        {
            // Simulate the row the old build persisted: verbatim wire type, but shelved as Unknown.
            seedCtx.YardEvents.Add(new YardEventRecord
            {
                DedupeKey = $"e2e00000-0000-4000-8000-000000000002:{sys}:{type}:{recordId}",
                EventId = "e2e00000-0000-4000-8000-000000000002",
                SchemaVersion = 1,
                EventType = "yard.truck.arrived",
                Category = YardEventCategory.Unknown.ToString(),
                AffectsSchedulerInput = false,
                OccurredAt = T0,
                ReceivedAt = T0,
                SourceSystem = sys,
                SourceRecordType = type,
                SourceRecordId = recordId,
                YardLocationId = "LAREDO-01",
                PayloadJson = "{\"truckId\":\"T-42\"}",
                Sequence = 1,
            });
            seedCtx.SaveChanges();
        }

        using (var replayCtx = NewContext())
        {
            var projection = new EfYardEventStore(replayCtx).ReplayRecord(sys, type, recordId);

            Assert.NotNull(projection);
            Assert.True(projection!.SchedulerEligible);
            Assert.Equal(ScheduleReadiness.Provisional.ToString(), projection.Readiness);
            Assert.Equal("T-42", projection.TruckId);
        }

        using var readCtx = NewContext();
        var store = new EfYardEventStore(readCtx);
        // Projection now exists...
        Assert.NotNull(store.GetProjection(sys, type, recordId));
        // ...and the stored event's derived classification healed to Arrival (wire type untouched).
        var healed = store.ListEventsForRecord(sys, type, recordId).Single();
        Assert.Equal("yard.truck.arrived", healed.EventType);
        Assert.Equal(YardEventCategory.Arrival.ToString(), healed.Category);
        Assert.True(healed.AffectsSchedulerInput);
        // No new inbox event was created by the replay.
        Assert.Single(store.ListEvents(100));
    }

    // A truly unmodeled type stays Unknown and non-projecting after replay — the heal only ever moves
    // an event out of Unknown, never fabricates a category.
    [Fact]
    public void ReplayRecord_leaves_a_genuinely_unknown_type_unprojected()
    {
        const string sys = "value-truck-yard";
        const string type = "truck-visit";
        const string recordId = "R-unknown";

        using (var seedCtx = NewContext())
        {
            seedCtx.YardEvents.Add(new YardEventRecord
            {
                DedupeKey = $"u1:{sys}:{type}:{recordId}",
                EventId = "u1",
                SchemaVersion = 1,
                EventType = "yard.some.type.we.do.not.model",
                Category = YardEventCategory.Unknown.ToString(),
                AffectsSchedulerInput = false,
                OccurredAt = T0,
                ReceivedAt = T0,
                SourceSystem = sys,
                SourceRecordType = type,
                SourceRecordId = recordId,
                YardLocationId = "LAREDO-01",
                PayloadJson = "{}",
                Sequence = 1,
            });
            seedCtx.SaveChanges();
        }

        using var replayCtx = NewContext();
        var store = new EfYardEventStore(replayCtx);
        Assert.Null(store.ReplayRecord(sys, type, recordId));
        Assert.Equal(YardEventCategory.Unknown.ToString(), store.ListEventsForRecord(sys, type, recordId).Single().Category);
    }

    [Fact]
    public void ListEventsForRecord_orders_oldest_occurrence_first()
    {
        using (var ctx = NewContext())
        {
            var store = new EfYardEventStore(ctx);
            store.Append(Event(YardEventCategory.Release, "e-late", minutesAfterT0: 40), T0);
            store.Append(Event(YardEventCategory.LoadComplete, "e-early", minutesAfterT0: 10), T0);
        }

        using var readCtx = NewContext();
        var events = new EfYardEventStore(readCtx).ListEventsForRecord("yard-control", "appointment", "R1");

        Assert.Equal("e-early", events[0].EventId);
        Assert.Equal("e-late", events[1].EventId);
    }
}
