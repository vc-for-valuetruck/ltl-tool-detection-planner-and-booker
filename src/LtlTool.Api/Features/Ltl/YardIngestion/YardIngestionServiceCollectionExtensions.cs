using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LtlTool.Api.Features.Ltl.YardIngestion;

/// <summary>
/// DI wiring for the Yard→LTL scheduler ingestion pipeline: options binding, the durable EF-backed
/// event store, the validation/classification service, and the metrics counters. Internal LTL data
/// only — nothing registered here reads or writes Alvys. The service-to-service authorization handler
/// is registered here too so the composition root only needs one call.
/// </summary>
public static class YardIngestionServiceCollectionExtensions
{
    public static IServiceCollection AddYardIngestion(
        this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<YardIngestionOptions>()
            .Bind(configuration.GetSection(YardIngestionOptions.SectionName));

        // Durable inbox + projection store, scoped to the AppDbContext lifetime.
        services.AddScoped<IYardEventStore, EfYardEventStore>();
        services.AddScoped<YardEventIngestionService>();

        // Counters live on a singleton meter; the underlying instruments are thread-safe.
        services.AddSingleton<YardIngestionMetrics>();

        // Service-to-service authorization handler for the ingest policy (app role / scope).
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, YardEventIngestHandler>());

        return services;
    }
}
