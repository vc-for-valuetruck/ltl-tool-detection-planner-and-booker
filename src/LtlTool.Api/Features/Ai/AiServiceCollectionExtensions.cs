using LtlTool.Api.Features.Ai.Narrative.Contracts;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LtlTool.Api.Features.Ai;

/// <summary>
/// DI wiring for the AI narrative HTTP surface (#150). Binds <see cref="AiOptions"/> and registers
/// a fallback <see cref="INarrativeService"/> so the endpoint has a dependency to activate before
/// the real NarrativeService (#149) lands. Read-only against Alvys.
/// </summary>
public static class AiServiceCollectionExtensions
{
    public static IServiceCollection AddAiNarrative(
        this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<AiOptions>()
            .Bind(configuration.GetSection(AiOptions.SectionName));

        // TODO(#149): remove this fallback registration once NarrativeService lands and registers
        // the real INarrativeService. TryAdd so the real service (registered by #149) wins at merge.
        services.TryAddScoped<INarrativeService, NullNarrativeService>();

        return services;
    }
}
