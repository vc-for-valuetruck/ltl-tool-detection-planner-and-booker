using LtlTool.Api.Data;
using LtlTool.Api.Features.Integrations.Alvys.Webhooks;
using LtlTool.Api.Tests.Ltl;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

/// <summary>
/// Verifies the webhook/reconciliation migration against a <b>real SQL Server</b> — the production
/// provider — rather than the SQLite double used by <see cref="EfAlvysWebhookStoreTests"/>. It applies
/// every migration through <see cref="Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade.Migrate"/>,
/// asserts the two new tables and the reconciliation columns exist in the SQL Server catalog, and proves
/// the event-id primary key rejects a duplicate delivery (the idempotency backstop for at-least-once
/// webhooks).
///
/// <para>Auto-skips unless <c>LTLTOOL_SQLSERVER_TEST_CONNECTION</c> is set (see
/// <see cref="SqlServerFactAttribute"/>), so it is inert locally and in the default CI job; the dedicated
/// SQL Server CI job provides the instance. Each run targets a uniquely named database dropped on dispose.</para>
/// </summary>
[Trait("Category", "SqlServerMigration")]
public sealed class SqlServerWebhooksMigrationTests : IDisposable
{
    private readonly string _databaseName = $"LtlToolWebhookMigTest_{Guid.NewGuid():N}";
    private readonly string _connectionString;
    private readonly string _masterConnectionString;

    public SqlServerWebhooksMigrationTests()
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

    private static AlvysWebhookEvent Event(string id) => new()
    {
        EventId = id,
        EventType = "load.changed",
        Timestamp = 1_770_000_000,
        LoadNumber = "L-100",
        RawBody = "{\"data\":{\"load\":{\"LoadNumber\":\"L-100\"}}}",
        ReceivedAt = DateTimeOffset.UtcNow,
    };

    [SqlServerFact]
    public void Migrations_apply_and_webhook_tables_and_columns_exist()
    {
        using (var ctx = NewContext())
        {
            ctx.Database.Migrate();
        }

        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME = 'AlvysWebhookEvents'"));
        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME = 'LoadFreshness'"));
        // Reconciliation columns landed on the existing outbox table.
        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'AlvysOperations' AND COLUMN_NAME = 'ReconciliationState'"));
        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'AlvysOperations' AND COLUMN_NAME = 'ResultReference'"));

        // The event-id primary key makes at-least-once delivery idempotent.
        using (var ctx = NewContext())
        {
            var store = new EfAlvysWebhookStore(ctx);
            Assert.True(store.TryInsertReceived(Event("evt-dup")));
        }
        using (var ctx = NewContext())
        {
            var store = new EfAlvysWebhookStore(ctx);
            Assert.False(store.TryInsertReceived(Event("evt-dup")));
            Assert.Equal(1, store.Count());
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
