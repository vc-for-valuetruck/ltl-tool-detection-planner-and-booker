namespace LtlTool.Api.Features.Ltl.DispatchAssist;

/// <summary>
/// Configuration for the Dispatch Assist notify step, bound from the <c>Ltl:Comms</c> section.
///
/// <para>
/// Two independent safety controls:
/// <list type="bullet">
///   <item><b><see cref="Enabled"/></b> — the master flag, default <c>false</c>. A fresh clone, CI
///   and the demo never send a real email; the notify step reports <c>NotEnabled</c> honestly.</item>
///   <item><b><see cref="OverrideRecipient"/></b> — when non-empty (default
///   <c>joshua.davis@valuetruck.com</c>), <em>every</em> outbound message is rerouted to this single
///   address regardless of the Alvys-resolved driver/dispatcher, and the UI shows a banner. Clearing
///   it (empty string) is the deliberate action required to reach real driver/dispatcher inboxes.</item>
/// </list>
/// The Graph transport itself (sender mailbox, app registration/secret) is reused from the existing
/// <c>Notifications:Email</c> wiring and stays server-side only — no secret lives in this section.
/// </para>
/// </summary>
public sealed class DispatchCommsOptions
{
    public const string SectionName = "Ltl:Comms";

    /// <summary>Master gate for the notify step. Default OFF.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Safe reroute address. When non-empty, all notify mail goes here instead of the real
    /// driver/dispatcher. Defaults to the demo-safe mailbox so a mis-config can never surprise a
    /// real driver; set to empty to send to the Alvys-resolved recipients.
    /// </summary>
    public string? OverrideRecipient { get; set; } = "joshua.davis@valuetruck.com";

    /// <summary>Trimmed override, or null when blank.</summary>
    public string? EffectiveOverride =>
        string.IsNullOrWhiteSpace(OverrideRecipient) ? null : OverrideRecipient.Trim();
}
