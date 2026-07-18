using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LtlTool.Api.Tests;

/// <summary>
/// Boots the API with <c>AccessPolicy:Mode=Demo</c>. Verifies the demo auth handler
/// admits unauthenticated callers to protected endpoints (no bearer token needed),
/// and that the /health endpoint publishes the mode so ops has a second independent
/// check that no UAT/prod deployment is silently in demo mode.
/// </summary>
public sealed class DemoModeWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AccessPolicy:Mode"] = "Demo",
                // AzureAd values also required so the JwtBearer scheme registered at
                // build time can materialize its options. In Demo mode nothing ever
                // reaches that scheme (router forwards to Demo), but MSAL still
                // validates its options greedily at registration; give it dummies.
                ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
                ["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000000",
                ["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000000",
                ["AzureAd:Audience"] = "api://00000000-0000-0000-0000-000000000000",
                ["ConnectionStrings:DefaultConnection"] =
                    "Server=localhost,14333;Database=Test;User Id=sa;Password=Not_Used_In_Tests1!;Encrypt=False;TrustServerCertificate=True",
            });
        });
        return base.CreateHost(builder);
    }
}

public sealed class DemoAuthenticationTests(DemoModeWebApplicationFactory factory)
    : IClassFixture<DemoModeWebApplicationFactory>
{
    private readonly DemoModeWebApplicationFactory _factory = factory;

    [Fact]
    public async Task Health_reports_authMode_Demo_when_configured()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<DemoHealthResponse>();
        Assert.Equal("Demo", body?.AuthMode);
    }

    [Fact]
    public async Task Protected_endpoint_admits_caller_without_bearer_in_demo_mode()
    {
        // In EntraId mode this would return 401 (proven by HealthEndpointTests.Protected_endpoint_returns_401_when_unauthenticated).
        // In Demo mode the synthetic identity admits every request.
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Pins the exact JSON key name that <c>scripts/demo-up.sh</c> and <c>demo-up.ps1</c>
    /// grep out of the health payload for their post-boot smoke assertion. If someone
    /// renames <c>authMode</c> to <c>auth_mode</c> or PascalCases it, the shell smoke
    /// test regex would silently miss and demo-mode misconfiguration could ship. This
    /// test fails first, forcing the runner scripts to be updated in lockstep.
    /// </summary>
    [Fact]
    public async Task Health_payload_uses_authMode_key_that_demo_up_scripts_grep()
    {
        var client = _factory.CreateClient();
        var raw = await client.GetStringAsync("/api/health");
        Assert.Contains("\"authMode\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"Demo\"", raw, StringComparison.Ordinal);
    }

    private sealed record DemoHealthResponse(string Status, DateTimeOffset Utc, string AuthMode);
}
