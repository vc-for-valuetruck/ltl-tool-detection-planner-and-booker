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
    public async Task Protected_endpoint_returns_401_when_unauthenticated()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed record HealthResponse(string Status, DateTimeOffset Utc);
}
