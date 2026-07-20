using LtlTool.Api.Features.Ltl.Agent.Handlers;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LtlTool.Api.Features.Ltl.Agent;

/// <summary>
/// DI wiring for the Phase 2 M4 agent command surface. Follows the same feature-flag + Null-fallback
/// posture as the optimization engines: the tool-style catalog is always available for schema
/// discovery, but POST dispatch is only enabled when <c>Ltl:Optimization:AgentCommands:Enabled</c> is
/// true. Every command is read-only against Alvys; nothing here writes back.
/// </summary>
public static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddLtlAgentCommands(
        this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<QuoteEstimatorOptions>()
            .Bind(configuration.GetSection(QuoteEstimatorOptions.SectionName));

        services
            .AddOptions<IncidentRiskOptions>()
            .Bind(configuration.GetSection(IncidentRiskOptions.SectionName));

        // Stateless reference calculator (rate card is options-bound) → singleton.
        services.TryAddSingleton<QuoteEstimatorService>();

        // Stateful in-memory planning signals (never touch Alvys) → singletons, mirroring the
        // consolidation audit store's posture.
        services.TryAddSingleton<IncidentStore>();
        services.TryAddSingleton<IAgentCommandAuditStore, InMemoryAgentCommandAuditStore>();

        var enabled = configuration
            .GetSection($"{LtlOptions.SectionName}:{nameof(LtlOptions.Optimization)}:AgentCommands")
            .GetValue<bool>("Enabled");

        if (enabled)
        {
            // Handlers depend on scoped decision-support services (plan/candidate/opportunity
            // services, the trailer-fit engine, the stop sequencer), so they and the dispatcher are
            // scoped too. Registered as IAgentCommandHandler so the dispatcher discovers them all.
            services.AddScoped<IAgentCommandHandler, ListOpportunitiesHandler>();
            services.AddScoped<IAgentCommandHandler, ExplainPlanHandler>();
            services.AddScoped<IAgentCommandHandler, CheckFitHandler>();
            services.AddScoped<IAgentCommandHandler, SequenceStopsHandler>();
            services.AddScoped<IAgentCommandHandler, EstimateQuoteHandler>();
            services.AddScoped<IAgentCommandHandler, ReportIncidentHandler>();

            services.AddScoped<IAgentCommandDispatcher, AgentCommandDispatcher>();
        }
        else
        {
            // Flag off: no handlers, and the null dispatcher refuses to dispatch while still serving
            // the catalog for discovery. The controller returns 404 for POST when IsEnabled is false.
            services.AddSingleton<IAgentCommandDispatcher, NullAgentCommandDispatcher>();
        }

        return services;
    }
}
