using System.Net;
using System.Net.Http.Json;
using LtlTool.Api.Features.Ai.Narrative.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LtlTool.Api.Tests.Ai;

/// <summary>
/// Integration tests for <c>GET /api/ai/consolidation/narrative</c> (#150). The underlying
/// <see cref="INarrativeService"/> is built in #149; here it is replaced with a hand-rolled test
/// double so the endpoint's routing, kill-switch, header and status-code contract can be exercised
/// in isolation. Auth mirrors the rest of the API: Demo mode admits the synthetic identity for the
/// behavioral tests; the 401 test boots in Entra (JWT) mode with no bearer token.
/// </summary>
public sealed class NarrativeEndpointTests
{
    private const string Route = "/api/ai/consolidation/narrative?planId=PLAN-1";

    /// <summary>Demo-auth factory: every request is authenticated as the synthetic demo identity.</summary>
    private sealed class DemoFactory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AccessPolicy:Mode"] = "Demo",
                    ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
                    ["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000000",
                    ["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000000",
                    ["AzureAd:Audience"] = "api://00000000-0000-0000-0000-000000000000",
                    ["ConnectionStrings:DefaultConnection"] =
                        "Server=localhost,14333;Database=Test;User Id=sa;Password=Not_Used_In_Tests1!;Encrypt=False;TrustServerCertificate=True",
                }));
            return base.CreateHost(builder);
        }
    }

    /// <summary>Configurable double standing in for the #149 NarrativeService.</summary>
    private sealed class StubNarrativeService(Func<string, (NarrativeResponse?, bool)> behavior)
        : INarrativeService
    {
        public Task<(NarrativeResponse? Response, bool Cached)> GenerateAsync(string planId, CancellationToken ct)
            => Task.FromResult(behavior(planId));
    }

    /// <summary>
    /// Builds a Demo-auth client with <c>AI:NarrativeEnabled</c> set and the narrative service
    /// replaced by the supplied double.
    /// </summary>
    private static HttpClient CreateClient(
        DemoFactory factory, bool narrativeEnabled, INarrativeService service)
        => factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["AI:NarrativeEnabled"] = narrativeEnabled ? "true" : "false",
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<INarrativeService>();
                    services.AddScoped<INarrativeService>(_ => service);
                });
            })
            .CreateClient();

    private static StubNarrativeService Returns(NarrativeResponse? response, bool cached)
        => new(_ => (response, cached));

    private static readonly NarrativeResponse SampleNarrative = new(
        WhyReview: "Two Laredo→Dallas loads share a lane and delivery window.",
        WhatToVerify: "Confirm both PODs and combined weight under trailer capacity.",
        NextAction: "Assign to driver D-42 and combine into one trip.",
        Citations: ["load:100", "load:101"]);

    [Fact]
    public async Task Kill_switch_off_returns_404_disabled()
    {
        using var factory = new DemoFactory();
        var client = CreateClient(factory, narrativeEnabled: false, Returns(SampleNarrative, cached: false));

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("disabled", await ReadReasonAsync(response));
    }

    [Fact]
    public async Task Kill_switch_on_openai_outage_returns_503_ai_unavailable()
    {
        using var factory = new DemoFactory();
        // Outage convention: (null, Cached: true).
        var client = CreateClient(factory, narrativeEnabled: true, Returns(response: null, cached: true));

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("ai-unavailable", await ReadReasonAsync(response));
    }

    [Fact]
    public async Task Kill_switch_on_unknown_plan_returns_404_plan_not_found()
    {
        using var factory = new DemoFactory();
        // Unknown-plan convention: (null, Cached: false).
        var client = CreateClient(factory, narrativeEnabled: true, Returns(response: null, cached: false));

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("plan-not-found", await ReadReasonAsync(response));
    }

    [Fact]
    public async Task Kill_switch_on_populated_uncached_returns_200_with_cached_false_header()
    {
        using var factory = new DemoFactory();
        var client = CreateClient(factory, narrativeEnabled: true, Returns(SampleNarrative, cached: false));

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("azure-openai", Header(response, "X-Ai-Source"));
        Assert.Equal("false", Header(response, "X-Ai-Cached"));
        var body = await response.Content.ReadFromJsonAsync<NarrativeResponse>();
        Assert.NotNull(body);
        Assert.Equal(SampleNarrative.WhyReview, body!.WhyReview);
        Assert.Equal(SampleNarrative.Citations, body.Citations);
    }

    [Fact]
    public async Task Kill_switch_on_populated_cached_returns_200_with_cached_true_header()
    {
        using var factory = new DemoFactory();
        var client = CreateClient(factory, narrativeEnabled: true, Returns(SampleNarrative, cached: true));

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("azure-openai", Header(response, "X-Ai-Source"));
        Assert.Equal("true", Header(response, "X-Ai-Cached"));
    }

    [Fact]
    public async Task Unauthenticated_request_returns_401()
    {
        // Entra (JWT) auth mode with no bearer token — mirrors the /api/ltl/* 401 contract.
        using var factory = new TemplateWebApplicationFactory();
        var response = await factory.CreateClient().GetAsync(Route);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static async Task<string?> ReadReasonAsync(HttpResponseMessage response)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
    }
}
