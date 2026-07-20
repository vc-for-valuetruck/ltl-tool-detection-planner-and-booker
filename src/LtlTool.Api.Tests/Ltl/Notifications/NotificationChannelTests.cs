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

    [Fact]
    public async Task Email_channel_reports_not_configured_when_disabled()
    {
        var channel = new EmailNotificationChannel(Microsoft.Extensions.Options.Options.Create(new NotificationOptions()));

        Assert.False(channel.IsConfigured);
        var delivery = await channel.SendAsync(Event(), Recipients(), CancellationToken.None);
        Assert.Equal(NotificationDeliveryState.NotConfigured, delivery.State);
    }

    [Fact]
    public async Task Email_channel_reports_pending_when_configured_but_transport_unwired()
    {
        var options = new NotificationOptions
        {
            Email = new EmailChannelOptions
            {
                Enabled = true,
                SmtpHost = "smtp.example.com",
                FromAddress = "ops@example.com",
            },
        };
        var channel = new EmailNotificationChannel(Microsoft.Extensions.Options.Options.Create(options));

        Assert.True(channel.IsConfigured);
        var delivery = await channel.SendAsync(Event(), Recipients(), CancellationToken.None);
        Assert.Equal(NotificationDeliveryState.Pending, delivery.State);
    }
}
