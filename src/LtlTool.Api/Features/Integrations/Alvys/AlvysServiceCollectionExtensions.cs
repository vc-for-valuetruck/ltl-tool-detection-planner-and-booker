using LtlTool.Api.Features.Integrations.Alvys.Writeback;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Integrations.Alvys;

/// <summary>
/// DI wiring for the Alvys integration. Live Alvys is the default source of
/// truth; the fallback provider is opt-in for local/UAT only. The writeback
/// boundary is disabled by default and never performs a live Alvys mutation
/// in this phase.
/// </summary>
public static class AlvysServiceCollectionExtensions
{
    public static IServiceCollection AddAlvysIntegration(
        this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<AlvysOptions>()
            .Bind(configuration.GetSection(AlvysOptions.SectionName));

        // Sandbox-gated writeback boundary. Bound from "Alvys:Writeback"; defaults to Disabled so
        // a fresh clone / CI / production never writes back to Alvys.
        services
            .AddOptions<AlvysWriteOptions>()
            .Bind(configuration.GetSection(AlvysWriteOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAlvysSyncTracker, InMemoryAlvysSyncTracker>();
        services.AddScoped<IAlvysWriteGateway, AlvysWriteGateway>();
        services.AddScoped<IAlvysReadinessService, AlvysReadinessService>();

        // Durable operation outbox/audit + idempotency. Persisted in AppDbContext (server-side, never
        // browser storage); scoped to match the DbContext lifetime.
        services.AddScoped<IAlvysOperationStore, EfAlvysOperationStore>();
        services.AddScoped<IAlvysOperationRecorder, AlvysOperationRecorder>();
        services.AddScoped<IAlvysWriteClient, AlvysHttpWriteClient>();

        var options = configuration.GetSection(AlvysOptions.SectionName).Get<AlvysOptions>()
            ?? new AlvysOptions();

        // Named clients: one for OAuth2 token requests, one for API calls.
        services.AddHttpClient(AlvysTokenProvider.AuthHttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds));

        services.AddHttpClient(AlvysClient.ApiHttpClientName, client =>
        {
            client.BaseAddress = new Uri(options.ApiBaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        // Sandbox write client — base address is overridden at call time with the sandbox URL.
        services.AddHttpClient(AlvysHttpWriteClient.SandboxHttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds));

        services.AddSingleton<IAlvysTokenProvider, AlvysTokenProvider>();

        // Startup token pre-warm (issue #80). No-ops unless the Live provider is configured with
        // credentials; runs off the startup thread so it can never block Kestrel from listening.
        services.AddHostedService<AlvysTokenPrewarmService>();

        // Provider selection. Live is the default source of truth; Fallback must
        // be chosen explicitly and is intended for local/UAT only.
        if (options.Provider == AlvysProvider.Fallback)
            services.AddScoped<IAlvysClient, FallbackAlvysClient>();
        else
            services.AddScoped<IAlvysClient, AlvysClient>();

        return services;
    }
}
