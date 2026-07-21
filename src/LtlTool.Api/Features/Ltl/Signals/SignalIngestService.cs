namespace LtlTool.Api.Features.Ltl.Signals;

/// <summary>
/// Phase 6 inbound signal ingestion — the fail-closed orchestrator. Validates the request, runs the
/// pluggable <see cref="ISignalExtractor"/>, verifies every produced signal, and persists the batch
/// atomically through <see cref="ISignalStore.AddBatch"/>.
///
/// <para><b>Fail-closed semantics (anti-failure map 3g / 3o boundary).</b> If the extractor throws,
/// is unavailable, or emits <em>any</em> signal without a verbatim evidence quote drawn from the
/// source text, the whole request fails: a <see cref="SignalIngestException"/> is raised and
/// <b>nothing is written</b>. There are no partial writes and no silent drops — an un-evidenced
/// signal can never slip into an internal surface, which is what keeps the audit trail defensible.</para>
///
/// <para>Alvys posture: read-only. Ingestion writes only to the tool's own signal store; accepting a
/// signal later annotates internal surfaces and never mutates Alvys.</para>
/// </summary>
public sealed class SignalIngestService(
    ISignalExtractor extractor,
    ISignalStore store,
    TimeProvider clock,
    ILogger<SignalIngestService> logger)
{
    private static readonly string[] AllowedSourceTypes = ["note", "email", "transcript", "call"];

    public async Task<SignalIngestResponse> IngestAsync(
        SignalIngestRequest request, string ingestedBy, CancellationToken ct)
    {
        var sourceType = Normalize(request.SourceType);
        if (string.IsNullOrEmpty(sourceType))
            throw new SignalIngestException(
                $"sourceType is required and must be one of: {string.Join(", ", AllowedSourceTypes)}.");
        if (!AllowedSourceTypes.Contains(sourceType))
            throw new SignalIngestException(
                $"sourceType '{request.SourceType}' is not recognized. Use one of: {string.Join(", ", AllowedSourceTypes)}.");

        var sourceId = request.SourceId?.Trim();
        if (string.IsNullOrEmpty(sourceId))
            throw new SignalIngestException("sourceId is required so every signal is traceable to its source.");

        var text = request.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new SignalIngestException("text is required — there is nothing to extract signals from.");

        IReadOnlyList<ExtractedSignal> extracted;
        try
        {
            extracted = await extractor.ExtractAsync(sourceType, sourceId, text, ct);
        }
        catch (SignalExtractorException ex)
        {
            // Extractor unavailable/failed → fail closed. Record nothing.
            logger.LogWarning(ex, "Signal extraction failed for source {SourceType}/{SourceId}; recording nothing.",
                sourceType, sourceId);
            throw new SignalIngestException(
                $"Signal extraction failed ({extractor.Name}): {ex.Message}. Nothing was recorded.");
        }

        // Validate the WHOLE batch before persisting anything. A single un-evidenced signal fails
        // the entire request — no partial writes.
        foreach (var signal in extracted)
        {
            var quote = signal.EvidenceQuote?.Trim();
            if (string.IsNullOrEmpty(quote))
                throw new SignalIngestException(
                    $"Extractor produced a {signal.Type} signal without an evidence quote. " +
                    "Ingestion fails closed; nothing was recorded.");

            // The quote must be a verbatim excerpt of the source — never fabricated. Compared
            // case-insensitively and whitespace-normalized so a legibly trimmed span still matches.
            if (!IsVerbatim(text, quote))
                throw new SignalIngestException(
                    $"Extractor produced a {signal.Type} signal whose evidence quote is not present in the " +
                    "source text. Ingestion fails closed; nothing was recorded.");
        }

        var now = clock.GetUtcNow();
        var records = extracted.Select(s => new SignalRecord
        {
            Id = Guid.NewGuid().ToString("n"),
            SourceType = sourceType,
            SourceId = sourceId,
            SignalType = s.Type.ToString(),
            Confidence = s.Confidence,
            EvidenceQuote = s.EvidenceQuote.Trim(),
            SuggestedSurface = s.SuggestedSurface.ToString(),
            Summary = s.Summary,
            LoadNumber = string.IsNullOrWhiteSpace(request.LoadNumber) ? null : request.LoadNumber.Trim(),
            Status = SignalStatus.Pending.ToString(),
            IngestedBy = ingestedBy,
            CreatedAt = now,
        }).ToArray();

        store.AddBatch(records);

        logger.LogInformation(
            "Ingested {Count} signal(s) from {SourceType}/{SourceId} via {Extractor}.",
            records.Length, sourceType, sourceId, extractor.Name);

        return new SignalIngestResponse(records.Length, records.Select(SignalMapping.ToView).ToArray());
    }

    private static string Normalize(string? s) => s?.Trim().ToLowerInvariant() ?? string.Empty;

    // Whitespace-insensitive containment: collapse runs of whitespace on both sides so a trimmed
    // sentence still matches the original span. Guards against a fabricated (non-source) quote.
    private static bool IsVerbatim(string source, string quote)
    {
        var trimmedQuote = quote.TrimEnd('…').Trim();
        if (trimmedQuote.Length == 0) return false;
        return Collapse(source).Contains(Collapse(trimmedQuote), StringComparison.OrdinalIgnoreCase);
    }

    private static string Collapse(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
