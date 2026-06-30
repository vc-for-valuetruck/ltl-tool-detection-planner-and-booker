using LtlTool.Api.Data;
using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.SavedViews;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the EF Core saved-view migration against a <b>real SQL Server</b> — the app's production
/// provider — rather than the SQLite double used by <see cref="EfSavedViewStoreTests"/>. CI build/test
/// runs SQLite-backed tests, which never exercises SQL Server-specific migration SQL (column types like
/// <c>nvarchar(max)</c>/<c>datetimeoffset</c>, index creation). This test closes that gap: it applies
/// the migrations through <see cref="DatabaseFacade.Migrate"/>, asserts the <c>SavedViews</c> table and
/// its owner index exist in the SQL Server catalog, and round-trips a create/list through
/// <see cref="EfSavedViewStore"/>.
///
/// <para>Auto-skipped unless <c>LTLTOOL_SQLSERVER_TEST_CONNECTION</c> is set (see
/// <see cref="SqlServerFactAttribute"/>), so it is inert locally and in the default CI job. The
/// dedicated SQL Server CI job provides an ephemeral instance and sets the variable. Each run targets a
/// uniquely named, freshly created database that is dropped on dispose, so runs never collide and leave
/// nothing behind.</para>
/// </summary>
[Trait("Category", "SqlServerMigration")]
public sealed class SqlServerSavedViewMigrationTests : IDisposable
{
    private readonly string _databaseName = $"LtlToolMigTest_{Guid.NewGuid():N}";
    private readonly string _connectionString;
    private readonly string _masterConnectionString;

    public SqlServerSavedViewMigrationTests()
    {
        var baseConnection = Environment.GetEnvironmentVariable(SqlServerFactAttribute.ConnectionEnvVar)
            ?? throw new InvalidOperationException(
                $"{SqlServerFactAttribute.ConnectionEnvVar} is not set; this test should have been skipped.");

        // Target a unique database for isolation; keep a master-scoped string for create/drop bookkeeping.
        var builder = new SqlConnectionStringBuilder(baseConnection) { InitialCatalog = _databaseName };
        _connectionString = builder.ConnectionString;
        builder.InitialCatalog = "master";
        _masterConnectionString = builder.ConnectionString;
    }

    private AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            // Tolerate the brief window where the freshly started CI SQL Server is reachable but not yet
            // accepting logins; without this the first connection can flake.
            .UseSqlServer(_connectionString, sql => sql.EnableRetryOnFailure())
            .Options);

    [SqlServerFact]
    public void Migrations_apply_and_saved_view_round_trips_on_sql_server()
    {
        // Apply the real migration history (creates the database, then the SavedViews schema).
        using (var ctx = NewContext())
        {
            ctx.Database.Migrate();
        }

        // The migration must have produced the SavedViews table and its owner index in SQL Server's catalog.
        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME = 'SavedViews'"));
        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_SavedViews_OwnerId' AND object_id = OBJECT_ID('dbo.SavedViews')"));

        // Basic create/list behavior must work end-to-end through the production store on SQL Server.
        using (var ctx = NewContext())
        {
            var store = new EfSavedViewStore(ctx, LtlTestFactory.Clock());
            var created = store.Create("dispatcher@valuetruck.com", new SavedViewRequest
            {
                Name = "Hot lanes",
                Description = "desc",
                Filters = new SavedViewFilters { Stage = WorkflowStage.Match, ReadyToBill = true },
            });

            var listed = store.ListForOwner("dispatcher@valuetruck.com");
            Assert.Single(listed);
            Assert.Equal(created.Id, listed[0].Id);
            Assert.Equal("Hot lanes", listed[0].Name);
            Assert.Equal(WorkflowStage.Match, listed[0].Filters.Stage);
            Assert.True(listed[0].Filters.ReadyToBill);
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

        // Drop the throwaway database. The name is a GUID-derived identifier, so it is safe to interpolate.
        using var connection = new SqlConnection(_masterConnectionString);
        try
        {
            connection.Open();
        }
        catch (SqlException)
        {
            // Server unreachable (e.g. the test was skipped before any database was created): nothing to clean up.
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
