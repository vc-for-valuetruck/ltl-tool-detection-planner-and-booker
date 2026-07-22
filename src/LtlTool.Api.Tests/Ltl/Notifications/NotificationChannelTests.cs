using LtlTool.Api.Features.Ltl.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Notifications;

/// <summary>
/// Honesty tests for the notification channels: an unconfigured channel reports NotConfigured
/// (never a fabricated Delivered), and a configured-but-unwired email channel reports Pending.
/// </summary>
public sealed class NotificationChannelTests
{
    private static NotificationEvent Event() => new()
    {
        Id = "e1",
        IdempotencyKey = "k1",
        Stage = NotificationStage.ConsolidationPlanCreated,
        Title = "t",
        Summary = "s",
        OccurredAt = DateTimeOffset.UtcNow,
        FiredAt = DateTimeOffset.UtcNow,
        Deliveries = [],
    };

    private static IReadOnlyList<NotificationRecipient> Recipients() =>
        [new NotificationRecipient { Name = "dispatcher" }];

    [Fact]
    public void InApp_channel_is_always_configured()
    {
        Assert.True(new InAppNotificationChannel().IsConfigured);
    }

    [Fact]
    public async Task InApp_channel_reports_delivered()
    {
        var delivery = await new InAppNotificationChannel()
            .SendAsync(Event(), Recipients(), CancellationToken.None);

        Assert.Equal(NotificationDeliveryState.Delivered, delivery.State);
        Assert.Equal(["dispatcher"], delivery.Recipients);
    }

    [Fact]
    public async Task Teams_channel_reports_not_configured_without_webhook()
    {
        var channel = new TeamsNotificationChannel(
            new HttpClient(),
            Microsoft.Extensions.Options.Options.Create(new NotificationOptions()),
            NullLogger<TeamsNotificationChannel>.Instance);

        Assert.False(channel.IsConfigured);
        var delivery = await channel.SendAsync(Event(), Recipients(), CancellationToken.None);
        Assert.Equal(NotificationDeliveryState.NotConfigured, delivery.State);
    }

    private static IReadOnlyList<NotificationRecipient> EmailRecipients() =>
    [
        new NotificationRecipient
        {
            Name = "dispatch@valuetruck.com",
            Channel = NotificationChannelKind.Email,
            Address = "dispatch@valuetruck.com",
        },
    ];

    [Fact]
    public async Task Email_channel_reports_not_configured_when_disabled()
    {
        var channel = NotificationTestFactory.UnconfiguredEmailChannel();

        Assert.False(channel.IsConfigured);
        var delivery = await channel.SendAsync(Event(), EmailRecipients(), CancellationToken.None);
        Assert.Equal(NotificationDeliveryState.NotConfigured, delivery.State);
    }

    [Fact]
    public void Email_channel_is_configured_only_with_sender_and_graph_app()
    {
        Assert.False(new EmailChannelOptions { Enabled = true, FromAddress = "ops@x.com" }.IsConfigured);
        Assert.False(new EmailChannelOptions
        {
            Enabled = true,
            Graph = new GraphMailOptions { TenantId = "t", ClientId = "c", ClientSecret = "s" },
        }.IsConfigured);
        Assert.True(NotificationTestFactory.ConfiguredEmail().IsConfigured);
    }

    [Fact]
    public async Task Email_channel_reports_delivered_on_graph_success()
    {
        var graph = new FakeGraphMailClient(GraphMailSendOutcome.Sent());
        var channel = NotificationTestFactory.EmailChannel(graph, new InMemoryMailOutbox());

        var delivery = await channel.SendAsync(Event(), EmailRecipients(), CancellationToken.None);

        Assert.Equal(NotificationDeliveryState.Delivered, delivery.State);
        Assert.Equal(1, graph.SendCount);
    }

    [Fact]
    public async Task Email_channel_reports_failed_on_permanent_graph_error_without_retrying()
    {
        var graph = new FakeGraphMailClient(GraphMailSendOutcome.PermanentFailure("HTTP 403"));
        var channel = NotificationTestFactory.EmailChannel(graph, new InMemoryMailOutbox());

        var delivery = await channel.SendAsync(Event(), EmailRecipients(), CancellationToken.None);

        Assert.Equal(NotificationDeliveryState.Failed, delivery.State);
        Assert.Equal(1, graph.SendCount); // permanent → no retry
    }

    [Fact]
    public async Task Email_channel_retries_transient_then_succeeds()
    {
        var graph = new FakeGraphMailClient(
            GraphMailSendOutcome.Sent(),
            GraphMailSendOutcome.TransientFailure("HTTP 503"),
            GraphMailSendOutcome.Sent());
        var channel = NotificationTestFactory.EmailChannel(graph, new InMemoryMailOutbox());

        var delivery = await channel.SendAsync(Event(), EmailRecipients(), CancellationToken.None);

        Assert.Equal(NotificationDeliveryState.Delivered, delivery.State);
        Assert.Equal(2, graph.SendCount); // one transient failure, then success
    }

    [Fact]
    public async Task Email_channel_fails_after_exhausting_transient_retries()
    {
        var email = NotificationTestFactory.ConfiguredEmail();
        email.MaxSendAttempts = 3;
        var graph = new FakeGraphMailClient(GraphMailSendOutcome.TransientFailure("HTTP 503"));
        var channel = NotificationTestFactory.EmailChannel(graph, new InMemoryMailOutbox(), email);

        var delivery = await channel.SendAsync(Event(), EmailRecipients(), CancellationToken.None);

        Assert.Equal(NotificationDeliveryState.Failed, delivery.State);
        Assert.Equal(3, graph.SendCount);
    }

    [Fact]
    public async Task Email_channel_does_not_resend_an_already_delivered_event()
    {
        var graph = new FakeGraphMailClient(GraphMailSendOutcome.Sent());
        var outbox = new InMemoryMailOutbox();
        var channel = NotificationTestFactory.EmailChannel(graph, outbox);

        var first = await channel.SendAsync(Event(), EmailRecipients(), CancellationToken.None);
        var replay = await channel.SendAsync(Event(), EmailRecipients(), CancellationToken.None);

        Assert.Equal(NotificationDeliveryState.Delivered, first.State);
        Assert.Equal(NotificationDeliveryState.Delivered, replay.State);
        Assert.Equal(1, graph.SendCount); // idempotent replay — no duplicate email
    }

    [Fact]
    public async Task Email_channel_records_last_send_in_the_outbox()
    {
        var graph = new FakeGraphMailClient(GraphMailSendOutcome.PermanentFailure("HTTP 400"));
        var outbox = new InMemoryMailOutbox();
        var channel = NotificationTestFactory.EmailChannel(graph, outbox);

        await channel.SendAsync(Event(), EmailRecipients(), CancellationToken.None);

        var recent = outbox.MostRecent();
        Assert.NotNull(recent);
        Assert.Equal(NotificationDeliveryState.Failed, recent!.State);
        Assert.Equal("k1", recent.IdempotencyKey);
    }
}
