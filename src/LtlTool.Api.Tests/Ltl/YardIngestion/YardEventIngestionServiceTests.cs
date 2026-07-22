using System.Text.Json;
using LtlTool.Api.Data;
using LtlTool.Api.Features.Ltl.YardIngestion;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.YardIngestion;

/// <summary>
/// End-to-end tests for the ingestion service over a real SQLite-backed store: contract validation
/// (missing fields, wrong schema version, wrong source system → 400/Invalid), first-delivery accept
/// (202-shaped Accepted), idempotent duplicate (200-shaped Duplicate, no second projection), and the
/// administrative exclusion (persisted but no scheduler projection).
/// </summary>
public sealed class YardEventIngestionServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ltl-yard-ingest-{Guid.NewGuid():n}.db");
    private readonly string _connectionString;
    private readonly ServiceProvider _metricsProvider;
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    public YardEventIngestionServiceTests()
    {
        _connectionString = $"Data Source={_dbPath}";
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
        _metricsProvider = new ServiceCollection().AddMetrics().BuildServiceProvider();
    }

    public void Dispose()
    {
        _metricsProvider.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options);

    private YardEventIngestionService NewService(AppDbContext ctx, YardIngestionOptions? options = null) =>
        new(
            new EfYardEventStore(ctx),
            Options.Create(options ?? new YardIngestionOptions()),
            new YardIngestionMetrics(_metricsProvider.GetRequiredService<IMeterFactory>()),
            new FixedTimeProvider(Now),
            NullLogger<YardEventIngestionService>.Instance);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static YardEventEnvelope ValidEnvelope(
        Guid? eventId = null,
        string eventType = "arrival",
        string sourceSystem = "yard-control",
        int schemaVersion = 1) => new()
    {
        EventId = eventId ?? Guid.NewGuid(),
        SchemaVersion = schemaVersion,
        EventType = eventType,
        OccurredAt = Now,
        SourceSystem = sourceSystem,
        SourceRecordType = "appointment",
        SourceRecordId = "R1",
        YardLocationId = "YARD-A",
        Payload = Json("{\"truckId\":\"T-1\"}"),
    };

    [Fact]
    public void Null_envelope_is_rejected()
    {
        using var ctx = NewContext();
        var outcome = NewService(ctx).Ingest(null);
        Assert.Equal(YardIngestionStatus.Invalid, outcome.Status);
        Assert.NotEmpty(outcome.Errors);
    }

    [Fact]
    public void Missing_required_fields_are_rejected_with_errors()
    {
        using var ctx = NewContext();
        var outcome = NewService(ctx).Ingest(new YardEventEnvelope());
        Assert.Equal(YardIngestionStatus.Invalid, outcome.Status);
        Assert.Contains(outcome.Errors, e => e.Contains("eventId"));
        Assert.Contains(outcome.Errors, e => e.Contains("payload"));
    }

    [Fact]
    public void Empty_guid_event_id_is_rejected()
    {
        using var ctx = NewContext();
        var outcome = NewService(ctx).Ingest(ValidEnvelope(eventId: Guid.Empty));
        Assert.Equal(YardIngestionStatus.Invalid, outcome.Status);
        Assert.Contains(outcome.Errors, e => e.Contains("eventId"));
    }

    [Fact]
    public void Unsupported_schema_version_is_rejected()
    {
        using var ctx = NewContext();
        var outcome = NewService(ctx).Ingest(ValidEnvelope(schemaVersion: 2));
        Assert.Equal(YardIngestionStatus.Invalid, outcome.Status);
        Assert.Contains(outcome.Errors, e => e.Contains("schemaVersion"));
    }

    [Fact]
    public void Unexpected_source_system_is_rejected()
    {
        using var ctx = NewContext();
        var outcome = NewService(ctx).Ingest(ValidEnvelope(sourceSystem: "some-other-system"));
        Assert.Equal(YardIngestionStatus.Invalid, outcome.Status);
        Assert.Contains(outcome.Errors, e => e.Contains("sourceSystem"));
    }

    [Fact]
    public void Payload_that_is_not_an_object_is_rejected()
    {
        using var ctx = NewContext();
        var envelope = ValidEnvelope();
        envelope.Payload = Json("\"not-an-object\"");
        var outcome = NewService(ctx).Ingest(envelope);
        Assert.Equal(YardIngestionStatus.Invalid, outcome.Status);
        Assert.Contains(outcome.Errors, e => e.Contains("payload"));
    }

    [Fact]
    public void First_delivery_is_accepted_and_projected()
    {
        using var ctx = NewContext();
        var outcome = NewService(ctx).Ingest(ValidEnvelope());
        Assert.Equal(YardIngestionStatus.Accepted, outcome.Status);
        Assert.Equal(YardEventCategory.Arrival, outcome.Category);
        Assert.NotNull(outcome.Projection);
        Assert.Equal("T-1", outcome.Projection!.TruckId);
    }

    [Fact]
    public void Duplicate_delivery_is_acked_without_a_second_projection()
    {
        var eventId = Guid.NewGuid();

        using (var ctx = NewContext())
        {
            Assert.Equal(YardIngestionStatus.Accepted, NewService(ctx).Ingest(ValidEnvelope(eventId)).Status);
        }

        using (var ctx = NewContext())
        {
            var dup = NewService(ctx).Ingest(ValidEnvelope(eventId));
            Assert.Equal(YardIngestionStatus.Duplicate, dup.Status);
        }

        using var readCtx = NewContext();
        Assert.Single(new EfYardEventStore(readCtx).ListEvents(100));
    }

    [Fact]
    public void Administrative_event_is_accepted_but_not_projected()
    {
        using var ctx = NewContext();
        var envelope = ValidEnvelope(eventType: "note.added");
        var outcome = NewService(ctx).Ingest(envelope);

        Assert.Equal(YardIngestionStatus.Accepted, outcome.Status);
        Assert.Equal(YardEventCategory.Administrative, outcome.Category);
        Assert.Null(outcome.Projection);
    }

    [Fact]
    public void Empty_expected_source_system_disables_the_source_check()
    {
        using var ctx = NewContext();
        var options = new YardIngestionOptions { ExpectedSourceSystem = "" };
        var outcome = NewService(ctx, options).Ingest(ValidEnvelope(sourceSystem: "anything"));
        Assert.Equal(YardIngestionStatus.Accepted, outcome.Status);
    }
}
