using LtlTool.Api.Features.Integrations.Yard.Webhooks;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LtlTool.Api.Features.Integrations.Yard;

/// <summary>
/// DI wiring for the Yard integration (issue #166). Registers the read-only presence client and the
/// inbound webhook receiver pipeline. Everything degrades honestly when unconfigured: the presence
/// client returns null (never throws at startup) and the webhook receiver is dormant until
/// <c>Yard:Webhooks:Enabled</c> is turned on with a signing secret. No credentials are exposed to the
/// SPA; Alvys stays the read-only source of truth.
/// </summary>
public static class YardServiceCollectionExtensions
{
    public static IServiceCollection AddYardIntegration(
        this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<YardOptions>()
            .Bind(configuration.GetSection(YardOptions.SectionName));

        services
            .AddOptions<YardWebhookOptions>()
            .Bind(configuration.GetSection(YardWebhookOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        // IMemoryCache is registered by the LTL layer; TryAdd keeps this extension self-sufficient.
        services.AddMemoryCache();

        // Presence client + its Entra token provider. Both singletons so the short-lived presence cache
        // and the token cache are shared across request scopes (they hold no per-request state).
        services.AddSingleton<IYardTokenProvider, YardTokenProvider>();
        services.AddSingleton<IYardPresenceClient, YardPresenceClient>();

        // Webhook receiver pipeline. The signature verifier and processing queue are singletons; the
        // store is scoped (it wraps the scoped DbContext); the processor is a hosted singleton that opens
        // a fresh scope per event.
        services.AddSingleton<IYardWebhookSignatureVerifier, YardWebhookSignatureVerifier>();
        services.AddSingleton<IYardWebhookProcessingQueue, YardWebhookProcessingQueue>();
        services.AddScoped<IYardWebhookStore, EfYardWebhookStore>();
        services.AddHostedService<YardWebhookProcessor>();

        var options = configuration.GetSection(YardOptions.SectionName).Get<YardOptions>()
            ?? new YardOptions();

        // Named clients: one for Entra token requests, one for presence API calls.
        services.AddHttpClient(YardTokenProvider.AuthHttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds));

        services.AddHttpClient(YardPresenceClient.ApiHttpClientName, client =>
        {
            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
                client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        return services;
    }
}
