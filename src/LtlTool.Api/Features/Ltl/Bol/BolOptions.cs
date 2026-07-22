namespace LtlTool.Api.Features.Ltl.Bol;

/// <summary>
/// Configuration for the BOL reader. Bound from the <c>Bol</c> section. Everything here is optional;
/// with no configuration the reader uses the dependency-free built-in text-layer extractor.
///
/// <para><b>OCR is a stub.</b> The cloud-OCR adapter (Azure Document Intelligence) is present as an
/// interface + config shape only. Even with <see cref="OcrEnabled"/> = true and an endpoint/key set,
/// it is intentionally NOT wired to the live Azure service in this slice — it fails closed with a
/// legible "not wired" error. Wiring it is a follow-up that must land with its own review.</para>
/// </summary>
public sealed class BolOptions
{
    public const string SectionName = "Bol";

    /// <summary>When true, selects the cloud-OCR extractor instead of the built-in text-layer one.</summary>
    public bool OcrEnabled { get; set; }

    /// <summary>Azure Document Intelligence endpoint (server-side only; never exposed to the SPA).</summary>
    public string? OcrEndpoint { get; set; }

    /// <summary>
    /// Azure Document Intelligence API key. Server-side only. NEVER logged, NEVER returned to the SPA.
    /// Present so the config shape is complete; the adapter is not wired to Azure in this slice.
    /// </summary>
    public string? OcrApiKey { get; set; }
}
