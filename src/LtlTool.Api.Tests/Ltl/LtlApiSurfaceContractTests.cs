using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace LtlTool.Api.Tests;

/// <summary>
/// Phase 0 stability contract test. Locks the LTL API surface exactly as documented in
/// <c>CLAUDE.md</c> § "Current API surface (preserve)". Every endpoint below is expected to be
/// mapped AND protected: an unauthenticated request returns 401 (not 404), which proves both that
/// the route exists and that it sits behind <c>AllowedEmailDomain</c>.
///
/// <para>
/// If a rename, verb change, or accidental deletion drops one of these endpoints, this test fails
/// with a 404 instead of the expected 401 — catching contract regressions before UAT does.
/// Do not weaken this test by changing an assertion; if the surface intentionally evolves, update
/// this file AND <c>CLAUDE.md</c> together as the intentional API change.
/// </para>
///
/// <para>
/// Tagged <c>Category=ApiSurfaceContract</c> so it runs both in the full <c>api</c> job and in the
/// dedicated "Verify API Surface Contract" CI job, alongside the reflection-based
/// <see cref="LtlApiSurfaceManifestTests"/>.
/// </para>
/// </summary>
[Trait("Category", "ApiSurfaceContract")]
public sealed class LtlApiSurfaceContractTests(TemplateWebApplicationFactory factory)
    : IClassFixture<TemplateWebApplicationFactory>
{
    private readonly TemplateWebApplicationFactory _factory = factory;

    [Theory]
    [InlineData("/api/ltl/search")]
    [InlineData("/api/ltl/loads/100")]
    [InlineData("/api/ltl/loads/100/matches")]
    [InlineData("/api/ltl/loads/100/matches?top=3")]
    [InlineData("/api/ltl/loads/100/billing-readiness")]
    [InlineData("/api/ltl/loads/100/accessorial-review")]
    [InlineData("/api/ltl/loads/100/assignments")]
    [InlineData("/api/ltl/billing/worklist")]
    [InlineData("/api/ltl/billing/worklist?badge=ReadyToBill")]
    [InlineData("/api/ltl/exceptions")]
    [InlineData("/api/ltl/consolidation/candidates?loadId=100")]
    [InlineData("/api/ltl/consolidation/candidates?loadId=100&corridor=LAREDO_TO_DALLAS")]
    [InlineData("/api/ltl/consolidation/plan/audits")]
    [InlineData("/api/ltl/consolidation/plan/audits?parentLoadId=L-100234")]
    [InlineData("/api/ltl/notifications")]
    [InlineData("/api/ltl/notifications?max=25")]
    [InlineData("/api/ltl/notifications/channels")]
    [InlineData("/api/ltl/reporting/margin-rollup")]
    [InlineData("/api/ltl/reporting/margin-rollup?groupBy=Rep")]
    [InlineData("/api/ltl/reporting/margin-rollup/export")]
    [InlineData("/api/ltl/reporting/margin-rollup/export?groupBy=Lane")]
    public async Task Ltl_get_routes_are_mapped_and_protected(string route)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(route);

        // 401 means the route was matched AND the AllowedEmailDomain policy blocked it.
        // A 404 here would mean the route was renamed or deleted — a contract regression.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/ltl/loads/100/assign/validate")]
    [InlineData("/api/ltl/loads/100/assign")]
    [InlineData("/api/ltl/consolidation/plan")]
    [InlineData("/api/ltl/consolidation/plan/audit")]
    public async Task Ltl_post_routes_are_mapped_and_protected(string route)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(route, new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
