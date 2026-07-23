using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.YardIngestion;

/// <summary>
/// Proves the versioned Yard→LTL ingestion surface (<c>/api/v1/yard-events</c>) is both mapped and
/// protected. Under the factory's default EntraId access mode an unauthenticated request returns 401
/// (not 404), which shows the route exists and sits behind the <c>YardEventIngest</c> authorization
/// policy — Yard's service-to-service token is required. A 404 here would mean a route was renamed or
/// dropped; a 200/202 would mean the endpoint was accidentally left anonymous.
/// </summary>
public sealed class YardEventsRouteProtectionTests(TemplateWebApplicationFactory factory)
    : IClassFixture<TemplateWebApplicationFactory>
{
    private readonly TemplateWebApplicationFactory _factory = factory;

    [Theory]
    [InlineData("/api/v1/yard-events/schedule-input")]
    [InlineData("/api/v1/yard-events/schedule-input?readiness=Ready")]
    [InlineData("/api/v1/yard-events/schedule-input/yard-control/appointment/R1")]
    [InlineData("/api/v1/yard-events/events")]
    [InlineData("/api/v1/yard-events/events/yard-control/appointment/R1")]
    public async Task Yard_event_get_routes_are_mapped_and_protected(string route)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/yard-events")]
    [InlineData("/api/v1/yard-events/schedule-input/yard-control/appointment/R1/replay")]
    public async Task Yard_event_post_routes_are_mapped_and_protected(string route)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(route, new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
