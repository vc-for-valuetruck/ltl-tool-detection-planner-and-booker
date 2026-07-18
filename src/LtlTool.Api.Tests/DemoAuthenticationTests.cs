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

    private sealed record DemoHealthResponse(string Status, DateTimeOffset Utc, string AuthMode);
}
