using LtlTool.Api.Data;
using LtlTool.Api.Features.Ltl.Signals;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Signals;

/// <summary>
/// Verifies the EF Core signal migration against a <b>real SQL Server</b> — the app's production
/// provider — rather than the SQLite double used by <see cref="EfSignalStoreTests"/>. It applies the
/// migration history, asserts the <c>Signals</c> table and its review/surfacing indexes exist in the
/// SQL Server catalog, and round-trips a batch add/query through <see cref="EfSignalStore"/>
/// (exercising the <c>nvarchar(max)</c> evidence column and the string-stored enums).
///
/// <para>Auto-skipped unless <c>LTLTOOL_SQLSERVER_TEST_CONNECTION</c> is set (see
/// <see cref="SqlServerFactAttribute"/>), so it is inert locally and in the default CI job; the
/// dedicated SQL Server CI job provides an ephemeral instance. Each run targets a uniquely named,
/// freshly created database that is dropped on dispose.</para>
/// </summary>
[Trait("Category", "SqlServerMigration")]
public sealed class SqlServerSignalMigrationTests : IDisposable
{
    private readonly string _databaseName = $"LtlToolSignalMigTest_{Guid.NewGuid():N}";
    private readonly string _connectionString;
    private readonly string _masterConnectionString;

    public SqlServerSignalMigrationTests()
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

    [SqlServerFact]
    public void Migrations_apply_and_signal_round_trips_on_sql_server()
    {
        using (var ctx = NewContext())
        {
            ctx.Database.Migrate();
        }

        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME = 'Signals'"));
        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_Signals_Status' AND object_id = OBJECT_ID('dbo.Signals')"));
        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_Signals_LoadNumber' AND object_id = OBJECT_ID('dbo.Signals')"));

        using (var ctx = NewContext())
        {
            var store = new EfSignalStore(ctx);
            store.AddBatch(
            [
                new SignalRecord
                {
                    Id = "s1",
                    SourceType = "email",
                    SourceId = "msg-1",
                    SignalType = SignalType.AccessorialEvidence.ToString(),
                    Confidence = 1.0,
                    EvidenceQuote = "Driver was detained 3 hours waiting on a lumper.",
                    SuggestedSurface = LtlSurface.BillingWorklistBadge.ToString(),
                    Summary = "detention evidence",
                    LoadNumber = "L100",
                    Status = SignalStatus.Pending.ToString(),
                    IngestedBy = "dispatcher@valuetruck.com",
                    CreatedAt = LtlTestFactory.Now,
                },
            ]);

            var listed = store.Query(new SignalQuery(LoadNumber: "L100"));
            Assert.Single(listed);
            Assert.Equal("s1", listed[0].Id);
            Assert.Equal(SignalType.AccessorialEvidence.ToString(), listed[0].SignalType);

            var accepted = store.UpdateStatus("s1", SignalStatus.Accepted, "dispatcher@valuetruck.com", LtlTestFactory.Now);
            Assert.Equal(SignalStatus.Accepted.ToString(), accepted!.Status);
        }
    }

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
