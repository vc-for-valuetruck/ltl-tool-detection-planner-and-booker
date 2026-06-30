namespace LtlTool.Api.Features.Integrations.Alvys;

/// <summary>
/// Selects which <see cref="IAlvysClient"/> implementation backs LTL data.
/// </summary>
public enum AlvysProvider
{
    /// <summary>
    /// Live Alvys TMS API — the default and the source of truth for all LTL data
    /// in production-like environments.
    /// </summary>
    Live = 0,

    /// <summary>
    /// In-process stub that returns empty results. Local/UAT fallback only; must
    /// never be the configured provider in production-like environments.
    /// </summary>
    Fallback = 1,
}

/// <summary>
/// Server-side configuration for the Alvys TMS integration. Bound from the
/// "Alvys" configuration section (env vars <c>Alvys__*</c> / <c>ALVYS_*</c>).
///
/// Credentials are read on the server only and must never be surfaced to the
/// Angular SPA.
/// </summary>
public sealed class AlvysOptions
{
    public const string SectionName = "Alvys";

    /// <summary>
    /// Provider backing LTL data. Defaults to <see cref="AlvysProvider.Live"/>
    /// so live Alvys is the source of truth unless explicitly overridden.
    /// </summary>
    public AlvysProvider Provider { get; set; } = AlvysProvider.Live;

    /// <summary>Base URL for the Alvys public API (no trailing slash required).</summary>
    public string ApiBaseUrl { get; set; } = "https://integrations.alvys.com/api/p/v1";

    /// <summary>OAuth2 token endpoint for the client-credentials flow.</summary>
    public string TokenUrl { get; set; } = "https://auth.alvys.com/oauth/token";

    /// <summary>OAuth2 audience claim requested for the access token.</summary>
    public string Audience { get; set; } = "https://api.alvys.com/public/";

    /// <summary>Alvys tenant identifier (informational; scopes the credentials).</summary>
    public string TenantId { get; set; } = "";

    /// <summary>OAuth2 client_id from Alvys Admin → API Access. Secret-adjacent.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>OAuth2 client_secret. Secret — never logged or returned to clients.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>Per-request timeout for outbound Alvys calls, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// True when both OAuth2 credentials are present. The live client can only
    /// authenticate when this holds.
    /// </summary>
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
