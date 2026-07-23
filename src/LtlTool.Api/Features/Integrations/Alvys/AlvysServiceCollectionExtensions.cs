using LtlTool.Api.Features.Integrations.Alvys.Webhooks;
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

        // Internal-API (Phase-2 consolidation) write path. Bound from "Alvys:InternalApi"; defaults
        // to Enabled=false with every per-operation arm switch off, so a fresh clone / CI /
        // production never dispatches an internal-API write. Observed-not-contracted (decision #10).
        services
            .AddOptions<AlvysInternalApiOptions>()
            .Bind(configuration.GetSection(AlvysInternalApiOptions.SectionName));

        // Consolidation auto-execute orchestrator flag. Bound from "Ltl:Writeback:AutoConsolidate";
        // defaults to Enabled=false so a fresh clone / CI / production never offers "Execute now".
        // Gates only the orchestrator — the internal-API arm switches above still apply independently
        // (spec §3.1). Bound here so AlvysReadinessService (which reports AutoConsolidateEnabled) can
        // read it without a layering cycle into the Ltl feature.
        services
            .AddOptions<ConsolidationAutoExecuteOptions>()
            .Bind(configuration.GetSection(ConsolidationAutoExecuteOptions.SectionName));
        // Inbound webhook receiver. Bound from "Alvys:Webhooks"; the signing secret lives server-side
        // (config / Key Vault) and is never exposed to the SPA. When blank the receiver fails closed.
        services
            .AddOptions<AlvysWebhookOptions>()
            .Bind(configuration.GetSection(AlvysWebhookOptions.SectionName));
        services.AddSingleton<IAlvysWebhookSignatureVerifier, AlvysWebhookSignatureVerifier>();
        services.AddScoped<IAlvysWebhookStore, EfAlvysWebhookStore>();
        // The processing queue is a singleton (it outlives any request scope); the processor drains it
        // off-thread and opens a fresh DbContext scope per event.
        services.AddSingleton<IAlvysWebhookProcessingQueue, AlvysWebhookProcessingQueue>();
        services.AddHostedService<AlvysWebhookProcessor>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAlvysSyncTracker, InMemoryAlvysSyncTracker>();
        services.AddScoped<IAlvysWriteGateway, AlvysWriteGateway>();
        services.AddScoped<IAlvysReadinessService, AlvysReadinessService>();

        // Durable operation outbox/audit + idempotency. Persisted in AppDbContext (server-side, never
        // browser storage); scoped to match the DbContext lifetime.
        services.AddScoped<IAlvysOperationStore, EfAlvysOperationStore>();
        services.AddScoped<IAlvysOperationRecorder, AlvysOperationRecorder>();
        services.AddScoped<IAlvysWriteClient, AlvysHttpWriteClient>();

        // Public-API billing-document upload path (multipart) + post-write reconciliation. Uploads
        // authenticate with the client-credentials Public-API token, never an internal session token.
        services.AddScoped<IAlvysDocumentUploadClient, AlvysHttpDocumentUploadClient>();
        services.AddScoped<IAlvysUploadReconciler, AlvysUploadReconciler>();

        // Internal-API write path: per-acting-user session-token provider + write client. The token
        // provider is a singleton so a session token is cached across scoped requests for the same
        // acting user; the write client is scoped like its Public-API counterpart. Neither ever
        // dispatches unless the internal API is enabled and the operation individually armed.
        services.AddSingleton<IAlvysInternalTokenProvider, AlvysInternalTokenProvider>();
        services.AddScoped<IAlvysInternalWriteClient, AlvysHttpInternalWriteClient>();

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

        // Internal-API named clients: one for per-acting-user session-token acquisition, one for the
        // internal write calls. Base addresses come from Alvys:InternalApi config at call time.
        services.AddHttpClient(AlvysInternalTokenProvider.AuthHttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds));
        services.AddHttpClient(AlvysHttpInternalWriteClient.HttpClientName,
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
