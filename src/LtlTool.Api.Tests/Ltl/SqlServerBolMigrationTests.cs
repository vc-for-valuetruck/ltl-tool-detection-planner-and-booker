using LtlTool.Api.Data;
using LtlTool.Api.Features.Ltl.Bol;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the EF Core BOL-suggestion migration against a <b>real SQL Server</b> — the app's
/// production provider — rather than the SQLite double used by <see cref="Bol.EfBolSuggestionStoreTests"/>.
/// It applies the migration history through <see cref="DatabaseFacade.Migrate"/>, asserts the
/// <c>BolFieldSuggestions</c> table and its surfacing indexes exist in the SQL Server catalog, and
/// round-trips an add/query through <see cref="EfBolSuggestionStore"/> (exercising the
/// <c>nvarchar(max)</c> evidence column and the string-converted enum columns).
///
/// <para>Auto-skipped unless <c>LTLTOOL_SQLSERVER_TEST_CONNECTION</c> is set (see
/// <see cref="SqlServerFactAttribute"/>), so it is inert locally and in the default CI job; the
/// dedicated SQL Server CI job provides an ephemeral instance. Each run targets a uniquely named,
/// freshly created database that is dropped on dispose.</para>
/// </summary>
[Trait("Category", "SqlServerMigration")]
public sealed class SqlServerBolMigrationTests : IDisposable
{
    private readonly string _databaseName = $"LtlToolBolMigTest_{Guid.NewGuid():N}";
    private readonly string _connectionString;
    private readonly string _masterConnectionString;

    public SqlServerBolMigrationTests()
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
    public void Migrations_apply_and_bol_suggestion_round_trips_on_sql_server()
    {
        using (var ctx = NewContext())
        {
            ctx.Database.Migrate();
        }

        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME = 'BolFieldSuggestions'"));
        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_BolFieldSuggestions_LoadNumber' AND object_id = OBJECT_ID('dbo.BolFieldSuggestions')"));
        Assert.Equal(1, ScalarCount(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_BolFieldSuggestions_Status' AND object_id = OBJECT_ID('dbo.BolFieldSuggestions')"));

        using (var ctx = NewContext())
        {
            var store = new EfBolSuggestionStore(ctx);
            store.AddBatch(
            [
                new BolFieldSuggestionRecord
                {
                    Id = "s1",
                    LoadNumber = "L100",
                    DocumentId = "doc-1",
                    DocumentName = "BOL.pdf",
                    Field = BolField.PalletCount.ToString(),
                    Value = "12",
                    Confidence = 0.9,
                    EvidenceQuote = "Pallet count: 12",
                    ExtractorName = "deterministic-regex",
                    Status = BolSuggestionStatus.Pending.ToString(),
                    CreatedBy = "dispatch@valuetruck.com",
                    CreatedAt = LtlTestFactory.Now,
                },
            ]);

            var listed = store.Query(new BolSuggestionQuery(LoadNumber: "L100"));
            Assert.Single(listed);
            Assert.Equal("s1", listed[0].Id);
            Assert.Equal("12", listed[0].Value);

            var updated = store.UpdateStatus(
                "s1", BolSuggestionStatus.Accepted, "ops@valuetruck.com", LtlTestFactory.Now.AddHours(1));
            Assert.Equal(BolSuggestionStatus.Accepted.ToString(), updated!.Status);
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
