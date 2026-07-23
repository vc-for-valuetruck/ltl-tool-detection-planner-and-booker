namespace LtlTool.Api.Features.Integrations.Yard;

/// <summary>
/// Server-side configuration for the inbound Yard webhook receiver. Bound from the
/// <c>Yard:Webhooks</c> configuration section (env vars <c>Yard__Webhooks__*</c>). The signing secret
/// lives here (config / Key Vault) and is <b>never</b> exposed to the SPA or written to any
/// <c>RUNTIME_*</c> surface.
///
/// <para>
/// The whole receiver is gated behind <see cref="Enabled"/> (default false), so a fresh clone / CI /
/// production never accepts Yard deliveries until an operator explicitly turns it on. Even when enabled
/// it fails closed: without a signing secret every request is rejected with 503.
/// </para>
/// </summary>
public sealed class YardWebhookOptions
{
    public const string SectionName = "Yard:Webhooks";

    /// <summary>
    /// Master switch for the receiver. When false the receiver endpoint returns 404 (the feature is
    /// off), so the Yard→LTL webhook boundary is dormant until deliberately enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The shared HMAC signing secret the Yard uses to sign the <c>X-Yard-Signature</c> header. When
    /// blank the receiver rejects every request (fail-closed): it never accepts an unverified event.
    /// </summary>
    public string Secret { get; set; } = "";

    /// <summary>
    /// Maximum accepted age of the signed timestamp, in seconds. Events older than this are rejected to
    /// blunt replay. Defaults to 300s (5 minutes) per the shared Yard↔LTL contract.
    /// </summary>
    public int ToleranceSeconds { get; set; } = 300;

    /// <summary>True when the receiver is enabled and a signing secret is configured.</summary>
    public bool HasSecret => !string.IsNullOrWhiteSpace(Secret);
}
