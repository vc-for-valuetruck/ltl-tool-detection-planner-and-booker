using LtlTool.Api.Features.Ltl.DispatchAssist;
using LtlTool.Api.Features.Ltl.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the Dispatch Assist notify step's two safety controls: the master <c>Ltl:Comms:Enabled</c>
/// flag (default OFF → honest <c>NotEnabled</c>, never a fabricated send) and the
/// <c>Ltl:Comms:OverrideRecipient</c> reroute (default <c>joshua.davis@valuetruck.com</c> → all mail to
/// one safe address while still reporting the intended driver/dispatcher for the UI banner).
/// </summary>
public sealed class DispatchAssemblyNotifierTests
{
    private const string DefaultOverride = "joshua.davis@valuetruck.com";

    /// <summary>Records the last message it was asked to send and returns a scripted outcome.</summary>
    private sealed class RecordingGraphMailClient(bool success = true) : IGraphMailClient
    {
        public GraphMailMessage? LastMessage { get; private set; }
        public int SendCount { get; private set; }

        public Task<GraphMailSendOutcome> SendAsync(GraphMailMessage message, CancellationToken ct)
        {
            LastMessage = message;
            SendCount++;
            return Task.FromResult(success
                ? GraphMailSendOutcome.Sent("ok")
                : GraphMailSendOutcome.PermanentFailure("boom"));
        }
    }

    private static NotificationOptions ConfiguredEmail() => new()
    {
        Email = new EmailChannelOptions
        {
            Enabled = true,
            FromAddress = "dispatch@valuetruck.com",
            Graph = new GraphMailOptions { TenantId = "t", ClientId = "c", ClientSecret = "s" },
        },
    };

    private static NotificationOptions UnconfiguredEmail() => new();

    private static DispatchAssemblyNotifier Notifier(
        IGraphMailClient graph,
        bool commsEnabled,
        string? overrideRecipient = DefaultOverride,
        NotificationOptions? notifications = null) =>
        new(
            graph,
            Options.Create(new DispatchCommsOptions
            {
                Enabled = commsEnabled,
                OverrideRecipient = overrideRecipient,
            }),
            Options.Create(notifications ?? ConfiguredEmail()),
            NullLogger<DispatchAssemblyNotifier>.Instance);

    private static IReadOnlyList<DispatchNotifyRecipient> Recipients() =>
    [
        new DispatchNotifyRecipient { Role = "driver", Name = "Pat", Address = "pat@fleet.test" },
        new DispatchNotifyRecipient { Role = "dispatcher", Name = "Dana", Address = "dana@valuetruck.com" },
    ];

    [Fact]
    public void ResolveEffectiveRecipients_defaults_to_the_safe_override_address()
    {
        var notifier = Notifier(new RecordingGraphMailClient(), commsEnabled: true);

        var (effective, overrideActive, overrideTo) = notifier.ResolveEffectiveRecipients(Recipients());

        Assert.True(overrideActive);
        Assert.Equal(DefaultOverride, overrideTo);
        Assert.Equal(new[] { DefaultOverride }, effective);
    }

    [Fact]
    public void ResolveEffectiveRecipients_uses_intended_addresses_when_override_is_cleared()
    {
        var notifier = Notifier(new RecordingGraphMailClient(), commsEnabled: true, overrideRecipient: "");

        var (effective, overrideActive, overrideTo) = notifier.ResolveEffectiveRecipients(Recipients());

        Assert.False(overrideActive);
        Assert.Null(overrideTo);
        Assert.Equal(new[] { "pat@fleet.test", "dana@valuetruck.com" }, effective);
    }

    [Fact]
    public void ResolveEffectiveRecipients_dedupes_intended_addresses_case_insensitively()
    {
        var notifier = Notifier(new RecordingGraphMailClient(), commsEnabled: true, overrideRecipient: "  ");
        IReadOnlyList<DispatchNotifyRecipient> dupes =
        [
            new DispatchNotifyRecipient { Role = "driver", Address = "same@fleet.test" },
            new DispatchNotifyRecipient { Role = "dispatcher", Address = "SAME@fleet.test" },
            new DispatchNotifyRecipient { Role = "cc", Address = null },
        ];

        var (effective, overrideActive, _) = notifier.ResolveEffectiveRecipients(dupes);

        Assert.False(overrideActive);
        Assert.Single(effective);
        Assert.Equal("same@fleet.test", effective[0]);
    }

