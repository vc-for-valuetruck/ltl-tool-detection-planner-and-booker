using LtlTool.Api.Features.Integrations.Alvys;

namespace LtlTool.Api.Features.Ltl.Bol;

/// <summary>
/// The fail-closed orchestrator for reading a Bill of Lading into suggested fields. It fetches the
/// document bytes over the existing read-only #141 document surface, extracts text, runs the pluggable
/// field extractor, verifies every suggestion, and persists the batch atomically.
///
/// <para><b>Fail-closed semantics (anti-failure map 3g — multi-BOL/POD billing leakage).</b> If the
/// document can't be fetched, no text can be extracted, the extractor throws, or <em>any</em>
/// suggested field lacks a verbatim evidence quote drawn from the document text, the whole read fails:
/// a <see cref="BolReadException"/> is raised and <b>nothing is written</b>. No partial suggestions,
/// no silent drops — an un-evidenced value can never slip into the review queue.</para>
///
/// <para><b>Guardrails.</b> Suggestions are NEVER auto-applied and NEVER written to Alvys. A human
/// accepts each field in the UI; only then does it annotate internal surfaces, fully audited. The read
/// path is read-only against Alvys (document download only).</para>
/// </summary>
public sealed class BolReadService(
    IAlvysClient alvys,
    IPdfTextExtractor textExtractor,
    IBolFieldExtractor fieldExtractor,
    IBolSuggestionStore store,
    TimeProvider clock,
    ILogger<BolReadService> logger)
{
    public async Task<BolReadResponse> ReadAsync(
        string loadNumber, string documentId, string requestedBy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(loadNumber))
            throw new BolReadException("loadNumber is required to read a BOL.");
        if (string.IsNullOrWhiteSpace(documentId))
            throw new BolReadException("documentId is required to read a BOL.");

        loadNumber = loadNumber.Trim();
        documentId = documentId.Trim();

        // Read-only Alvys fetch of the document bytes (re-lists docs server-side; caller can't forge a
        // URL). Null = unknown/expired/no link/transport error → fail closed.
        AlvysDocumentContent? content;
        try
        {
            content = await alvys.DownloadLoadDocumentAsync(loadNumber, documentId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "BOL read: document fetch failed for load {LoadNumber} document {DocumentId}.",
                loadNumber, documentId);
            throw new BolReadException(
                "The BOL document could not be fetched from Alvys. Nothing was suggested.");
        }

        if (content is null || content.Content.Length == 0)
            throw new BolReadException(
                "The BOL document could not be fetched from Alvys (not found, no download link, or expired). "
                + "Nothing was suggested.");

        // Extract the text layer. A malformed PDF throws; an image-only scan yields no text — both
        // fail closed rather than guessing at a number a biller would then trust.
        string? text;
        try
        {
            text = await textExtractor.ExtractTextAsync(content.Content, content.ContentType, ct);
        }
        catch (PdfTextExtractionException ex)
        {
            logger.LogWarning(ex, "BOL read: text extraction failed for load {LoadNumber} document {DocumentId}.",
                loadNumber, documentId);
            throw new BolReadException(
                $"The BOL text could not be read ({textExtractor.Name}): {ex.Message} Nothing was suggested.");
        }

        if (string.IsNullOrWhiteSpace(text))
            throw new BolReadException(
                "No readable text layer was found in the BOL document (it may be a scanned image). "
                + "Nothing was suggested — enable cloud OCR to read scanned documents.");

        IReadOnlyList<ExtractedBolField> fields;
        try
        {
            fields = fieldExtractor.Extract(text);
        }
        catch (BolFieldExtractionException ex)
        {
            logger.LogWarning(ex, "BOL read: field extraction failed for load {LoadNumber} document {DocumentId}.",
                loadNumber, documentId);
            throw new BolReadException(
                $"BOL field extraction failed ({fieldExtractor.Name}): {ex.Message} Nothing was suggested.");
        }

        // Validate the WHOLE batch before persisting anything. A single un-evidenced field fails the
        // entire read — no partial suggestions.
        foreach (var field in fields)
        {
            var quote = field.EvidenceQuote?.Trim();
            if (string.IsNullOrEmpty(quote))
                throw new BolReadException(
                    $"The extractor produced a {field.Field} suggestion without an evidence quote. "
                    + "The read fails closed; nothing was suggested.");

            if (!IsVerbatim(text, quote))
                throw new BolReadException(
                    $"The extractor produced a {field.Field} suggestion whose evidence quote is not present "
                    + "in the document text. The read fails closed; nothing was suggested.");
        }

        var now = clock.GetUtcNow();
        var records = fields.Select(f => new BolFieldSuggestionRecord
        {
            Id = Guid.NewGuid().ToString("n"),
            LoadNumber = loadNumber,
            DocumentId = documentId,
            DocumentName = content.FileName,
            Field = f.Field.ToString(),
            Value = f.Value.Trim(),
            Confidence = f.Confidence,
            EvidenceQuote = f.EvidenceQuote.Trim(),
            ExtractorName = fieldExtractor.Name,
            Status = BolSuggestionStatus.Pending.ToString(),
            CreatedBy = requestedBy,
            CreatedAt = now,
        }).ToArray();

        store.AddBatch(records);

        logger.LogInformation(
            "BOL read: suggested {Count} field(s) for load {LoadNumber} document {DocumentId} via {Extractor}.",
            records.Length, loadNumber, documentId, fieldExtractor.Name);

        return new BolReadResponse(
            loadNumber, documentId, fieldExtractor.Name, records.Length,
            records.Select(BolMapping.ToView).ToArray());
    }

    // Whitespace-insensitive containment: collapse runs of whitespace on both sides so a legibly
    // trimmed line still matches its span in the source. Guards against a fabricated (non-source) quote.
    private static bool IsVerbatim(string source, string quote)
    {
        var trimmed = quote.TrimEnd('…').Trim();
        if (trimmed.Length == 0) return false;
        return Collapse(source).Contains(Collapse(trimmed), StringComparison.OrdinalIgnoreCase);
    }

    private static string Collapse(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
