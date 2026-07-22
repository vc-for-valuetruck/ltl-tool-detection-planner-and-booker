namespace LtlTool.Api.Features.Ltl.Notifications;

/// <summary>
/// Configuration for the LTL workflow notification engine, bound from the <c>Notifications</c>
/// section. All external channels default to off/unconfigured so a fresh clone, CI and the demo
/// only ever exercise the always-on in-app feed — Teams/email honestly report "not configured".
/// No secrets live here beyond the operator-supplied webhook/SMTP settings (server-side only).
/// </summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>How often the trigger engine diffs source state. Clamped to a sane floor at use.</summary>
    public int PollIntervalSeconds { get; set; } = 20;

    /// <summary>Max notification events retained in the in-memory feed store.</summary>
    public int FeedCapacity { get; set; } = 500;

    public TeamsChannelOptions Teams { get; set; } = new();
    public EmailChannelOptions Email { get; set; } = new();

    /// <summary>
    /// Per-stage recipient groups, keyed by <see cref="NotificationStage"/> name. When a stage has
    /// no configured group the engine falls back to a built-in default (in-app role labels), so the
    /// feed always shows who should have been aligned even before an operator customises recipients.
    /// </summary>
    public Dictionary<string, List<RecipientConfig>> Recipients { get; set; } = new();
}

/// <summary>A configured recipient (name + channel + optional address) for a stage group.</summary>
public sealed class RecipientConfig
{
    public string Name { get; set; } = string.Empty;
    public NotificationChannelKind Channel { get; set; } = NotificationChannelKind.InApp;
    public string? Address { get; set; }
}

/// <summary>Teams incoming-webhook settings. Disabled unless <see cref="WebhookUrl"/> is present.</summary>
public sealed class TeamsChannelOptions
{
    public string? WebhookUrl { get; set; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(WebhookUrl);
}

/// <summary>
/// Email settings for the Microsoft Graph <c>sendMail</c> transport. Disabled unless
/// <see cref="Enabled"/>, a sender mailbox (<see cref="FromAddress"/>) and a fully-populated
/// <see cref="Graph"/> app registration are all present server-side. All Graph values are secrets
/// or tenant identifiers supplied server-side (config / Key Vault) — none are ever returned to the SPA.
/// </summary>
public sealed class EmailChannelOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// The sender mailbox the Graph <c>/users/{FromAddress}/sendMail</c> call posts as. Must be a real
    /// licensed/shared mailbox the app registration is permitted to send on behalf of.
    /// </summary>
    public string? FromAddress { get; set; }

    /// <summary>Microsoft Graph client-credentials app registration used to send mail.</summary>
    public GraphMailOptions Graph { get; set; } = new();

    /// <summary>Max send attempts (initial + retries) before a transient failure is reported Failed.</summary>
    public int MaxSendAttempts { get; set; } = 3;

    /// <summary>Base backoff between retries; the nth retry waits Base * 2^(n-1). Small so ops isn't blocked.</summary>
    public int RetryBaseDelayMs { get; set; } = 500;

    public bool IsConfigured => Enabled
        && !string.IsNullOrWhiteSpace(FromAddress)
        && Graph.IsConfigured;
}

/// <summary>
/// Microsoft Graph client-credentials (app-only) settings. The app registration needs the
/// <c>Mail.Send</c> <b>application</b> permission with tenant admin consent. Disabled unless the
/// tenant id, client id and client secret are all present. Secret lives server-side only.
/// </summary>
public sealed class GraphMailOptions
{
    /// <summary>Entra tenant id (GUID) the app registration lives in.</summary>
    public string? TenantId { get; set; }

    /// <summary>Application (client) id of the app registration.</summary>
    public string? ClientId { get; set; }

    /// <summary>Client secret (or cert-derived secret) — server-side only, never logged, never sent to the SPA.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>OIDC authority host; overridable only for sovereign clouds. Defaults to the public cloud.</summary>
    public string Instance { get; set; } = "https://login.microsoftonline.com/";

    /// <summary>Graph base URL; overridable only for sovereign clouds. Defaults to the public cloud.</summary>
    public string GraphBaseUrl { get; set; } = "https://graph.microsoft.com/v1.0/";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);
}
