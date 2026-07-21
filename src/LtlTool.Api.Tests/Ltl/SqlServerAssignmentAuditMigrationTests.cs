using LtlTool.Api.Data;
using LtlTool.Api.Features.Ltl.Assignment;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the EF Core assignment-audit migration against a <b>real SQL Server</b> — the app's
/// production provider — rather than the SQLite double used by <see cref="EfAssignmentAuditStoreTests"/>.
/// It applies the migration history through <see cref="DatabaseFacade.Migrate"/>, asserts the
/// <c>AssignmentAudits</c> table and its history indexes exist in the SQL Server catalog, and
/// round-trips a record/query through <see cref="EfAssignmentAuditStore"/> (exercising the
/// <c>nvarchar(max)</c> warnings JSON column and the string-converted <see cref="AssignmentReasonType"/>).
///
/// <para>Auto-skipped unless <c>LTLTOOL_SQLSERVER_TEST_CONNECTION</c> is set (see
/// <see cref="SqlServerFactAttribute"/>), so it is inert locally and in the default CI job; the
/// dedicated SQL Server CI job provides an ephemeral instance. Each run targets a uniquely named,
/// freshly created database that is dropped on dispose.</para>
/// </summary>
[Trait("Category", "SqlServerMigration")]
public sealed class SqlServerAssignmentAuditMigrationTests : IDisposable
{
    private readonly string _databaseName = $"LtlToolAssignMigTest_{Guid.NewGuid():N}";
    private readonly string _connectionString;
    private readonly string _masterConnectionString;

    public SqlServerAssignmentAuditMigrationTests()
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
    public void Migrations_apply_and_assignment_audit_round_trips_on_sql_server()
    {
        using (var ctx = NewContext())
        {
            ctx.Database.Migrate();
        }

        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME = 'AssignmentAudits'"));
        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_AssignmentAudits_LoadId' AND object_id = OBJECT_ID('dbo.AssignmentAudits')"));
        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_AssignmentAudits_RecordedBy' AND object_id = OBJECT_ID('dbo.AssignmentAudits')"));

        using (var ctx = NewContext())
        {
            var store = new EfAssignmentAuditStore(ctx);
            var recorded = store.Record(
                "L100",
                new AssignmentRequest
                {
                    DriverId = "D1",
                    TruckId = "T1",
                    TrailerId = "TR1",
                    MatchScore = 82,
                    MatchLabel = "Good Match",
                    Notes = "dispatcher note",
                    ReasonType = AssignmentReasonType.ServiceRecovery,
                    OverrideReason = "late load, best available",
                },
                "dispatcher@valuetruck.com",
                [new AssignmentIssue { Code = "EQUIP_MISMATCH", Message = "Trailer differs", Severity = AssignmentIssueSeverity.Warn }]);

            Assert.Equal(AssignmentReasonType.ServiceRecovery, recorded.ReasonType);
            Assert.Equal("NotPerformed", recorded.AlvysWriteback);

            var forLoad = store.ForLoad("L100");
            Assert.Single(forLoad);
            Assert.Equal(AssignmentReasonType.ServiceRecovery, forLoad[0].ReasonType);
            Assert.Single(forLoad[0].Warnings);

            var queried = store.Query(new AssignmentAuditQuery(
                RecordedBy: "dispatcher@valuetruck.com",
                ReasonType: AssignmentReasonType.ServiceRecovery));
            Assert.Single(queried);
            Assert.Equal("L100", queried[0].LoadId);
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
