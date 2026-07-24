using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.Agents;
using LtlTool.Api.Features.Ltl.Notifications;
using LtlTool.Api.Tests.Ltl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LtlTool.Api.Tests.Agents;

/// <summary>
/// Behaviour tests for the read-only <see cref="BillingReadySweeperService"/>, driving one sweep
/// cycle directly (bypassing the PeriodicTimer) against a real DI graph wired with test doubles.
/// Covers the three non-negotiables: a disabled agent is a no-op, an unavailable Alvys degrades
/// honestly with no notification, and a load that clears every billing gate fires exactly one
/// notification even across re-polls — while a load still missing data fires none.
/// </summary>
public sealed class BillingReadySweeperServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Disabled_agent_records_off_heartbeat_and_emits_no_notification()
    {
        var alvys = new ScriptedAlvysClient { Loads = { ReadyLoad("L-A") } };
        var (service, store, heartbeats) = Build(alvys, new BillingReadySweeperOptions { Enabled = false });

        var result = await service.RunSweepCycleAsync(CancellationToken.None);

        Assert.Equal(AgentHeartbeatStatus.Off, result.Status);
        Assert.Equal(AgentHeartbeatStatus.Off, heartbeats.Latest[BillingReadySweeperService.Name].Status);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Alvys_null_response_degrades_and_emits_no_notification()
    {
        var alvys = new ScriptedAlvysClient { ReturnNull = true };
        var (service, store, heartbeats) = Build(alvys, new BillingReadySweeperOptions { Enabled = true });

        var result = await service.RunSweepCycleAsync(CancellationToken.None);

        Assert.Equal(AgentHeartbeatStatus.Degraded, result.Status);
        var hb = heartbeats.Latest[BillingReadySweeperService.Name];
        Assert.Equal(AgentHeartbeatStatus.Degraded, hb.Status);
        Assert.Equal("AlvysNullResponse", hb.LastErrorType);
        Assert.Null(hb.WindowSweptCount);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Ready_to_bill_load_fires_exactly_one_notification_across_repolls()
    {
        var alvys = new ScriptedAlvysClient { Loads = { ReadyLoad("L-A") } };
        var (service, store, _) = Build(alvys, new BillingReadySweeperOptions { Enabled = true });

        var first = await service.RunSweepCycleAsync(CancellationToken.None);
        var second = await service.RunSweepCycleAsync(CancellationToken.None);

        Assert.Equal(AgentHeartbeatStatus.Healthy, first.Status);
        Assert.Equal(AgentHeartbeatStatus.Healthy, second.Status);
        // Dispatcher idempotency (load number + fixed sentinel) collapses the re-poll: exactly one event.
        Assert.Equal(1, store.Count);
        var evt = Assert.Single(store.Recent(50));
        Assert.Equal(NotificationStage.BillingReady, evt.Stage);
        Assert.Equal("L-A", evt.LoadNumber);
    }

    [Fact]
    public async Task Load_missing_billing_data_emits_no_notification()
    {
        // Delivered but no rate and no weight — not ready to bill, so no T6 trigger should fire.
        var notReady = ReadyLoad("L-B");
        notReady.CustomerRate = null;
        notReady.Linehaul = null;
        notReady.Weight = null;

        var alvys = new ScriptedAlvysClient { Loads = { notReady } };
        var (service, store, _) = Build(alvys, new BillingReadySweeperOptions { Enabled = true });

        var result = await service.RunSweepCycleAsync(CancellationToken.None);

        Assert.Equal(AgentHeartbeatStatus.Healthy, result.Status);
        Assert.Equal(0, store.Count);
    }

    private static (BillingReadySweeperService Service, INotificationStore Store, RecordingAgentHeartbeatStore Heartbeats)
        Build(ScriptedAlvysClient alvys, BillingReadySweeperOptions billingReadyOptions)
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
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new NotificationOptions()));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new LtlOptions()));
        services.AddScoped(_ => LtlTestFactory.Normalizer());
        services.AddScoped(_ => LtlTestFactory.Visibility());
        services.AddScoped(_ => LtlTestFactory.AccessorialAnalyzer());
        services.AddScoped<IAccessorialSignalExtractor>(_ => new NullAccessorialSignalExtractor());
        services.AddScoped<LtlLoadService>();
        services.AddScoped<NotificationDispatcher>();

        var provider = services.BuildServiceProvider();

        var agentOptions = Microsoft.Extensions.Options.Options.Create(
            new AgentsOptions { BillingReadySweeper = billingReadyOptions });
        var service = new BillingReadySweeperService(
            provider,
            clock,
            agentOptions,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger<BillingReadySweeperService>());

        return (service, store, heartbeats);
    }

    private static AlvysLoad ReadyLoad(string loadNumber) => new()
    {
        Id = $"id-{loadNumber}",
        LoadNumber = loadNumber,
        CustomerId = "CUST1",
        CustomerName = "Acme Co",
        Status = "Delivered",
        CustomerRate = 1500m,
        Weight = 20000m,
        ActualDeliveryAt = Now.AddDays(-1),
        ScheduledPickupAt = Now.AddDays(-2),
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
