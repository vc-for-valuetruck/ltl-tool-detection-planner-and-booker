using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LtlTool.Api.Features.Ltl.Bol;

/// <summary>
/// DI wiring for the BOL intelligence slice. Registers the durable EF-backed suggestion store, the
/// pluggable text + field extractors, and the fail-closed read service.
///
/// <para>Extractors are bound behind interfaces so a cloud-OCR / LLM extractor can be swapped in
/// without touching the read/store/controller code. The defaults are dependency-free and deterministic
/// (built-in PDF text layer + regex fields) so a fresh clone / CI / the demo stays offline and
/// reproducible. Cloud OCR is selected only when <c>Bol:OcrEnabled = true</c>, and even then it is a
/// stub that fails closed — it is NOT wired to a live Azure service in this slice.</para>
///
/// <para>Alvys posture: read-only. The read path only downloads a document; suggestions are never
/// applied and never written to Alvys.</para>
/// </summary>
public static class BolServiceCollectionExtensions
{
    public static IServiceCollection AddLtlBol(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BolOptions>(configuration.GetSection(BolOptions.SectionName));

        // Durable suggestion store (AppDbContext — SQL Server in prod, SQLite in tests). Internal data.
        services.AddScoped<IBolSuggestionStore, EfBolSuggestionStore>();

        // Text extractor: built-in text-layer by default; the config-gated OCR stub when enabled.
        var ocrEnabled = configuration.GetValue<bool>($"{BolOptions.SectionName}:OcrEnabled");
        if (ocrEnabled)
            services.TryAddSingleton<IPdfTextExtractor, AzureOcrPdfTextExtractor>();
        else
            services.TryAddSingleton<IPdfTextExtractor, BuiltInPdfTextExtractor>();

        // Deterministic regex field extractor is the default. TryAdd so a host can register an
        // alternative before this call without a duplicate registration.
        services.TryAddSingleton<IBolFieldExtractor, RegexBolFieldExtractor>();

        services.AddScoped<BolReadService>();

        return services;
    }
}
