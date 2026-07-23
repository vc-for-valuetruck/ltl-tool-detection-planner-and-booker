using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl.Agents;
using LtlTool.Api.Features.Ltl.Consolidation;
using LtlTool.Api.Features.Ltl.Notifications;
using LtlTool.Api.Features.Ltl.Optimization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Agents;

/// <summary>
/// Behaviour tests for the read-only <see cref="OpportunitySweeperService"/>, driving one sweep cycle
/// directly (bypassing the PeriodicTimer) against a real DI graph wired with test doubles. Covers the
/// three non-negotiables: a disabled agent is a no-op, an unavailable Alvys degrades honestly with no
/// notification, and a newly-crossed uplift threshold fires exactly one notification even across
/// re-polls.
/// </summary>
public sealed class OpportunitySweeperServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Disabled_agent_records_off_heartbeat_and_emits_no_notification()
    {
        var alvys = new ScriptedAlvysClient { Loads = { EligibleLoad("L-A", "CUST1", 2000m) } };
        var (service, store, heartbeats) = Build(alvys, new OpportunitySweeperOptions { Enabled = false });

        var result = await service.RunSweepCycleAsync(CancellationToken.None);

        Assert.Equal(AgentHeartbeatStatus.Off, result.Status);
        Assert.Equal(AgentHeartbeatStatus.Off, heartbeats.Latest[OpportunitySweeperService.Name].Status);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Alvys_null_response_degrades_and_emits_no_notification()
    {
        var alvys = new ScriptedAlvysClient { ReturnNull = true };
        var (service, store, heartbeats) = Build(alvys, new OpportunitySweeperOptions { Enabled = true });

        var result = await service.RunSweepCycleAsync(CancellationToken.None);

        Assert.Equal(AgentHeartbeatStatus.Degraded, result.Status);
        var hb = heartbeats.Latest[OpportunitySweeperService.Name];
        Assert.Equal(AgentHeartbeatStatus.Degraded, hb.Status);
        Assert.Equal("AlvysNullResponse", hb.LastErrorType);
        Assert.Null(hb.WindowSweptCount);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Newly_crossed_threshold_fires_exactly_one_notification_across_repolls()
    {
        // Two same-customer / same-state / same-day delivered loads. Parent linehaul 2000, one sibling
        // at 800 → projected uplift 800 ≥ the 500 default threshold.
        var alvys = new ScriptedAlvysClient
        {
            Loads =
            {
                EligibleLoad("L-A", "CUST1", 2000m),
                EligibleLoad("L-B", "CUST1", 800m),
            },
        };
        var (service, store, heartbeats) = Build(
            alvys, new OpportunitySweeperOptions { Enabled = true, UpliftAlertThresholdUsd = 500m });

        var first = await service.RunSweepCycleAsync(CancellationToken.None);
        var second = await service.RunSweepCycleAsync(CancellationToken.None);

        Assert.Equal(AgentHeartbeatStatus.Healthy, first.Status);
        Assert.Equal(AgentHeartbeatStatus.Healthy, second.Status);
        // Dispatcher idempotency (parent load + pickup date) collapses the re-poll: exactly one event.
        Assert.Equal(1, store.Count);
        var evt = Assert.Single(store.Recent(50));
        Assert.Equal(NotificationStage.OpportunityDetected, evt.Stage);
        Assert.Equal("L-A", evt.LoadNumber);
    }

    private static (OpportunitySweeperService Service, INotificationStore Store, RecordingAgentHeartbeatStore Heartbeats)
        Build(ScriptedAlvysClient alvys, OpportunitySweeperOptions opportunityOptions)
    {
        var store = new InMemoryNotificationStore();
        var heartbeats = new RecordingAgentHeartbeatStore();
        var clock = new FixedClock(Now);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IAlvysClient>(alvys);
        services.AddSingleton<INotificationStore>(store);
        services.AddSingleton<IAgentHeartbeatStore>(heartbeats);
        services.AddSingleton<INotificationChannel, InAppNotificationChannel>();
        services.AddSingleton<ICapacityCostSolver, NullCapacityCostSolver>();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new CapacityCostSolverOptions()));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new NotificationOptions()));
        services.AddScoped<ConsolidationOpportunityService>();
        services.AddScoped<NotificationDispatcher>();

        var provider = services.BuildServiceProvider();

        var agentOptions = Microsoft.Extensions.Options.Options.Create(new AgentsOptions { OpportunitySweeper = opportunityOptions });
        var service = new OpportunitySweeperService(
            provider,
            clock,
            agentOptions,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger<OpportunitySweeperService>());

        return (service, store, heartbeats);
    }

    private static AlvysLoad EligibleLoad(string loadNumber, string customerId, decimal linehaul) => new()
    {
        Id = $"id-{loadNumber}",
        LoadNumber = loadNumber,
        CustomerId = customerId,
        CustomerName = "Acme Co",
        Status = "Delivered",
        Linehaul = linehaul,
        CustomerMileage = 1000m,
        Weight = 20000m,
        ScheduledPickupAt = Now.AddDays(-1),
        Stops =
        [
            new AlvysLoadStop
            {
                StopType = "Pickup",
                Sequence = 1,
                Address = new AlvysAddress { City = "Dallas", State = "TX" },
            },
            new AlvysLoadStop
            {
                StopType = "Delivery",
                Sequence = 2,
                Address = new AlvysAddress { City = "Los Angeles", State = "CA" },
            },
        ],
    };
}
