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
/// Email channel backed by Microsoft Graph <c>sendMail</c> (app-only / client-credentials, fitting
/// the existing Entra stack). Honest states throughout:
/// <list type="bullet">
/// <item><b>NotConfigured</b> — <c>Notifications:Email</c> is not enabled or the Graph app
/// registration / sender mailbox is absent. Nothing is sent; this is not a failure.</item>
/// <item><b>Delivered</b> — Graph accepted the message (HTTP 202).</item>
/// <item><b>Failed</b> — a configured send genuinely failed (auth/consent/permanent error, or a
/// transient error still failing after the configured retries). Surfaced as the UI retry chip; never
/// a fabricated delivery.</item>
/// </list>
/// Outbox semantics: every send is keyed by the event's idempotency key. A prior <b>Delivered</b>
/// entry short-circuits as an idempotent replay so a retry never sends a duplicate email. Transient
/// failures are retried with exponential backoff up to <c>Notifications:Email:MaxSendAttempts</c>.
/// The Graph client secret and bearer token are server-side only — never returned to the SPA.
/// </summary>
public sealed class EmailNotificationChannel(
    IGraphMailClient graph,
    IMailOutbox outbox,
    IOptions<NotificationOptions> options,
    TimeProvider clock,
    ILogger<EmailNotificationChannel> logger) : INotificationChannel
{
    private readonly EmailChannelOptions _email = options.Value.Email;

    public NotificationChannelKind Kind => NotificationChannelKind.Email;
    public bool IsConfigured => _email.IsConfigured;

    public async Task<NotificationDelivery> SendAsync(
        NotificationEvent evt,
        IReadOnlyList<NotificationRecipient> recipients,
        CancellationToken ct)
    {
        var addresses = recipients
            .Select(r => r.Address ?? r.Name)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .ToArray();
        var names = recipients.Select(r => r.Name).ToArray();

        if (!IsConfigured)
        {
            return Delivery(NotificationDeliveryState.NotConfigured, names,
                "Email not configured (set Notifications:Email:Enabled + FromAddress + Graph app registration).");
        }

        // Idempotent replay: a previously Delivered event never re-sends. This is what makes the dock
        // retry chip / a re-poll safe — no duplicate emails on retry.
        var existing = outbox.Get(evt.IdempotencyKey);
        if (existing is { State: NotificationDeliveryState.Delivered })
        {
            return Delivery(NotificationDeliveryState.Delivered, existing.Recipients,
                "Already delivered (idempotent replay — no duplicate sent).");
        }

        if (addresses.Length == 0)
        {
            return Delivery(NotificationDeliveryState.NotConfigured, names,
                "No email addresses on the recipients for this event.");
        }

        var message = new GraphMailMessage
        {
            Subject = evt.Title,
            Body = evt.Summary,
            ToAddresses = addresses,
        };

        var attempts = Math.Max(1, _email.MaxSendAttempts);
        GraphMailSendOutcome outcome = GraphMailSendOutcome.TransientFailure("No send attempted.");
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            outcome = await graph.SendAsync(message, ct);
            if (outcome.Success || !outcome.Retryable)
                break;

            if (attempt < attempts)
            {
                var delay = TimeSpan.FromMilliseconds(
                    Math.Max(0, _email.RetryBaseDelayMs) * Math.Pow(2, attempt - 1));
                logger.LogWarning(
                    "Graph sendMail attempt {Attempt}/{Max} failed ({Detail}); retrying in {DelayMs}ms.",
                    attempt, attempts, outcome.Detail, delay.TotalMilliseconds);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, clock, ct);
            }
        }

        var state = outcome.Success ? NotificationDeliveryState.Delivered : NotificationDeliveryState.Failed;
        outbox.Save(new MailOutboxEntry
        {
            IdempotencyKey = evt.IdempotencyKey,
            State = state,
            AttemptCount = (existing?.AttemptCount ?? 0) + 1,
            Detail = outcome.Detail,
            Recipients = addresses,
            UpdatedAt = clock.GetUtcNow(),
        });

        return Delivery(state, addresses, outcome.Detail
            ?? (outcome.Success ? "Sent via Microsoft Graph." : "Graph sendMail failed."));
    }

    private NotificationDelivery Delivery(
        NotificationDeliveryState state, IReadOnlyList<string> recipients, string detail) =>
        new()
        {
            Channel = Kind,
            State = state,
            Recipients = recipients,
            Detail = detail,
        };
}
