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

/// <summary>Email settings. Disabled unless <see cref="Enabled"/> and an SMTP host are present.</summary>
public sealed class EmailChannelOptions
{
    public bool Enabled { get; set; }
    public string? SmtpHost { get; set; }
    public string? FromAddress { get; set; }
    public bool IsConfigured => Enabled
        && !string.IsNullOrWhiteSpace(SmtpHost)
        && !string.IsNullOrWhiteSpace(FromAddress);
}
