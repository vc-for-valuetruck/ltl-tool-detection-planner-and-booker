using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Integrations.Alvys;

/// <summary>
/// Read-only self-test for the Live Alvys data path. Unlike <see cref="AlvysClient"/> — which
/// deliberately swallows every non-2xx into an empty result so the workbench degrades gracefully —
/// this probe reports the <em>real</em> HTTP status of a token acquisition and one tiny
/// <c>loads/search</c> read, so ops can tell "Live but Alvys is rejecting us" apart from "Live and
/// healthy" without reading server logs.
///
/// <para><b>Secret posture.</b> The bearer token is never returned or logged. Success bodies (which
/// carry live operational load data) are <b>never</b> echoed — only that the read returned 2xx.
/// Non-2xx bodies are Alvys error envelopes (no operational payload); a short, truncated snippet is
/// surfaced because that is exactly what pins a 401/403/404 to its cause.</para>
/// </summary>
public interface IAlvysDiagnostics
{
    Task<AlvysDiagnosticsReport> ProbeAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IAlvysDiagnostics"/>
public sealed class AlvysDiagnostics(
    IHttpClientFactory httpClientFactory,
    IAlvysTokenProvider tokenProvider,
    IOptions<AlvysOptions> options,
    ILogger<AlvysDiagnostics> logger) : IAlvysDiagnostics
{
    /// <summary>
    /// Fallback versions to probe when the configured version does not answer 2xx. Bounded and
    /// read-only — this lets a single deploy prove whether the failure is a version-segment
    /// mismatch (the configured <c>v{n}</c> path is wrong) versus an auth/scope/tenant rejection
    /// (every version returns the same 401/403).
    /// </summary>
    private static readonly string[] CandidateVersions = ["v1", "v2", "v2.0"];

    private const int BodySnippetMaxChars = 400;

    private readonly AlvysOptions _options = options.Value;

    public async Task<AlvysDiagnosticsReport> ProbeAsync(CancellationToken ct = default)
    {
        var provider = _options.Provider.ToString();
        var configuredVersion = AlvysApiRoutes.NormalizeVersion(_options.ApiVersion);

        if (_options.Provider != AlvysProvider.Live)
        {
            return Report(provider, configuredVersion, tokenAcquired: false,
                tokenDetail: "Provider is not Live — no live Alvys call attempted.",
                probes: [], recommendation: null,
                summary: "Fallback provider configured; the workbench serves empty results by design.");
        }

        if (!_options.HasCredentials)
        {
            return Report(provider, configuredVersion, tokenAcquired: false,
                tokenDetail: "Alvys credentials are not configured (Alvys:ClientId / Alvys:ClientSecret).",
                probes: [], recommendation: "Set server-side Alvys credentials for the Live provider.",
                summary: "Live provider selected but credentials are absent — no live data can be served.");
        }

        // --- Token acquisition ---
        try
        {
            await tokenProvider.GetAccessTokenAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("Alvys diagnostic token acquisition failed: {Message}", ex.Message);
            return Report(provider, configuredVersion, tokenAcquired: false,
                tokenDetail: $"Token request failed: {ex.Message}",
                probes: [], recommendation: "Verify Alvys OAuth2 credentials / audience / token endpoint.",
                summary: "Could not acquire an Alvys access token — the read path cannot authenticate.");
        }

        // --- Configured-version probe ---
        var probes = new List<AlvysProbeResult> { await ProbeVersionAsync(configuredVersion, ct) };
        var configured = probes[0];
        if (configured.Ok)
        {
            return Report(provider, configuredVersion, tokenAcquired: true, tokenDetail: "Token acquired.",
                probes: probes, recommendation: null,
                summary: $"Live Alvys reachable — loads/search returned HTTP {configured.StatusCode} on {configuredVersion}.");
        }

        // --- Fallback-version probes (only when the configured version failed) ---
        foreach (var candidate in CandidateVersions)
        {
            if (string.Equals(AlvysApiRoutes.NormalizeVersion(candidate), configuredVersion,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            probes.Add(await ProbeVersionAsync(AlvysApiRoutes.NormalizeVersion(candidate), ct));
        }

        var working = probes.FirstOrDefault(p => p.Ok);
        string? recommendation;
        string summary;
        if (working is not null)
        {
            recommendation =
                $"loads/search succeeds on {working.Version} but not the configured {configuredVersion}. " +
                $"Set Alvys:ApiVersion={working.Version} (env Alvys__ApiVersion / ALVYS_API_VERSION).";
            summary = $"Configured version {configuredVersion} was rejected (HTTP {configured.StatusCode}); " +
                      $"{working.Version} answered HTTP {working.StatusCode}.";
        }
        else
        {
            recommendation =
                "Token acquired but every probed version was rejected — the credentials authenticate but " +
                "are not authorized to read this tenant. Confirm the Alvys API app is provisioned for the " +
                "target tenant and granted load read scope.";
            summary = $"Token OK, but loads/search returned HTTP {configured.StatusCode} on every probed version.";
        }

        return Report(provider, configuredVersion, tokenAcquired: true, tokenDetail: "Token acquired.",
            probes: probes, recommendation: recommendation, summary: summary);
    }

    private async Task<AlvysProbeResult> ProbeVersionAsync(string version, CancellationToken ct)
    {
        var path = AlvysApiRoutes.LoadsSearch(version);
        try
        {
            var client = httpClientFactory.CreateClient(AlvysClient.ApiHttpClientName);
            var token = await tokenProvider.GetAccessTokenAsync(ct);

            var body = new LoadSearchRequest { Page = 0, PageSize = 1 };
            body.EnsureConditionalFilter();

            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, ct);
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                // Never echo the success body — it carries live operational load data.
                return new AlvysProbeResult(version, path, status, Ok: true,
                    Detail: "loads/search returned success.");
            }

            // Non-2xx: an Alvys error envelope (no operational payload). Surface a short, secret-free
            // snippet — this is what distinguishes 401 vs 403 vs a version-mismatch 404.
            var raw = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Alvys diagnostic {Path} returned HTTP {StatusCode}.", path, status);
            return new AlvysProbeResult(version, path, status, Ok: false, Detail: Snippet(raw));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("Alvys diagnostic {Path} transport error: {Message}", path, ex.Message);
            return new AlvysProbeResult(version, path, StatusCode: null, Ok: false,
                Detail: $"Transport error: {ex.GetType().Name}.");
        }
    }

    private static string? Snippet(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var trimmed = body.Trim();
        return trimmed.Length <= BodySnippetMaxChars
            ? trimmed
            : trimmed[..BodySnippetMaxChars] + "…";
    }

    private AlvysDiagnosticsReport Report(
        string provider, string configuredVersion, bool tokenAcquired, string tokenDetail,
        IReadOnlyList<AlvysProbeResult> probes, string? recommendation, string summary)
        => new(
            Provider: provider,
            ApiBaseUrl: _options.ApiBaseUrl,
            ConfiguredVersion: configuredVersion,
            CredentialsPresent: _options.HasCredentials,
            TokenAcquired: tokenAcquired,
            TokenDetail: tokenDetail,
            Probes: probes,
            Recommendation: recommendation,
            Summary: summary);
}

/// <summary>One version's <c>loads/search</c> probe outcome. Carries no secret and no success body.</summary>
public sealed record AlvysProbeResult(
    string Version, string Path, int? StatusCode, bool Ok, string? Detail);

/// <summary>
/// Read-only Alvys self-test result. Exposes provider/version posture, token acquisition outcome,
/// and per-version probe statuses with a secret-free recommendation. No token, no success payload.
/// </summary>
public sealed record AlvysDiagnosticsReport(
    string Provider,
    string ApiBaseUrl,
    string ConfiguredVersion,
    bool CredentialsPresent,
    bool TokenAcquired,
    string? TokenDetail,
    IReadOnlyList<AlvysProbeResult> Probes,
    string? Recommendation,
    string Summary);
