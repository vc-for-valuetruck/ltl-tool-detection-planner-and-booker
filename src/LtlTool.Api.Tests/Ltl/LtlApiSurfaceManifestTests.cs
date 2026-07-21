using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LtlTool.Api.Tests;

/// <summary>
/// Phase 0 stability contract test — the reflection half of the API-surface guard.
///
/// <para>
/// Where <see cref="LtlApiSurfaceContractTests"/> proves each documented route is mapped AND
/// protected by firing an unauthenticated HTTP request (401 vs 404), this test reflects over the
/// fully-materialized ASP.NET route table (<see cref="EndpointDataSource"/>) and asserts every
/// entry in the checked-in <c>Ltl/ltl-api-surface.manifest.txt</c> manifest — which mirrors
/// <c>CLAUDE.md</c> § "Current API surface (preserve)" — is still mapped with the documented verb.
/// A route rename, verb change, or accidental deletion drops the match and fails the build.
/// </para>
///
/// <para>
/// The manifest is the single checked-in expected-surface source. If the surface intentionally
/// evolves, update the manifest AND <c>CLAUDE.md</c> together — that paired edit is the deliberate
/// change signal. This test only asserts the documented surface is a subset of what is mapped; it
/// does not fail on additional (not-yet-documented) routes, so it never blocks additive work.
/// </para>
/// </summary>
[Trait("Category", "ApiSurfaceContract")]
public sealed class LtlApiSurfaceManifestTests(TemplateWebApplicationFactory factory)
    : IClassFixture<TemplateWebApplicationFactory>
{
    private readonly TemplateWebApplicationFactory _factory = factory;

    private static readonly Regex RouteParam = new(@"\{[^}]+\}", RegexOptions.Compiled);

    [Fact]
    public void Documented_ltl_surface_is_still_mapped_with_matching_verbs()
    {
        var mapped = ReflectMappedLtlRoutes();
        var documented = ReadManifest();

        var missing = documented.Where(entry => !mapped.Contains(entry)).OrderBy(e => e).ToList();

        Assert.True(
            missing.Count == 0,
            "The following routes documented in CLAUDE.md § \"Current API surface (preserve)\" " +
            "are no longer mapped with the expected verb — a rename, verb change, or deletion " +
            "broke the contract. If the change was intentional, update the manifest " +
            "(Ltl/ltl-api-surface.manifest.txt) AND CLAUDE.md together:" +
            Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", missing) +
            Environment.NewLine + Environment.NewLine +
            "Actual mapped api/ltl routes were:" + Environment.NewLine + "  " +
            string.Join(Environment.NewLine + "  ", mapped.OrderBy(r => r)));
    }

    private HashSet<string> ReflectMappedLtlRoutes()
    {
        // WebApplicationFactory.Services is the running server's provider; EndpointDataSource is
        // registered by app.MapControllers() and exposes the fully-materialized route table.
        using var scope = _factory.Services.CreateScope();
        var dataSource = scope.ServiceProvider.GetRequiredService<EndpointDataSource>();

        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in dataSource.Endpoints.OfType<RouteEndpoint>())
        {
            var raw = endpoint.RoutePattern.RawText;
            if (string.IsNullOrEmpty(raw))
            {
                continue;
            }

            var path = Normalize(raw);
            if (!path.StartsWith("api/ltl", StringComparison.Ordinal))
            {
                continue;
            }

            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            if (methods is null)
            {
                continue;
            }

            foreach (var method in methods)
            {
                routes.Add($"{method.ToUpperInvariant()} {path}");
            }
        }

        return routes;
    }

    private static List<string> ReadManifest()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "Ltl", "ltl-api-surface.manifest.txt");
        Assert.True(File.Exists(manifestPath), $"API surface manifest not found at {manifestPath}");

        var entries = new List<string>();
        foreach (var line in File.ReadAllLines(manifestPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            Assert.True(parts.Length == 2, $"Malformed manifest line (expected '<VERB> <route>'): {line}");
            entries.Add($"{parts[0].ToUpperInvariant()} {Normalize(parts[1])}");
        }

        Assert.NotEmpty(entries);
        return entries;
    }

    private static string Normalize(string route) =>
        RouteParam.Replace(route.Trim().TrimStart('/'), "{}").ToLowerInvariant();
}
