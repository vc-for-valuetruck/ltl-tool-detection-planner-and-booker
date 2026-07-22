namespace LtlTool.Api.Features.Ltl.Bol;

/// <summary>
/// Turns raw document bytes into plain text for the BOL field extractor. The default implementation
/// (<see cref="BuiltInPdfTextExtractor"/>) reads a PDF's embedded text layer using only the BCL — no
/// third-party PDF dependency — so a fresh clone / CI / the demo stays offline and reproducible and
/// there is no license or NuGet-restore risk.
///
/// <para><b>Text layer only.</b> This extractor does NOT rasterize or OCR. A scanned/image-only BOL
/// with no embedded text yields no text, and the read fails closed rather than guessing. An OCR
/// backend is pluggable behind this interface (see <see cref="AzureOcrPdfTextExtractor"/>) but is a
/// config-gated stub — NOT wired to a live service in this slice.</para>
/// </summary>
public interface IPdfTextExtractor
{
    /// <summary>Human-readable name of the active extractor, surfaced for honesty in the UI/audit.</summary>
    string Name { get; }

    /// <summary>
    /// Extract plain text from <paramref name="content"/>. Returns <c>null</c> or empty when no text
    /// layer is present (e.g. an image-only scan) so the caller can fail closed. Throws
    /// <see cref="PdfTextExtractionException"/> when the document is malformed / cannot be parsed.
    /// </summary>
    Task<string?> ExtractTextAsync(byte[] content, string? contentType, CancellationToken ct = default);
}

/// <summary>
/// Signals that document text could not be extracted (malformed PDF, unsupported content, backend
/// error). The read service converts this into a fail-closed <see cref="BolReadException"/> — the
/// read records nothing.
/// </summary>
public sealed class PdfTextExtractionException(string message, Exception? inner = null)
    : Exception(message, inner);
