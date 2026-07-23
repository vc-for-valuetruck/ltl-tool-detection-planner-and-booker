using LtlTool.Api.Features.Ltl.Notifications;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.DispatchAssist;

/// <summary>
/// The Dispatch Assist notify step. On assembly it emails the assigned driver and dispatcher —
/// reusing the existing Microsoft Graph <c>sendMail</c> transport (<see cref="IGraphMailClient"/>,
/// same Entra app registration as the workflow notifications) — subject to two safety controls from
/// <see cref="DispatchCommsOptions"/>:
/// <list type="bullet">
///   <item><b>Master flag off</b> (<c>Ltl:Comms:Enabled=false</c>, the default) → nothing is sent;
///   the result is an honest <c>NotEnabled</c>, never a fabricated delivery.</item>
///   <item><b>Override recipient set</b> (<c>Ltl:Comms:OverrideRecipient</c>, default
///   <c>joshua.davis@valuetruck.com</c>) → all mail is rerouted to that single address and
///   <see cref="DispatchNotifyResult.OverrideActive"/> is true so the UI shows the banner. The real
///   driver/dispatcher are still reported as <see cref="DispatchNotifyResult.IntendedRecipients"/>.</item>
/// </list>
/// </summary>
public sealed class DispatchAssemblyNotifier(
    IGraphMailClient graph,
    IOptions<DispatchCommsOptions> commsOptions,
    IOptions<NotificationOptions> notificationOptions,
    ILogger<DispatchAssemblyNotifier> logger)
{
    private readonly DispatchCommsOptions _comms = commsOptions.Value;
    private readonly EmailChannelOptions _email = notificationOptions.Value.Email;

    /// <summary>
    /// Resolves the addresses a notification will <i>actually</i> be sent to, applying the override.
    /// Pure and side-effect-free so the override policy is unit-testable without a transport.
    /// </summary>
    public (IReadOnlyList<string> Effective, bool OverrideActive, string? OverrideRecipient)
        ResolveEffectiveRecipients(IReadOnlyList<DispatchNotifyRecipient> intended)
    {
        var overrideTo = _comms.EffectiveOverride;
        if (overrideTo is not null)
            return ([overrideTo], true, overrideTo);

        var addresses = intended
            .Select(r => r.Address)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (addresses, false, null);
    }

    /// <summary>
    /// Fires the notify step. Honest states: <c>NotEnabled</c> (flag off), <c>NoRecipients</c>
    /// (nothing resolved and no override), <c>Sent</c>, or <c>Failed</c>.
    /// </summary>
    public async Task<DispatchNotifyResult> NotifyAsync(
        IReadOnlyList<DispatchNotifyRecipient> intended, string subject, string body,
        CancellationToken ct)
    {
        var (effective, overrideActive, overrideTo) = ResolveEffectiveRecipients(intended);

        if (!_comms.Enabled)
        {
            return new DispatchNotifyResult
            {
                Sent = false,
                State = "NotEnabled",
                OverrideActive = overrideActive,
                OverrideRecipient = overrideTo,
                IntendedRecipients = intended,
                EffectiveRecipients = [],
                Detail = "Comms disabled (Ltl:Comms:Enabled=false). No email sent.",
            };
        }

        if (effective.Count == 0)
        {
            return new DispatchNotifyResult
            {
                Sent = false,
                State = "NoRecipients",
                OverrideActive = overrideActive,
                OverrideRecipient = overrideTo,
                IntendedRecipients = intended,
                EffectiveRecipients = [],
                Detail = "No resolvable recipient addresses from Alvys and no override configured.",
            };
        }

        if (!_email.IsConfigured)
        {
            return new DispatchNotifyResult
            {
                Sent = false,
                State = "Failed",
                OverrideActive = overrideActive,
                OverrideRecipient = overrideTo,
                IntendedRecipients = intended,
                EffectiveRecipients = effective,
                Detail = "Graph email transport not configured (Notifications:Email + Graph app registration).",
            };
        }

        var outcome = await graph.SendAsync(
            new GraphMailMessage { Subject = subject, Body = body, ToAddresses = effective }, ct);

        if (!outcome.Success)
            logger.LogWarning("Dispatch Assist notify send failed: {Detail}", outcome.Detail);

        return new DispatchNotifyResult
        {
            Sent = outcome.Success,
            State = outcome.Success ? "Sent" : "Failed",
            OverrideActive = overrideActive,
            OverrideRecipient = overrideTo,
            IntendedRecipients = intended,
            EffectiveRecipients = effective,
            Detail = outcome.Detail,
        };
    }
}
