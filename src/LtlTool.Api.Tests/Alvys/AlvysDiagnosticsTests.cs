using System.Net;
using System.Text.Json;
using LtlTool.Api.Features.Integrations.Alvys;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

public sealed class AlvysDiagnosticsTests
{
    private sealed class StubTokenProvider(string token = "test-token") : IAlvysTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult(token);
    }

    private sealed class ThrowingTokenProvider(Exception ex) : IAlvysTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromException<string>(ex);
    }

    private static AlvysDiagnostics Build(
        StubHttpMessageHandler handler, AlvysOptions options, IAlvysTokenProvider? token = null)
        => new(
            new StubHttpClientFactory(handler, new Uri("https://alvys.test/")),
            token ?? new StubTokenProvider(),
            Options.Create(options),
            new CapturingLogger<AlvysDiagnostics>());

    private static AlvysOptions LiveWithCreds(string version = "v1") => new()
    {
        Provider = AlvysProvider.Live,
        ApiVersion = version,
        ClientId = "cid",
        ClientSecret = "secret",
    };

    private static StubHttpMessageHandler AlwaysReturns(HttpStatusCode status, string body = "{}")
        => new((_, _) => new HttpResponseMessage(status) { Content = new StringContent(body) });

    [Fact]
    public async Task Fallback_provider_does_not_call_alvys()
    {
        var handler = AlwaysReturns(HttpStatusCode.OK);
        var diag = Build(handler, new AlvysOptions { Provider = AlvysProvider.Fallback });

        var report = await diag.ProbeAsync();

        Assert.False(report.TokenAcquired);
        Assert.Empty(report.Probes);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Live_without_credentials_reports_missing_and_probes_nothing()
    {
        var handler = AlwaysReturns(HttpStatusCode.OK);
        var diag = Build(handler, new AlvysOptions { Provider = AlvysProvider.Live });

        var report = await diag.ProbeAsync();

        Assert.False(report.CredentialsPresent);
        Assert.False(report.TokenAcquired);
        Assert.Empty(report.Probes);
        Assert.NotNull(report.Recommendation);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Successful_probe_reports_ok_without_recommendation_or_body()
    {
        var handler = AlwaysReturns(HttpStatusCode.OK,
            """{"Page":0,"PageSize":1,"Total":1,"Items":[{"Id":"L1","LoadNumber":"100"}]}""");
        var diag = Build(handler, LiveWithCreds("v1"));

        var report = await diag.ProbeAsync();

        Assert.True(report.TokenAcquired);
        var probe = Assert.Single(report.Probes);
        Assert.True(probe.Ok);
        Assert.Equal(200, probe.StatusCode);
        Assert.Null(report.Recommendation);
        // The live load payload must never be echoed back through the anonymous probe.
        Assert.DoesNotContain("LoadNumber", JsonSerializer.Serialize(report));
        Assert.DoesNotContain("L1", JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task Configured_version_404_but_alternate_200_recommends_the_working_version()
    {
        // Configured v1 is rejected; the real Alvys public API answers on v2.0. The probe must
        // discover that in one pass and recommend the fix.
        var handler = new StubHttpMessageHandler((req, _) =>
            req.RequestUri!.AbsolutePath.Contains("/v2.0/")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"Items\":[]}") }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"no route\"}") });
        var diag = Build(handler, LiveWithCreds("v1"));

        var report = await diag.ProbeAsync();

        Assert.True(report.TokenAcquired);
        Assert.Contains(report.Probes, p => p.Version == "v1" && p.StatusCode == 404 && !p.Ok);
        var working = Assert.Single(report.Probes, p => p.Ok);
        Assert.Equal("v2.0", working.Version);
        Assert.NotNull(report.Recommendation);
        Assert.Contains("v2.0", report.Recommendation);
    }

    [Fact]
    public async Task All_versions_403_reports_authorization_cause_and_never_leaks_token()
    {
        var handler = AlwaysReturns(HttpStatusCode.Forbidden, """{"error":"forbidden for tenant"}""");
        var diag = Build(handler, LiveWithCreds("v1"), new StubTokenProvider("super-secret-token"));

        var report = await diag.ProbeAsync();

        Assert.True(report.TokenAcquired);
        Assert.All(report.Probes, p => Assert.False(p.Ok));
        Assert.All(report.Probes, p => Assert.Equal(403, p.StatusCode));
        // The secret-free error envelope is surfaced to pin the 403 cause…
        Assert.Contains(report.Probes, p => p.Detail is not null && p.Detail.Contains("forbidden"));
        Assert.NotNull(report.Recommendation);
        // …but the bearer token must never appear anywhere in the serialized report.
        Assert.DoesNotContain("super-secret-token", JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task Token_acquisition_failure_is_reported_without_probing()
    {
        var handler = AlwaysReturns(HttpStatusCode.OK);
        var diag = Build(handler, LiveWithCreds("v1"),
            new ThrowingTokenProvider(new InvalidOperationException("token endpoint rejected request")));

        var report = await diag.ProbeAsync();

        Assert.False(report.TokenAcquired);
        Assert.Empty(report.Probes);
        Assert.Empty(handler.Calls);
        Assert.Contains("token endpoint rejected request", report.TokenDetail);
    }
}
