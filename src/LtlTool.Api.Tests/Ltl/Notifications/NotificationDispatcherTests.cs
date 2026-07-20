using LtlTool.Api.Features.Ltl.Notifications;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Notifications;

/// <summary>
/// Behavior tests for the notification dispatcher: idempotency across re-polls, fan-out to the
/// always-on in-app channel plus explicitly-targeted external channels, and honest delivery
/// state (never a fabricated "Delivered" for an unconfigured channel).
/// </summary>
public sealed class NotificationDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private static NotificationTrigger Trigger(
        string sourceKey = "L-100",
        NotificationStage stage = NotificationStage.ConsolidationPlanCreated,
        DateTimeOffset? occurredAt = null) => new()
    {
        Stage = stage,
        SourceKey = sourceKey,
        Title = "Consolidation plan recorded",
        Summary = "A plan was recorded.",
        LoadId = "L-100",
        LoadNumber = "100",
        OccurredAt = occurredAt ?? Now,
    };

    private static NotificationDispatcher Build(
        out INotificationStore store,
        IEnumerable<INotificationChannel>? channels = null,
        NotificationOptions? options = null)
    {
        store = new InMemoryNotificationStore();
        var chans = channels?.ToArray() ?? [new InAppNotificationChannel()];
        return new NotificationDispatcher(
            chans,
            store,
            Options.Create(options ?? new NotificationOptions()),
            new FixedTimeProvider(Now));
    }

    [Fact]
    public async Task Dispatch_fires_once_and_dedupes_repeat()
    {
        var dispatcher = Build(out var store);

        var first = await dispatcher.DispatchAsync(Trigger(), CancellationToken.None);
        var second = await dispatcher.DispatchAsync(Trigger(), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second); // same stage+source+occurredAt → duplicate
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Dispatch_distinct_source_keys_fire_separately()
    {
        var dispatcher = Build(out var store);

        await dispatcher.DispatchAsync(Trigger(sourceKey: "A"), CancellationToken.None);
        await dispatcher.DispatchAsync(Trigger(sourceKey: "B"), CancellationToken.None);

        Assert.Equal(2, store.Count);
    }

    [Fact]
    public async Task Dispatch_always_delivers_in_app_with_default_recipients()
    {
        var dispatcher = Build(out _);

        var evt = await dispatcher.DispatchAsync(Trigger(), CancellationToken.None);

        Assert.NotNull(evt);
        var inApp = Assert.Single(evt!.Deliveries, d => d.Channel == NotificationChannelKind.InApp);
        Assert.Equal(NotificationDeliveryState.Delivered, inApp.State);
        // Default ConsolidationPlanCreated audience is a non-empty in-app role list.
        Assert.NotEmpty(inApp.Recipients);
    }

    [Fact]
    public async Task Dispatch_fans_out_to_configured_recipients_across_channels()
    {
        var options = new NotificationOptions
        {
            Recipients =
            {
                [NotificationStage.ConsolidationPlanCreated.ToString()] =
                [
                    new RecipientConfig { Name = "dispatch", Channel = NotificationChannelKind.InApp },
                    new RecipientConfig { Name = "ops team", Channel = NotificationChannelKind.Teams },
                    new RecipientConfig { Name = "billing", Channel = NotificationChannelKind.Email, Address = "b@x.com" },
                ],
            },
        };
        var teams = new RecordingChannel(NotificationChannelKind.Teams);
        var email = new RecordingChannel(NotificationChannelKind.Email);
        var dispatcher = Build(
            out var store,
            channels: [new InAppNotificationChannel(), teams, email],
            options: options);

        var evt = await dispatcher.DispatchAsync(Trigger(), CancellationToken.None);

        Assert.NotNull(evt);
        // In-app sees the whole group (self-serve feed); external channels only their targeted names.
        var inApp = evt!.Deliveries.Single(d => d.Channel == NotificationChannelKind.InApp);
        Assert.Equal(3, inApp.Recipients.Count);
        Assert.Equal(["ops team"], teams.LastRecipients);
        Assert.Equal(["billing"], email.LastRecipients);
    }

    [Fact]
    public async Task Dispatch_skips_external_channel_with_no_targeted_recipients()
    {
        // Default recipients are all in-app, so a Teams channel must not be invoked.
        var teams = new RecordingChannel(NotificationChannelKind.Teams);
        var dispatcher = Build(
            out _,
            channels: [new InAppNotificationChannel(), teams]);

        var evt = await dispatcher.DispatchAsync(Trigger(), CancellationToken.None);

        Assert.NotNull(evt);
        Assert.False(teams.WasCalled);
        Assert.DoesNotContain(evt!.Deliveries, d => d.Channel == NotificationChannelKind.Teams);
    }

    private sealed class RecordingChannel(NotificationChannelKind kind) : INotificationChannel
    {
        public NotificationChannelKind Kind => kind;
        public bool IsConfigured => true;
        public bool WasCalled { get; private set; }
        public IReadOnlyList<string> LastRecipients { get; private set; } = [];

        public Task<NotificationDelivery> SendAsync(
            NotificationEvent evt,
            IReadOnlyList<NotificationRecipient> recipients,
            CancellationToken ct)
        {
            WasCalled = true;
            LastRecipients = recipients.Select(r => r.Name).ToArray();
            return Task.FromResult(new NotificationDelivery
            {
                Channel = kind,
                State = NotificationDeliveryState.Delivered,
                Recipients = LastRecipients,
            });
        }
    }
}
