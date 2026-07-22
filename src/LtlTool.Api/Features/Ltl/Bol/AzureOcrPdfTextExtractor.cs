using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Bol;

/// <summary>
/// Config-gated cloud-OCR <see cref="IPdfTextExtractor"/> for scanned/image-only BOLs — a
/// <b>stub, intentionally not wired</b> to Azure Document Intelligence in this slice.
///
/// <para>It exists so the seam is real: <see cref="BolServiceCollectionExtensions"/> selects it when
/// <c>Bol:OcrEnabled = true</c>, and the config shape (<see cref="BolOptions.OcrEndpoint"/> /
/// <see cref="BolOptions.OcrApiKey"/>) is complete. But calling it always fails closed with a legible
/// <see cref="PdfTextExtractionException"/> — it never fabricates text and never silently returns
/// empty. Actually calling the Azure SDK is a follow-up that must land with its own review; wiring it
/// here would ship an unreviewed external-data path.</para>
/// </summary>
public sealed class AzureOcrPdfTextExtractor(IOptions<BolOptions> options) : IPdfTextExtractor
{
    private readonly BolOptions _options = options.Value;

    public string Name => "azure-document-intelligence (stub — not wired)";

    public Task<string?> ExtractTextAsync(byte[] content, string? contentType, CancellationToken ct = default)
    {
        // Fail closed. The presence of an endpoint/key does not make this live: the SDK call is
        // deliberately unimplemented so no unreviewed cloud-OCR path can ship in this slice.
        var configured = !string.IsNullOrWhiteSpace(_options.OcrEndpoint)
            && !string.IsNullOrWhiteSpace(_options.OcrApiKey);
        throw new PdfTextExtractionException(
            configured
                ? "Cloud OCR (Azure Document Intelligence) is configured but not wired in this build. "
                  + "The read fails closed rather than calling an unreviewed external service."
                : "Cloud OCR is enabled but not configured (missing endpoint/key), and is not wired in "
                  + "this build. The read fails closed.");
    }
}
