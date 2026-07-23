using LtlTool.Api.Data;
using LtlTool.Api.Features.Ltl.YardIngestion;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.YardIngestion;

/// <summary>
/// Verifies the Yard→LTL ingestion migration against a <b>real SQL Server</b> — the app's production
/// provider — rather than the SQLite double used by <see cref="EfYardEventStoreTests"/>. It applies the
/// migration history, asserts the <c>YardEvents</c> and <c>YardScheduleInputs</c> tables and their
/// indexes exist in the SQL Server catalog, and round-trips an append/query through
/// <see cref="EfYardEventStore"/> (exercising the <c>nvarchar(max)</c> payload column, the
/// string-stored enum columns, and the store-assigned <c>Sequence</c> ordinal).
///
/// <para>Auto-skipped unless <c>LTLTOOL_SQLSERVER_TEST_CONNECTION</c> is set (see
/// <see cref="SqlServerFactAttribute"/>), so it is inert locally and in the default CI job; the
/// dedicated SQL Server CI job provides an ephemeral instance. Each run targets a uniquely named,
/// freshly created database that is dropped on dispose.</para>
/// </summary>
[Trait("Category", "SqlServerMigration")]
public sealed class SqlServerYardScheduleIngestionMigrationTests : IDisposable
{
    private readonly string _databaseName = $"LtlToolYardIngestMigTest_{Guid.NewGuid():N}";
    private readonly string _connectionString;
    private readonly string _masterConnectionString;
    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 8, 0, 0, TimeSpan.Zero);

    public SqlServerYardScheduleIngestionMigrationTests()
    {
        var baseConnection = Environment.GetEnvironmentVariable(SqlServerFactAttribute.ConnectionEnvVar)
            ?? throw new InvalidOperationException(
                $"{SqlServerFactAttribute.ConnectionEnvVar} is not set; this test should have been skipped.");

        var builder = new SqlConnectionStringBuilder(baseConnection) { InitialCatalog = _databaseName };
        _connectionString = builder.ConnectionString;
        builder.InitialCatalog = "master";
        _masterConnectionString = builder.ConnectionString;
    }

    private AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_connectionString, sql => sql.EnableRetryOnFailure())
            .Options);

    private static YardEventRecord Event(YardEventCategory category, string eventId, string? payloadJson = null, int minutesAfterT0 = 0)
    {
        var system = "yard-control";
        var type = "appointment";
        var recordId = "R1";
        return new YardEventRecord
        {
            DedupeKey = $"{eventId}:{system}:{type}:{recordId}",
            EventId = eventId,
            SchemaVersion = 1,
            EventType = category.ToString(),
            Category = category.ToString(),
            AffectsSchedulerInput = YardEventClassifier.AffectsSchedulerInput(category),
            OccurredAt = T0.AddMinutes(minutesAfterT0),
            SourceSystem = system,
            SourceRecordType = type,
            SourceRecordId = recordId,
            YardLocationId = "YARD-A",
            PayloadJson = payloadJson ?? "{}",
        };
    }

    [SqlServerFact]
    public void Migrations_apply_and_yard_ingestion_round_trips_on_sql_server()
    {
        using (var ctx = NewContext())
        {
            ctx.Database.Migrate();
        }

        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME = 'YardEvents'"));
        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME = 'YardScheduleInputs'"));
        Assert.Equal(1, IndexCount("YardEvents", "IX_YardEvents_Sequence"));
        Assert.Equal(1, IndexCount("YardEvents", "IX_YardEvents_SourceSystem_SourceRecordType_SourceRecordId"));
        Assert.Equal(1, IndexCount("YardScheduleInputs", "IX_YardScheduleInputs_HoldState"));
        Assert.Equal(1, IndexCount("YardScheduleInputs", "IX_YardScheduleInputs_Readiness"));
        Assert.Equal(1, IndexCount("YardScheduleInputs", "IX_YardScheduleInputs_UpdatedAt"));
        Assert.Equal(1, IndexCount("YardScheduleInputs", "IX_YardScheduleInputs_YardLocationId"));

        using (var ctx = NewContext())
        {
            var store = new EfYardEventStore(ctx);
            // Deliver out of order (release before load-complete) to exercise the deterministic rebuild.
            store.Append(Event(YardEventCategory.Release, "e-rel", minutesAfterT0: 40), T0.AddMinutes(1));
            var result = store.Append(
                Event(YardEventCategory.LoadComplete, "e-dock", "{\"truckId\":\"T-1\"}", minutesAfterT0: 30),
                T0.AddMinutes(2));

            Assert.Equal(YardAppendStatus.Accepted, result.Status);

            // A duplicate dedupe key is an idempotent no-op on the real provider's unique key.
            var dup = store.Append(Event(YardEventCategory.Release, "e-rel", minutesAfterT0: 40), T0.AddMinutes(5));
            Assert.Equal(YardAppendStatus.Duplicate, dup.Status);
        }

        using (var ctx = NewContext())
        {
            var store = new EfYardEventStore(ctx);
            var projection = store.GetProjection("yard-control", "appointment", "R1");

            Assert.NotNull(projection);
            Assert.True(projection!.DockCompleted);
            Assert.True(projection.SecurityCleared);
            Assert.Equal(ScheduleReadiness.Ready.ToString(), projection.Readiness);
            Assert.Equal("T-1", projection.TruckId);
            Assert.Equal(2, projection.EventCount);
            Assert.Equal(2, store.ListEvents(100).Count);
        }
    }

    private int IndexCount(string table, string indexName) => ScalarCount(
        $"SELECT COUNT(*) FROM sys.indexes WHERE name = '{indexName}' AND object_id = OBJECT_ID('dbo.{table}')");

    private int ScalarCount(string sql)
    {
        using var connection = new SqlConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Dispose()
    {
        SqlConnection.ClearAllPools();

        using var connection = new SqlConnection(_masterConnectionString);
        try
        {
            connection.Open();
        }
        catch (SqlException)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            $"IF DB_ID(N'{_databaseName}') IS NOT NULL " +
            $"BEGIN " +
            $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{_databaseName}]; " +
            $"END";
        command.ExecuteNonQuery();
    }
}
