using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Notifications;

/// <summary>
/// A delivery transport for notifications. Each channel reports its own configured state and
/// returns an honest <see cref="NotificationDelivery"/> — never a fabricated "Delivered". The
/// engine fans a fired event out across every registered channel that has recipients.
/// </summary>
public interface INotificationChannel
{
    NotificationChannelKind Kind { get; }

    /// <summary>Whether the channel has the server-side config it needs to actually deliver.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Delivers <paramref name="evt"/> to <paramref name="recipients"/>. Returns the honest
    /// outcome; a not-configured channel returns <see cref="NotificationDeliveryState.NotConfigured"/>
    /// without pretending anything was sent.
    /// </summary>
    Task<NotificationDelivery> SendAsync(
        NotificationEvent evt,
        IReadOnlyList<NotificationRecipient> recipients,
        CancellationToken ct);
}

/// <summary>
/// Always-on in-app channel. "Delivery" is the event being written to the feed store, which the
/// bell/feed UI reads — so it is genuinely delivered the moment the engine records it. This is
/// the channel that makes triggers demoable without any external configuration.
/// </summary>
public sealed class InAppNotificationChannel : INotificationChannel
{
    public NotificationChannelKind Kind => NotificationChannelKind.InApp;
    public bool IsConfigured => true;

    public Task<NotificationDelivery> SendAsync(
        NotificationEvent evt,
        IReadOnlyList<NotificationRecipient> recipients,
        CancellationToken ct) =>
        Task.FromResult(new NotificationDelivery
        {
            Channel = Kind,
            State = NotificationDeliveryState.Delivered,
            Recipients = recipients.Select(r => r.Name).ToArray(),
            Detail = "Shown in the in-app notification feed.",
        });
}

/// <summary>
/// Microsoft Teams channel via incoming webhook. Disabled (honest "not configured") until
/// <c>Notifications:Teams:WebhookUrl</c> is set server-side. When configured it POSTs a simple
/// message card; a transport failure is reported as <see cref="NotificationDeliveryState.Failed"/>,
/// never a false success. The webhook URL is a server-side secret and is never returned to the SPA.
/// </summary>
public sealed class TeamsNotificationChannel(
    HttpClient http,
    IOptions<NotificationOptions> options,
    ILogger<TeamsNotificationChannel> logger) : INotificationChannel
{
    private readonly TeamsChannelOptions _teams = options.Value.Teams;

    public NotificationChannelKind Kind => NotificationChannelKind.Teams;
    public bool IsConfigured => _teams.IsConfigured;

    public async Task<NotificationDelivery> SendAsync(
        NotificationEvent evt,
        IReadOnlyList<NotificationRecipient> recipients,
        CancellationToken ct)
    {
        var names = recipients.Select(r => r.Name).ToArray();
        if (!IsConfigured)
        {
            return new NotificationDelivery
            {
                Channel = Kind,
                State = NotificationDeliveryState.NotConfigured,
                Recipients = names,
                Detail = "Teams webhook not configured (set Notifications:Teams:WebhookUrl).",
            };
        }

        try
        {
            // MessageCard is the schema Teams incoming webhooks accept without Graph auth.
            var card = new
            {
                @type = "MessageCard",
                @context = "https://schema.org/extensions",
                summary = evt.Title,
                title = evt.Title,
                text = evt.Summary,
            };
            var response = await http.PostAsJsonAsync(_teams.WebhookUrl, card, ct);
            if (response.IsSuccessStatusCode)
            {
                return new NotificationDelivery
                {
                    Channel = Kind,
                    State = NotificationDeliveryState.Delivered,
                    Recipients = names,
                    Detail = "Posted to Teams channel.",
                };
            }

            logger.LogWarning(
                "Teams webhook returned {StatusCode} for notification {Stage}.",
                (int)response.StatusCode, evt.Stage);
            return new NotificationDelivery
            {
                Channel = Kind,
                State = NotificationDeliveryState.Failed,
                Recipients = names,
                Detail = $"Teams webhook returned HTTP {(int)response.StatusCode}.",
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Teams webhook post failed for notification {Stage}.", evt.Stage);
            return new NotificationDelivery
            {
                Channel = Kind,
                State = NotificationDeliveryState.Failed,
                Recipients = names,
                Detail = "Teams webhook post failed.",
            };
        }
    }
}

/// <summary>
/// Email channel. Disabled (honest "not configured") until <c>Notifications:Email</c> is enabled
/// with an SMTP host + from-address server-side. The SMTP transport itself is deliberately not
/// wired in this first slice: when configured the channel reports
/// <see cref="NotificationDeliveryState.Pending"/> with an honest detail rather than fabricating a
/// delivery. Wiring a real SMTP/Graph send is a documented follow-up.
/// </summary>
public sealed class EmailNotificationChannel(IOptions<NotificationOptions> options) : INotificationChannel
{
    private readonly EmailChannelOptions _email = options.Value.Email;

    public NotificationChannelKind Kind => NotificationChannelKind.Email;
    public bool IsConfigured => _email.IsConfigured;

    public Task<NotificationDelivery> SendAsync(
        NotificationEvent evt,
        IReadOnlyList<NotificationRecipient> recipients,
        CancellationToken ct)
    {
        var names = recipients.Select(r => r.Name).ToArray();
        var delivery = IsConfigured
            ? new NotificationDelivery
            {
                Channel = Kind,
                State = NotificationDeliveryState.Pending,
                Recipients = names,
                Detail = "Email configured; SMTP transport not wired in this slice.",
            }
            : new NotificationDelivery
            {
                Channel = Kind,
                State = NotificationDeliveryState.NotConfigured,
                Recipients = names,
                Detail = "Email not configured (set Notifications:Email:Enabled + SmtpHost + FromAddress).",
            };
        return Task.FromResult(delivery);
    }
}
