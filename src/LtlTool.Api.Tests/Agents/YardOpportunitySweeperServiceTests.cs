using LtlTool.Api.Features.Integrations.Yard.Webhooks;
using LtlTool.Api.Features.Ltl.Agents;
using LtlTool.Api.Features.Ltl.Notifications;
using LtlTool.Api.Tests.Yard;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LtlTool.Api.Tests.Agents;

/// <summary>
/// Behaviour tests for the read-only <see cref="YardOpportunitySweeperService"/>, driving one sweep
/// cycle directly (bypassing the PeriodicTimer) against a real DI graph wired with test doubles.
/// Covers the non-negotiables: a disabled agent is a no-op, and a yard-originated opportunity fires
/// exactly one notification even across re-polls. Unlike the Alvys-backed sweepers this agent never
/// touches Alvys, so there is no degraded-probe case to cover.
/// </summary>
public sealed class YardOpportunitySweeperServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Disabled_agent_records_off_heartbeat_and_emits_no_notification()
    {
        var store = new FakeYardWebhookStore();
        store.Opportunities.Add(Opportunity("evt-1", "draft-1"));
        var (service, notifications, heartbeats) = Build(store, new YardOpportunitySweeperOptions { Enabled = false });

        var result = await service.RunSweepCycleAsync(CancellationToken.None);

        Assert.Equal(AgentHeartbeatStatus.Off, result.Status);
        Assert.Equal(AgentHeartbeatStatus.Off, heartbeats.Latest[YardOpportunitySweeperService.Name].Status);
        Assert.Equal(0, notifications.Count);
    }

    [Fact]
    public async Task Yard_draft_fires_exactly_one_notification_across_repolls()
    {
        var store = new FakeYardWebhookStore();
        store.Opportunities.Add(Opportunity("evt-1", "draft-1"));
        var (service, notifications, _) = Build(store, new YardOpportunitySweeperOptions { Enabled = true });

        var first = await service.RunSweepCycleAsync(CancellationToken.None);
        var second = await service.RunSweepCycleAsync(CancellationToken.None);

        Assert.Equal(AgentHeartbeatStatus.Healthy, first.Status);
        Assert.Equal(1, first.WindowSweptCount);
        Assert.Equal(AgentHeartbeatStatus.Healthy, second.Status);
        // Dispatcher idempotency (opportunity id + received-at) collapses the re-poll: exactly one event.
        Assert.Equal(1, notifications.Count);
        var evt = Assert.Single(notifications.Recent(50));
        Assert.Equal(NotificationStage.OpportunityDetected, evt.Stage);
        Assert.Equal("parent-draft-1", evt.LoadId);
    }

    [Fact]
    public async Task Two_distinct_drafts_fire_two_notifications()
    {
        var store = new FakeYardWebhookStore();
        store.Opportunities.Add(Opportunity("evt-1", "draft-1"));
        store.Opportunities.Add(Opportunity("evt-2", "draft-2"));
        var (service, notifications, _) = Build(store, new YardOpportunitySweeperOptions { Enabled = true });

        var result = await service.RunSweepCycleAsync(CancellationToken.None);

        Assert.Equal(AgentHeartbeatStatus.Healthy, result.Status);
        Assert.Equal(2, result.WindowSweptCount);
        Assert.Equal(2, notifications.Count);
    }

    private static (YardOpportunitySweeperService Service, INotificationStore Store, RecordingAgentHeartbeatStore Heartbeats)
        Build(FakeYardWebhookStore yardStore, YardOpportunitySweeperOptions yardOptions)
    {
        var notifications = new InMemoryNotificationStore();
        var heartbeats = new RecordingAgentHeartbeatStore();
        var clock = new FixedClock(Now);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IYardWebhookStore>(yardStore);
        services.AddSingleton<INotificationStore>(notifications);
        services.AddSingleton<IAgentHeartbeatStore>(heartbeats);
        services.AddSingleton<INotificationChannel, InAppNotificationChannel>();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new NotificationOptions()));
        services.AddScoped<NotificationDispatcher>();

        var provider = services.BuildServiceProvider();

        var agentOptions = Microsoft.Extensions.Options.Options.Create(
            new AgentsOptions { YardOpportunitySweeper = yardOptions });
        var service = new YardOpportunitySweeperService(
            provider,
            clock,
            agentOptions,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger<YardOpportunitySweeperService>());

        return (service, notifications, heartbeats);
    }

    private static YardLtlOpportunity Opportunity(string eventId, string draftId) => new()
    {
        Id = $"opp-{draftId}",
        EventId = eventId,
        DraftId = draftId,
        YardCode = "LAREDO",
        ParentLoadId = $"parent-{draftId}",
        SiblingLoadIdsJson = "[\"id-sib-1\",\"id-sib-2\"]",
        FreightJson = "[]",
        CreatedByStation = "dock-1",
        ScannedAt = Now.AddMinutes(-10),
        ReceivedAt = Now.AddMinutes(-5),
    };
}
