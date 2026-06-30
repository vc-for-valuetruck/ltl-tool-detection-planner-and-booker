using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// A <see cref="FactAttribute"/> that auto-skips unless a SQL Server test connection string is
/// available via the <c>LTLTOOL_SQLSERVER_TEST_CONNECTION</c> environment variable. SQL Server is the
/// app's production database provider, but most environments (local dev, the default CI build/test
/// job) have no SQL Server instance, so these tests stay skipped there rather than failing. The
/// dedicated SQL Server CI job sets the variable, at which point the tests run and verify the EF Core
/// migrations apply against a real SQL Server.
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public const string ConnectionEnvVar = "LTLTOOL_SQLSERVER_TEST_CONNECTION";

    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar)))
        {
            Skip = $"Set {ConnectionEnvVar} to a SQL Server connection string to run this test.";
        }
    }
}
