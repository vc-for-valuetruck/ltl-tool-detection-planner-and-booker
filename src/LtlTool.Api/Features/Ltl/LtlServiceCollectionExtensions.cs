using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl.Agent;
using LtlTool.Api.Features.Ltl.Assignment;
using LtlTool.Api.Features.Ltl.Consolidation;
using LtlTool.Api.Features.Ltl.Notifications;
using LtlTool.Api.Features.Ltl.Optimization;
using LtlTool.Api.Features.Ltl.SavedViews;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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
        services.AddScoped<AccessorialSignalAnalyzer>();
        services.AddScoped<LtlNormalizationService>();
        services.AddScoped<MatchScoringService>();
        services.AddScoped<MatchService>();
        services.AddScoped<LtlLoadService>();
        services.AddScoped<AssignmentValidationService>();

        // Accessorial signal AI extractor (Phase 6). Disabled by default — the
        // NullAccessorialSignalExtractor is registered until Ltl:AccessorialAi:Enabled=true and
        // credentials are supplied server-side. The AccessorialSignalAnalyzer (deterministic
        // keyword extraction) is always registered regardless of this setting.
        var accessorialAiEnabled = configuration
            .GetSection($"{LtlOptions.SectionName}:AccessorialAi")
            .GetValue<bool>("Enabled");
        if (accessorialAiEnabled)
        {
            services.AddHttpClient<IAccessorialSignalExtractor, AzureOpenAiAccessorialSignalExtractor>();
        }
        else
        {
            services.AddSingleton<IAccessorialSignalExtractor, NullAccessorialSignalExtractor>();
        }

        // Phase 2 optimization engines (trailer fit, capacity/cost solver, stop sequencer). Each
        // follows the AccessorialAI Null-fallback pattern: the Null… implementation is registered
        // until its Ltl:Optimization:* flag is turned on and a real engine is wired. All flags
        // default to false, so a fresh clone / CI / the demo only ever run the no-op services and
        // no half-built optimization can affect behavior. The engines are pure compute over
        // Alvys-derived inputs — they never fetch data themselves.
        var optimization = configuration
            .GetSection($"{LtlOptions.SectionName}:{nameof(LtlOptions.Optimization)}")
            .Get<OptimizationOptions>() ?? new OptimizationOptions();

        if (optimization.TrailerFit.Enabled)
        {
            // Real trailer-fit engine: binds its own options and a named HttpClient to the packing
            // sidecar. The BaseUrl/timeout live in Ltl:Optimization:TrailerFit. The service degrades
            // to an Unknown verdict on any sidecar failure, so a fresh clone with the flag on but no
            // reachable sidecar still runs — it just reports "verify at dock".
            services
                .AddOptions<TrailerFitOptions>()
                .Bind(configuration.GetSection(TrailerFitOptions.SectionName));

            services.AddHttpClient<ITrailerFitClient, HttpTrailerFitClient>((sp, http) =>
            {
                var opts = sp.GetRequiredService<IOptions<TrailerFitOptions>>().Value;
                if (!string.IsNullOrWhiteSpace(opts.BaseUrl))
                {
                    var baseUrl = opts.BaseUrl.EndsWith('/') ? opts.BaseUrl : opts.BaseUrl + "/";
                    http.BaseAddress = new Uri(baseUrl);
                }
                // Generous transport ceiling; the per-request timeout is enforced via a linked
                // CancellationToken inside the service so a hang degrades rather than throws.
                http.Timeout = TimeSpan.FromSeconds(Math.Max(2, opts.TimeoutSeconds + 5));
            });

            services.AddScoped<ITrailerFitService, HttpTrailerFitService>();
        }
        else
        {
            services.AddSingleton<ITrailerFitService, NullTrailerFitService>();
        }

        // Capacity/cost solver + stop sequencer (Phase 2 M3). Both are gated behind
        // Ltl:Optimization:Solver:Enabled and register their Null… fallbacks when off. Wiring lives
        // in OptimizationServiceCollectionExtensions to keep this shared composition root stable
        // while parallel Phase 2 branches edit it.
        services.AddLtlCapacityCostOptimization(configuration);

        // Consolidation planner (Phase 1 pilot: Laredo → Dallas, read-only, click-card output).
        // Customer LTL policy: reads Alvys customer notes for LTL_TIER/LTL_ALLOW markers,
        // falls back to static ConsolidationOptions.CustomerPolicies. See
        // docs/ALVYS_API_DECISIONS.md decision #10 (Reuben 2026-07-17: Alvys has no first-
        // class per-customer LTL flag today; notes are the sanctioned interim).
        services.AddScoped<ICustomerLtlPolicyReader, CustomerNotesLtlPolicyReader>();

        services.AddScoped<ConsolidationCandidateService>();
        services.AddScoped<ConsolidationOpportunityService>();
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

        // Phase 2 M4 agent command surface (tool-style catalog + read-only dispatch). Feature-gated
        // behind Ltl:Optimization:AgentCommands:Enabled with a Null dispatcher fallback; every command
        // reuses the decision-support services above and writes nothing to Alvys.
        services.AddLtlAgentCommands(configuration);

        // Phase 6 workflow notifications: idempotent in-app feed (always-on) plus config-gated
        // Teams/email adapters and the background trigger poller. Read-only against Alvys.
        services.AddLtlNotifications(configuration);

        return services;
    }
}
