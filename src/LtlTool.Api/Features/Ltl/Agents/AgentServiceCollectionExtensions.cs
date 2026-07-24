namespace LtlTool.Api.Features.Ltl.Agents;

/// <summary>
/// DI wiring for the read-only background agents (opportunity sweeper, exception sweeper, AR
/// digest, billing-ready sweeper, yard-opportunity sweeper) and their durable heartbeat store.
/// Options bind from <c>Ltl:Agents</c>; every agent is OFF by default. The hosted services are
/// always registered but self-gate on their <c>Ltl:Agents:*:Enabled</c> flag — a disabled agent
/// records a single honest 'off' heartbeat and sweeps nothing, which keeps the heartbeat surface
/// truthful (an off agent shows as 'off', not absent) and the no-op path unit-testable.
///
/// <para>Alvys posture: read-only. Agents reuse existing Alvys reads / decision-support services and never write to Alvys.</para>
/// </summary>
public static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddLtlAgents(
        this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<AgentsOptions>()
            .Bind(configuration.GetSection(AgentsOptions.SectionName));

        // Durable heartbeat store (EF/AppDbContext), scoped to the DbContext lifetime. Internal
        // telemetry only — one row per agent, never Alvys data.
        services.AddScoped<IAgentHeartbeatStore, EfAgentHeartbeatStore>();

        // Five hosted sweepers. Always registered, self-gated on their Enabled flag.
        services.AddHostedService<OpportunitySweeperService>();
        services.AddHostedService<ExceptionSweeperService>();
        services.AddHostedService<ArDigestService>();
        services.AddHostedService<BillingReadySweeperService>();
        services.AddHostedService<YardOpportunitySweeperService>();

        return services;
    }
}
