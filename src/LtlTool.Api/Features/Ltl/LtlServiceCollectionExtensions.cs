using LtlTool.Api.Features.Ltl.Assignment;
using LtlTool.Api.Features.Ltl.SavedViews;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// DI wiring for the LTL decision-support layer: options binding, the normalization/billing/
/// match/search services and the internal assignment-audit store. Sits on top of the read-only
/// Alvys integration (registered separately) and adds no Alvys writeback.
/// </summary>
public static class LtlServiceCollectionExtensions
{
    public static IServiceCollection AddLtlDecisionSupport(
        this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<LtlOptions>()
            .Bind(configuration.GetSection(LtlOptions.SectionName));

        // Deterministic clock so scoring/billing are testable; the default system clock in prod.
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<BillingReadinessService>();
        services.AddScoped<WorkflowStageService>();
        services.AddScoped<VisibilityAnalyzer>();
        services.AddScoped<EquipmentEventAnalyzer>();
        services.AddScoped<LtlNormalizationService>();
        services.AddScoped<MatchScoringService>();
        services.AddScoped<MatchService>();
        services.AddScoped<LtlLoadService>();
        services.AddScoped<AssignmentValidationService>();

        // Internal, non-Alvys assignment audit. Singleton in-memory store for this slice;
        // swap for a persistent IAssignmentAuditStore in production.
        services.AddSingleton<IAssignmentAuditStore, InMemoryAssignmentAuditStore>();

        // Tool-local dispatcher saved views. Singleton in-memory store for this slice (server-side,
        // not browser storage); swap for a persistent ISavedViewStore in production. No Alvys writeback.
        services.AddSingleton<ISavedViewStore, InMemorySavedViewStore>();

        return services;
    }
}
