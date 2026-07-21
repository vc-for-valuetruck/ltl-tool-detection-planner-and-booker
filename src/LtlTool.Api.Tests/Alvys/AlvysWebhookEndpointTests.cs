using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace LtlTool.Api.Tests;

/// <summary>
/// Boundary tests for the webhook routes. The receiver is deliberately <b>anonymous</b> (Alvys has no
/// email identity) yet fail-closed: with no signing secret configured in the Testing environment it
/// returns 503, proving the request reached the controller past the default <c>AllowedEmailDomain</c>
/// policy rather than being rejected as unauthenticated. The admin listing stays behind that policy and
/// returns 401 when unauthenticated.
/// </summary>
public sealed class AlvysWebhookEndpointTests(TemplateWebApplicationFactory factory)
    : IClassFixture<TemplateWebApplicationFactory>
{
    private readonly TemplateWebApplicationFactory _factory = factory;

    [Fact]
    public async Task Receiver_is_anonymous_and_fails_closed_without_a_secret()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/alvys/webhooks/receiver", new { data = new { } });

        // Not 401 (would mean the auth policy blocked it) and not 404 (would mean unmapped) —
        // 503 proves the anonymous receiver ran and failed closed on the missing secret.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Admin_listing_requires_authentication()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/alvys/webhooks/events");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
