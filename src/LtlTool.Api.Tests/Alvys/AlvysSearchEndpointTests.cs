using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace LtlTool.Api.Tests;

/// <summary>
/// Integration tests asserting the read-only Alvys search routes are mapped and sit
/// behind the same authorization policy as other protected endpoints. An unauthenticated
/// request returns 401 (not 404), which confirms both that the route exists and that it
/// is protected — the route is matched before authorization runs.
/// </summary>
public sealed class AlvysSearchEndpointTests(TemplateWebApplicationFactory factory)
    : IClassFixture<TemplateWebApplicationFactory>
{
    private readonly TemplateWebApplicationFactory _factory = factory;

    [Theory]
    [InlineData("/api/alvys/loads/search")]
    [InlineData("/api/alvys/trips/search")]
    [InlineData("/api/alvys/trailers/search")]
    [InlineData("/api/alvys/trucks/search")]
    [InlineData("/api/alvys/dispatch-preferences/search")]
    [InlineData("/api/alvys/locations/search")]
    [InlineData("/api/alvys/drivers/search")]
    [InlineData("/api/alvys/customers/search")]
    [InlineData("/api/alvys/users/search")]
    public async Task Search_routes_require_authentication(string route)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(route, new { Status = new[] { "Open" } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
