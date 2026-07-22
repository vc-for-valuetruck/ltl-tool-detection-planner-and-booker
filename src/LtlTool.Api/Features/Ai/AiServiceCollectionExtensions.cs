using LtlTool.Api.Features.Ai.Narrative;
using LtlTool.Api.Features.Ai.Narrative.Contracts;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LtlTool.Api.Features.Ai;

/// <summary>
/// DI wiring for the AI narrative slice (Phase 2 · Sprint 1, #149/#150). Binds the endpoint's
/// <see cref="AiOptions"/> kill switch plus the service's <see cref="AiFeatureFlags"/> and
/// <see cref="AzureOpenAiOptions"/>, then registers the <see cref="INarrativeService"/>
/// implementation gated on <c>AI:NarrativeEnabled</c>:
/// <list type="bullet">
/// <item><c>true</c> — the real <see cref="NarrativeService"/> (plan source + Azure OpenAI chat
/// client + 10-minute in-memory cache). The chat client opens no connection until the service
/// actually calls the model, so a fresh clone / CI never touches Azure.</item>
/// <item><c>false</c> (default) — the fail-closed <see cref="NullNarrativeService"/>.</item>
/// </list>
/// Read-only against Alvys — nothing here adds a writeback path or an EF DbSet.
/// </summary>
public static class AiServiceCollectionExtensions
{
    public static IServiceCollection AddAiNarrative(
        this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<AiOptions>()
            .Bind(configuration.GetSection(AiOptions.SectionName));

        services
            .AddOptions<AiFeatureFlags>()
            .Bind(configuration.GetSection(AiFeatureFlags.SectionName));

        services
            .AddOptions<AzureOpenAiOptions>()
            .Bind(configuration.GetSection(AzureOpenAiOptions.SectionName));

        var narrativeEnabled = configuration.GetValue<bool>($"{AiOptions.SectionName}:NarrativeEnabled");
        if (narrativeEnabled)
        {
            // IMemoryCache is already added by the LTL layer; TryAdd keeps this self-sufficient.
            services.AddMemoryCache();
            services.TryAddScoped<INarrativePlanSource, ConsolidationNarrativePlanSource>();
            services.TryAddSingleton<INarrativeChatClient, AzureOpenAiNarrativeChatClient>();
            services.TryAddScoped<INarrativeService, NarrativeService>();
        }
        else
        {
            services.TryAddScoped<INarrativeService, NullNarrativeService>();
        }

        return services;
    }
}
