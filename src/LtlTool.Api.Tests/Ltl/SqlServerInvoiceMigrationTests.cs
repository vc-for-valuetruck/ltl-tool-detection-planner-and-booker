using LtlTool.Api.Data;
using LtlTool.Api.Features.Ltl.Billing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the EF Core <c>AddInvoices</c> migration against a <b>real SQL Server</b> — the app's
/// production provider — rather than the SQLite double used by the in-memory service tests. It
/// applies the migration history through <see cref="DatabaseFacade.Migrate"/>, asserts the
/// <c>Invoices</c> table and its lookup indexes exist in the SQL Server catalog, and round-trips a
/// record through <see cref="EfInvoiceStore"/> (exercising the <c>nvarchar(max)</c> LoadsJson /
/// EditHistoryJson columns, the string-converted <see cref="InvoiceStatus"/>, and the
/// <c>decimal(18,2)</c> money/RPM columns).
///
/// <para>Auto-skipped unless <c>LTLTOOL_SQLSERVER_TEST_CONNECTION</c> is set (see
/// <see cref="SqlServerFactAttribute"/>), so it is inert locally and in the default CI job; the
/// dedicated SQL Server CI job provides an ephemeral instance. Each run targets a uniquely named,
/// freshly created database that is dropped on dispose.</para>
/// </summary>
[Trait("Category", "SqlServerMigration")]
public sealed class SqlServerInvoiceMigrationTests : IDisposable
{
    private readonly string _databaseName = $"LtlToolInvoiceMigTest_{Guid.NewGuid():N}";
    private readonly string _connectionString;
    private readonly string _masterConnectionString;

    public SqlServerInvoiceMigrationTests()
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
    public void Migrations_apply_and_invoice_round_trips_on_sql_server()
    {
        using (var ctx = NewContext())
        {
            ctx.Database.Migrate();
        }

        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME = 'Invoices'"));
        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_Invoices_ParentLoadId' AND object_id = OBJECT_ID('dbo.Invoices')"));
        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_Invoices_Status' AND object_id = OBJECT_ID('dbo.Invoices')"));

        var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

        using (var ctx = NewContext())
        {
            var store = new EfInvoiceStore(ctx);
            store.Add(new InvoiceRecord
            {
                Id = "inv-1",
                InvoiceNumber = "INV-100482",
                Status = InvoiceStatus.Draft,
                CorridorCode = "LRD-DAL",
                CustomerName = "Acme Freight",
                ParentLoadId = "L-1",
                ParentLoadNumber = "100482",
                LoadsJson = """[{"loadNumber":"100482","isParent":true,"charges":[{"type":"Linehaul","amount":2000}]}]""",
                EditHistoryJson = """[{"action":"Created","by":"ops@vt.com"}]""",
                InvoiceTotal = 2700m,
                CombinedRevenue = 2700m,
                CombinedDriverTripValue = 1200m,
                DriverLoadedMiles = 500m,
                CombinedRevenuePerMile = 2.40m,
                LoadsMissingBolCount = 1,
                CreatedBy = "ops@vt.com",
                CreatedAt = now,
                UpdatedBy = "ops@vt.com",
                UpdatedAt = now,
            });
        }

        using (var ctx = NewContext())
        {
            var store = new EfInvoiceStore(ctx);

            var fetched = store.Get("inv-1");
            Assert.NotNull(fetched);
            Assert.Equal(InvoiceStatus.Draft, fetched!.Status);
            Assert.Equal("INV-100482", fetched.InvoiceNumber);
            Assert.Equal(2700m, fetched.InvoiceTotal);
            Assert.Equal(2.40m, fetched.CombinedRevenuePerMile);
            Assert.Equal(1, fetched.LoadsMissingBolCount);
            Assert.Equal("NotPerformed", fetched.AlvysWriteback);

            var byStatus = store.List(parentLoadId: null, status: InvoiceStatus.Draft, max: 50);
            Assert.Single(byStatus);
            Assert.Equal("inv-1", byStatus[0].Id);

            var byParent = store.List(parentLoadId: "L-1", status: null, max: 50);
            Assert.Single(byParent);
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
