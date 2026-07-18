using LtlTool.Api.Features.Ltl.Assignment;
using LtlTool.Api.Features.Ltl.Consolidation;
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

        services
            .AddOptions<ConsolidationOptions>()
            .Bind(configuration.GetSection(ConsolidationOptions.SectionName));

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

        // Consolidation planner (Phase 1 pilot: Laredo → Dallas, read-only, click-card output).
        // Customer LTL policy: reads Alvys customer notes for LTL_TIER/LTL_ALLOW markers,
        // falls back to static ConsolidationOptions.CustomerPolicies. See
        // docs/ALVYS_API_DECISIONS.md decision #10 (Reuben 2026-07-17: Alvys has no first-
        // class per-customer LTL flag today; notes are the sanctioned interim).
        services.AddScoped<ICustomerLtlPolicyReader, CustomerNotesLtlPolicyReader>();

        services.AddScoped<ConsolidationCandidateService>();
        services.AddScoped<ConsolidationPlanService>();

        // Consolidation audit trail. Singleton in-memory store matching the same posture as
        // InMemoryAssignmentAuditStore; swap for an EF-backed store alongside Phase 2 writeback.
        services.AddSingleton<IConsolidationAuditStore, InMemoryConsolidationAuditStore>();

        // Internal, non-Alvys assignment audit. Singleton in-memory store for this slice;
        // swap for a persistent IAssignmentAuditStore in production.
        services.AddSingleton<IAssignmentAuditStore, InMemoryAssignmentAuditStore>();

        // Tool-local dispatcher saved views, persisted durably in AppDbContext (server-side, never
        // browser storage). Scoped to match the DbContext lifetime. Owner-scoped; no Alvys writeback.
        services.AddScoped<ISavedViewStore, EfSavedViewStore>();

        return services;
    }
}