    [Fact]
    public async Task Disabled_comms_report_NotEnabled_and_send_nothing()
    {
        var graph = new RecordingGraphMailClient();
        var notifier = Notifier(graph, commsEnabled: false);

        var result = await notifier.NotifyAsync(Recipients(), "subj", "body", CancellationToken.None);

        Assert.False(result.Sent);
        Assert.Equal("NotEnabled", result.State);
        Assert.Equal(0, graph.SendCount);
        // Even disabled, the intended recipients + override banner state are reported honestly.
        Assert.True(result.OverrideActive);
        Assert.Equal(DefaultOverride, result.OverrideRecipient);
        Assert.Equal(2, result.IntendedRecipients.Count);
        Assert.Empty(result.EffectiveRecipients);
    }

    [Fact]
    public async Task Enabled_comms_send_to_the_override_address_only()
    {
        var graph = new RecordingGraphMailClient();
        var notifier = Notifier(graph, commsEnabled: true);

        var result = await notifier.NotifyAsync(Recipients(), "subj", "body", CancellationToken.None);

        Assert.True(result.Sent);
        Assert.Equal("Sent", result.State);
        Assert.Equal(1, graph.SendCount);
        Assert.NotNull(graph.LastMessage);
        Assert.Equal(new[] { DefaultOverride }, graph.LastMessage!.ToAddresses);
        // The real driver/dispatcher were never addressed, but are still reported as intended.
        Assert.True(result.OverrideActive);
        Assert.Contains(result.IntendedRecipients, r => r.Address == "pat@fleet.test");
    }

    [Fact]
    public async Task Enabled_comms_with_override_cleared_send_to_real_recipients()
    {
        var graph = new RecordingGraphMailClient();
        var notifier = Notifier(graph, commsEnabled: true, overrideRecipient: "");

        var result = await notifier.NotifyAsync(Recipients(), "subj", "body", CancellationToken.None);

        Assert.True(result.Sent);
        Assert.False(result.OverrideActive);
        Assert.Equal(new[] { "pat@fleet.test", "dana@valuetruck.com" }, graph.LastMessage!.ToAddresses);
    }

    [Fact]
    public async Task No_resolvable_recipients_and_no_override_report_NoRecipients()
    {
        var graph = new RecordingGraphMailClient();
        var notifier = Notifier(graph, commsEnabled: true, overrideRecipient: "");
        IReadOnlyList<DispatchNotifyRecipient> noAddresses =
        [
            new DispatchNotifyRecipient { Role = "driver", Name = "Pat", Address = null },
        ];

        var result = await notifier.NotifyAsync(noAddresses, "subj", "body", CancellationToken.None);

        Assert.False(result.Sent);
        Assert.Equal("NoRecipients", result.State);
        Assert.Equal(0, graph.SendCount);
    }

    [Fact]
    public async Task Enabled_but_unconfigured_transport_reports_Failed_without_sending()
    {
        var graph = new RecordingGraphMailClient();
        var notifier = Notifier(graph, commsEnabled: true, notifications: UnconfiguredEmail());

        var result = await notifier.NotifyAsync(Recipients(), "subj", "body", CancellationToken.None);

        Assert.False(result.Sent);
        Assert.Equal("Failed", result.State);
        Assert.Equal(0, graph.SendCount);
        Assert.Equal(new[] { DefaultOverride }, result.EffectiveRecipients);
    }

    [Fact]
    public async Task Transport_failure_is_reported_Failed_not_a_false_success()
    {
        var graph = new RecordingGraphMailClient(success: false);
        var notifier = Notifier(graph, commsEnabled: true);

        var result = await notifier.NotifyAsync(Recipients(), "subj", "body", CancellationToken.None);

        Assert.False(result.Sent);
        Assert.Equal("Failed", result.State);
        Assert.Equal(1, graph.SendCount);
    }
}
