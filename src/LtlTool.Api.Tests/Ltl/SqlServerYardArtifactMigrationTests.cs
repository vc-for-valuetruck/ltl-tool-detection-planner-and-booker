using LtlTool.Api.Data;
using LtlTool.Api.Features.Ltl.YardArtifacts;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the EF Core yard-artifact migration against a <b>real SQL Server</b> — the app's
/// production provider — rather than the SQLite double used by <see cref="EfYardArtifactStoreTests"/>.
/// It applies the migration history through <see cref="DatabaseFacade.Migrate"/>, asserts the
/// <c>YardArtifacts</c> table and its surfacing indexes exist in the SQL Server catalog, and
/// round-trips an add/get through <see cref="EfYardArtifactStore"/> (exercising the
/// <c>nvarchar(max)</c> JSON columns and the string-converted status enum).
///
/// <para>Auto-skipped unless <c>LTLTOOL_SQLSERVER_TEST_CONNECTION</c> is set (see
/// <see cref="SqlServerFactAttribute"/>), so it is inert locally and in the default CI job; the
/// dedicated SQL Server CI job provides an ephemeral instance. Each run targets a uniquely named,
/// freshly created database that is dropped on dispose.</para>
/// </summary>
[Trait("Category", "SqlServerMigration")]
public sealed class SqlServerYardArtifactMigrationTests : IDisposable
{
    private readonly string _databaseName = $"LtlToolYardMigTest_{Guid.NewGuid():N}";
    private readonly string _connectionString;
    private readonly string _masterConnectionString;

    public SqlServerYardArtifactMigrationTests()
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
    public void Migrations_apply_and_yard_artifact_round_trips_on_sql_server()
    {
        using (var ctx = NewContext())
        {
            ctx.Database.Migrate();
        }

        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME = 'YardArtifacts'"));
        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_YardArtifacts_LoadNumber' AND object_id = OBJECT_ID('dbo.YardArtifacts')"));
        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_YardArtifacts_TruckUnit' AND object_id = OBJECT_ID('dbo.YardArtifacts')"));
        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_YardArtifacts_TrailerUnit' AND object_id = OBJECT_ID('dbo.YardArtifacts')"));

        using (var ctx = NewContext())
        {
            var store = new EfYardArtifactStore(ctx);
            store.Add(new YardArtifactRecord
            {
                Id = "a1",
                Yard = "LAREDO",
                TruckUnit = "T1",
                LoadNumber = "L100",
                SubmittedBy = "dock@valuetruck.com",
                CapturedAt = LtlTestFactory.Now,
                CreatedAt = LtlTestFactory.Now,
                Status = YardInspectionStatus.Flagged,
                PassedItems = 2,
                FailedItems = 1,
                NaItems = 0,
                VerifiedPalletCount = 10,
                InspectionJson = "{\"yard\":\"LAREDO\"}",
                FilesJson = "[]",
            });

            var listed = store.Query(new YardArtifactQuery(LoadNumber: "L100"));
            Assert.Single(listed);
            Assert.Equal("a1", listed[0].Id);
            Assert.Equal(YardInspectionStatus.Flagged, listed[0].Status);
            Assert.Equal(10, listed[0].VerifiedPalletCount);
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
