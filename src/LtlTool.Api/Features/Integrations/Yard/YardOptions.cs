namespace LtlTool.Api.Features.Integrations.Yard;

/// <summary>
/// Server-side configuration for the Yard integration (the adjacent yard-management system that owns
/// gate scans, driver check-in and security-hold release review). Bound from the "Yard" configuration
/// section (env vars <c>Yard__*</c>). Credentials are read on the server only and must never be
/// surfaced to the Angular SPA.
///
/// <para>
/// The Yard is a peer system, not a source of operational truth: it supplies a read-only <b>presence</b>
/// signal (is the equipment/driver physically at the yard, was the load released) that the LTL tool
/// folds into assignment validation and the dock flow. Alvys remains authoritative for all load,
/// driver, truck, trailer, customer and billing data.
/// </para>
/// </summary>
public sealed class YardOptions
{
    public const string SectionName = "Yard";

    /// <summary>Host root for the Yard presence API (no trailing slash required).</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Entra ID (Azure AD) token endpoint for the client-credentials flow.</summary>
    public string TokenUrl { get; set; } = "";

    /// <summary>Entra tenant identifier that issues the token.</summary>
    public string TenantId { get; set; } = "";

    /// <summary>OAuth2 client_id for the Yard API. Secret-adjacent.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>OAuth2 client_secret. Secret — never logged or returned to clients.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// OAuth2 scope requested for the Yard access token (Entra exposes downstream APIs as a scope,
    /// e.g. <c>api://yard/.default</c>). Mirrors the Alvys audience/scope pattern.
    /// </summary>
    public string ApiScope { get; set; } = "";

    /// <summary>Per-request timeout for outbound Yard calls, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// How long a presence snapshot is cached, in seconds. Presence is volatile (a truck can leave the
    /// yard at any moment) so the cache is deliberately short. Defaults to 10s per the boundary contract.
    /// </summary>
    public int PresenceCacheSeconds { get; set; } = 10;

    /// <summary>
    /// True when a base URL and both OAuth2 credentials are present. The presence client only attempts a
    /// live call when this holds; otherwise it degrades to null (never throws at startup).
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);
}
