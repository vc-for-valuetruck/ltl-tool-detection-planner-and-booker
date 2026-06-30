using LtlTool.Api.Data;
using LtlTool.Api.Features.Integrations.Alvys.Writeback;
using LtlTool.Api.Tests.Ltl;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

/// <summary>
/// Verifies the EF Core Alvys-operations migration against a <b>real SQL Server</b> — the app's
/// production provider — rather than the SQLite double used by <see cref="EfAlvysOperationStoreTests"/>.
/// This closes the gap the SQLite tests cannot reach: SQL Server-specific migration SQL (the
/// <c>datetimeoffset</c> columns, the composite owner/created index, and the <b>filtered unique
/// index</b> that enforces idempotency only for non-null keys on the Execute channel). It applies the
/// migrations through <see cref="DatabaseFacade.Migrate"/>, asserts the table and both indexes exist in
/// the SQL Server catalog, and proves the filtered unique index actually rejects a duplicate executable
/// key while permitting many null-key rows.
///
/// <para>Auto-skips unless <c>LTLTOOL_SQLSERVER_TEST_CONNECTION</c> is set (see
/// <see cref="SqlServerFactAttribute"/>), so it is inert locally and in the default CI job. The
/// dedicated SQL Server CI job provides an ephemeral instance and sets the variable. Each run targets a
/// uniquely named, freshly created database dropped on dispose, so runs never collide.</para>
/// </summary>
[Trait("Category", "SqlServerMigration")]
public sealed class SqlServerAlvysOperationsMigrationTests : IDisposable
{
    private readonly string _databaseName = $"LtlToolOpsMigTest_{Guid.NewGuid():N}";
    private readonly string _connectionString;
    private readonly string _masterConnectionString;

    public SqlServerAlvysOperationsMigrationTests()
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

    private static AlvysOperationRecord Record(
        string id, AlvysOperationChannel channel, string? key) => new()
    {
        Id = id,
        OwnerId = "dispatcher@valuetruck.com",
        OperationCode = "create-load-note",
        Channel = channel,
        ResourceType = "load",
        ResourceId = "L100",
        IdempotencyKey = key,
        PayloadHash = "hash-" + id,
        PayloadPreview = "{\"Description\":\"x\"}",
        Mode = AlvysWritebackMode.Simulation,
        Disposition = AlvysOperationDisposition.Simulated,
        Status = AlvysOperationRecordStatus.Recorded,
        AttemptCount = 1,
        CorrelationId = "corr-" + id,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [SqlServerFact]
    public void Migrations_apply_and_idempotency_index_is_enforced_on_sql_server()
    {
        using (var ctx = NewContext())
        {
            ctx.Database.Migrate();
        }

        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME = 'AlvysOperations'"));
        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_AlvysOperations_OwnerId_CreatedAt' AND object_id = OBJECT_ID('dbo.AlvysOperations')"));
        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_AlvysOperations_OwnerId_IdempotencyKey' AND object_id = OBJECT_ID('dbo.AlvysOperations') AND is_unique = 1 AND has_filter = 1"));

        // Null-key rows must be allowed in abundance (the filter excludes them from uniqueness).
        using (var ctx = NewContext())
        {
            var store = new EfAlvysOperationStore(ctx);
            store.Add(Record("n1", AlvysOperationChannel.Execute, key: null));
            store.Add(Record("n2", AlvysOperationChannel.Execute, key: null));
            store.Add(Record("d1", AlvysOperationChannel.DryRun, key: "shared")); // dry-run excluded from filter
            store.Add(Record("e1", AlvysOperationChannel.Execute, key: "shared")); // first executable use is fine
        }

        // A second executable row with the same owner + key must violate the filtered unique index.
        using (var ctx = NewContext())
        {
            var store = new EfAlvysOperationStore(ctx);
            var ex = Assert.ThrowsAny<DbUpdateException>(() =>
                store.Add(Record("e2", AlvysOperationChannel.Execute, key: "shared")));
            Assert.IsType<SqlException>(ex.InnerException);
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
