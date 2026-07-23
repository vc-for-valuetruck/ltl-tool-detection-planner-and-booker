using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LtlTool.Api.Tests;

public sealed class TemplateWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
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

public sealed class HealthEndpointTests(TemplateWebApplicationFactory factory)
    : IClassFixture<TemplateWebApplicationFactory>
{
    private readonly TemplateWebApplicationFactory _factory = factory;

    [Fact]
    public async Task Health_is_anonymous_and_returns_ok()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("ok", body?.Status);
    }

    [Fact]
    public async Task Health_surfaces_alvys_provider_and_credential_state()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        // Provider defaults to Live (live Alvys is the source of truth); the test config supplies no
        // Alvys credentials, so the guard field must honestly report them absent. This is the signal
        // the deploy smoke test uses to catch a UAT deploy that would serve no live Alvys data.
        Assert.Equal("Live", root.GetProperty("alvysProvider").GetString());
        Assert.False(root.GetProperty("alvysCredentialsPresent").GetBoolean());
    }

    [Fact]
    public async Task Optimization_health_is_anonymous_and_returns_ok_with_flags_off_by_default()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health/optimization");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        // Default configuration leaves every optimization flag off, so nothing is enabled to be
        // unhealthy — the probe must report "ok" and all flags false.
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("flags").GetProperty("trailerFit").GetBoolean());
        Assert.False(root.GetProperty("flags").GetProperty("solver").GetBoolean());
        Assert.False(root.GetProperty("flags").GetProperty("agentCommands").GetBoolean());
    }

    [Fact]
    public async Task Alvys_health_is_anonymous_and_degraded_without_credentials()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health/alvys");

        // Anonymous like /api/health. The test config supplies no Alvys credentials, so the Live
        // provider cannot authenticate — the probe must honestly report "degraded" (not "ok") and
        // must not attempt any upstream call.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("degraded", root.GetProperty("status").GetString());
        var report = root.GetProperty("report");
        Assert.False(report.GetProperty("credentialsPresent").GetBoolean());
        Assert.False(report.GetProperty("tokenAcquired").GetBoolean());
        Assert.Equal(0, report.GetProperty("probes").GetArrayLength());
    }

    [Fact]
    public async Task Protected_endpoint_returns_401_when_unauthenticated()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed record HealthResponse(string Status, DateTimeOffset Utc);
}
