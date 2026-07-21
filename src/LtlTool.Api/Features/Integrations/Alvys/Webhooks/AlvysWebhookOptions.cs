namespace LtlTool.Api.Features.Integrations.Alvys.Webhooks;

/// <summary>
/// Server-side configuration for the Alvys webhook receiver. Bound from the <c>Alvys:Webhooks</c>
/// configuration section (env vars <c>Alvys__Webhooks__*</c>). The signing secret lives here (config
/// / Key Vault) and is <b>never</b> exposed to the SPA or written to any <c>RUNTIME_*</c> surface.
///
/// <para>
/// The receiver verifies an HMAC-SHA256 signature over the timestamp + raw body and rejects stale
/// events, so it is safe to expose anonymously (it is deliberately NOT behind the
/// <c>AllowedEmailDomain</c> policy — Alvys is a machine caller with no email identity).
/// </para>
/// </summary>
public sealed class AlvysWebhookOptions
{
    public const string SectionName = "Alvys:Webhooks";

    /// <summary>
    /// The shared HMAC signing secret Alvys uses to sign the <c>X-Alvys-Signature</c> header. When
    /// blank the receiver rejects every request (fail-closed): it never accepts an unverified event.
    /// </summary>
    public string Secret { get; set; } = "";

    /// <summary>
    /// Maximum accepted age of the signed timestamp, in seconds. Events older than this are rejected
    /// to blunt replay. Defaults to 300s (5 minutes) per the Alvys webhook contract.
    /// </summary>
    public int ToleranceSeconds { get; set; } = 300;

    /// <summary>
    /// Alvys auto-disables a webhook subscription after this many consecutive failed <b>events</b>, so
    /// the receiver must acknowledge fast. Surfaced on the admin panel as an operational reminder; the
    /// receiver itself never trips this — it exists to document the upstream behaviour. Defaults to 10.
    /// </summary>
    public int AutoDisableThreshold { get; set; } = 10;

    /// <summary>True when a signing secret is configured, so signature verification can run.</summary>
    public bool HasSecret => !string.IsNullOrWhiteSpace(Secret);
}
