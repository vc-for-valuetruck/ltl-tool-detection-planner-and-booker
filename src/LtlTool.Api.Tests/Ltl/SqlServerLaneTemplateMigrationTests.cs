using LtlTool.Api.Data;
using LtlTool.Api.Features.Ltl.Consolidation;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the EF Core lane-template migration (Phase 2.5) against a <b>real SQL Server</b> — the
/// app's production provider — rather than the SQLite double used by
/// <see cref="LtlTool.Api.Tests.Ltl.Consolidation.EfLaneTemplateStoreTests"/>. It applies the
/// migration history through <see cref="Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade.Migrate"/>,
/// asserts the <c>LaneTemplates</c> table and its surfacing indexes exist in the SQL Server catalog,
/// and round-trips an add/query through <see cref="EfLaneTemplateStore"/>.
///
/// <para>Auto-skipped unless <c>LTLTOOL_SQLSERVER_TEST_CONNECTION</c> is set (see
/// <see cref="SqlServerFactAttribute"/>), so it is inert locally and in the default CI job; the
/// dedicated SQL Server CI job provides an ephemeral instance. Each run targets a uniquely named,
/// freshly created database that is dropped on dispose.</para>
/// </summary>
[Trait("Category", "SqlServerMigration")]
public sealed class SqlServerLaneTemplateMigrationTests : IDisposable
{
    private readonly string _databaseName = $"LtlToolLaneMigTest_{Guid.NewGuid():N}";
    private readonly string _connectionString;
    private readonly string _masterConnectionString;

    public SqlServerLaneTemplateMigrationTests()
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
    public void Migrations_apply_and_lane_template_round_trips_on_sql_server()
    {
        using (var ctx = NewContext())
        {
            ctx.Database.Migrate();
        }

        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME = 'LaneTemplates'"));
        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_LaneTemplates_CorridorCode' AND object_id = OBJECT_ID('dbo.LaneTemplates')"));
        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_LaneTemplates_CustomerName' AND object_id = OBJECT_ID('dbo.LaneTemplates')"));

        using (var ctx = NewContext())
        {
            var store = new EfLaneTemplateStore(ctx);
            store.Add(new LaneTemplateRecord
            {
                Id = "lane-1",
                Name = "Verdef Laredo→Dallas weekly",
                CorridorCode = "LAREDO_TO_DALLAS",
                CustomerName = "Verdef",
                OriginLabel = "Laredo, TX",
                DestinationLabel = "Dallas, TX",
                CadenceDays = 7,
                Notes = "weekly",
                CreatedBy = "dispatch@valuetruck.com",
                CreatedAt = LtlTestFactory.Now,
                UpdatedAt = LtlTestFactory.Now,
            });

            var listed = store.Query(new LaneTemplateQuery(CorridorCode: "LAREDO_TO_DALLAS"));
            Assert.Single(listed);
            Assert.Equal("lane-1", listed[0].Id);
            Assert.Equal("Verdef", listed[0].CustomerName);
            Assert.Equal(7, listed[0].CadenceDays);
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
