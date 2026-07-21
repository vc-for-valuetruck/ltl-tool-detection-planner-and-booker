using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LtlTool.Api.Features.Ltl.Signals;

/// <summary>
/// DI wiring for the Phase 6 inbound signal-ingestion layer. Registers the durable EF-backed store,
/// the deterministic keyword extractor (always the default), and the fail-closed ingest service.
///
/// <para>The extractor is bound behind <see cref="ISignalExtractor"/> so an LLM-backed extractor can
/// be swapped in without touching the ingest/store/controller code. It is intentionally NOT wired to
/// a live model here — the deterministic extractor keeps a fresh clone / CI / the demo fully offline
/// and reproducible.</para>
///
/// <para>Alvys posture: read-only. Nothing in this slice reads from or writes to Alvys.</para>
/// </summary>
public static class SignalServiceCollectionExtensions
{
    public static IServiceCollection AddLtlSignals(this IServiceCollection services)
    {
        // Durable signal store (AppDbContext — SQL Server in prod, SQLite in tests). Scoped to the
        // DbContext lifetime. Internal data; no Alvys writeback.
        services.AddScoped<ISignalStore, EfSignalStore>();

        // Deterministic keyword extractor is the default. TryAdd so a host that wants to register an
        // LLM-backed ISignalExtractor before this call wins, without duplicate registration.
        services.TryAddSingleton<ISignalExtractor, KeywordSignalExtractor>();

        services.AddScoped<SignalIngestService>();

        return services;
    }
}
