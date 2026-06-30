using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace LtlTool.Api.Tests;

/// <summary>
/// Integration tests asserting the sandbox-gated Alvys operation routes are mapped and protected by
/// the same authorization policy as the rest of the API. An unauthenticated request returns 401
/// (not 404), confirming the route exists and is protected before authorization runs.
/// </summary>
public sealed class AlvysOperationsEndpointTests(TemplateWebApplicationFactory factory)
    : IClassFixture<TemplateWebApplicationFactory>
{
    private readonly TemplateWebApplicationFactory _factory = factory;

    [Theory]
    [InlineData("/api/alvys/ops/status")]
    [InlineData("/api/alvys/ops/operations")]
    [InlineData("/api/alvys/ops/history")]
    [InlineData("/api/alvys/ops/history/abc123")]
    public async Task Get_routes_require_authentication(string route)
    {
        var response = await _factory.CreateClient().GetAsync(route);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/alvys/ops/create-load-note/dry-run")]
    [InlineData("/api/alvys/ops/create-load-note/execute")]
    [InlineData("/api/alvys/ops/sync/probe")]
    public async Task Post_routes_require_authentication(string route)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(route, new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
