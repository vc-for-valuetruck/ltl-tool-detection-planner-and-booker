using LtlTool.Api.Features.Integrations.Alvys;

namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// Turns raw Alvys load notes and document metadata into the LTL decision-support view for
/// accessorial revenue protection. Pure and synchronous — the per-load notes/documents fetch
/// lives in <see cref="LtlLoadService"/>; this only interprets what was fetched and never
/// fabricates signals.
///
/// <para>
/// Deterministic keyword matching always runs. An optional <see cref="IAccessorialSignalExtractor"/>
/// (AI layer, disabled by default) is called by <see cref="LtlLoadService"/> after this
/// synchronous pass and its signals are merged in. See <c>LtlLoadService.GetAccessorialSignalsAsync</c>.
/// </para>
///
/// <para>
/// Guardrail: when BOTH notes and documents are empty/absent the context is
/// <see cref="AccessorialReviewContext.NotEvaluated"/> — the analyzer never reports "no signals"
/// for a load it had no evidence to inspect. This mirrors the <c>VisibilityContext</c> pattern.
/// </para>
/// </summary>
public sealed class AccessorialSignalAnalyzer
{
    // Keyword sets per accessorial type (case-insensitive substring match).
    private static readonly (AccessorialSignalType Type, string[] Keywords)[] DetectionRules =
    [
        (AccessorialSignalType.Detention, ["detention", "detain", "waited", "waiting", "wait time",
            "detained", "delay at", "held at", "delay charge"]),
        (AccessorialSignalType.Layover, ["layover", "lay over", "overnight", "held overnight",
            "forced lay", "driver layover"]),
        (AccessorialSignalType.Lumper, ["lumper", "unload fee", "unloading fee", "loading fee",
            "lumper charge", "lumper service", "labor charge"]),
        (AccessorialSignalType.Reconsignment, ["reconsign", "redelivery", "re-delivery",
            "address change", "redirect", "reroute", "rerouted", "delivery address changed"]),
        (AccessorialSignalType.Handling, ["hand unload", "hand-unload", "hand load", "hand-load",
            "pallet jack", "palletjack", "no dock", "floor load", "floor-load", "sort and segregate",
            "sort & segregate", "driver assist", "driver-assist", "handball", "hand ball",
            "handling fee", "cross dock", "cross-dock", "crossdock"]),
        (AccessorialSignalType.InsideDelivery, ["inside delivery", "inside-delivery", "deliver inside",
            "carry inside", "white glove", "white-glove", "threshold delivery"]),
        (AccessorialSignalType.WeekendDelivery, ["weekend delivery", "saturday delivery",
            "sunday delivery", "after hours", "after-hours", "afterhours", "weekend pickup",
            "saturday pickup", "sunday pickup"]),
    ];

    /// <summary>
    /// Build the accessorial-signal context from fetched Alvys notes and document listings.
    /// When both are empty the result is <see cref="AccessorialReviewContext.NotEvaluated"/> —
    /// empty evidence must never be read as a clean bill of health.
    /// </summary>
    public AccessorialReviewContext BuildContext(
        IReadOnlyList<AlvysLoadNote> notes,
        IReadOnlyList<AlvysLoadDocument> documents)
    {
        // No evidence to inspect → not evaluated (mirrors VisibilityContext.NotEvaluated).
        if (notes.Count == 0 && documents.Count == 0)
            return AccessorialReviewContext.NotEvaluated;

        var signals = new List<AccessorialSignal>();

        foreach (var note in notes)
        {
            var text = note.Description;
            if (string.IsNullOrWhiteSpace(text)) continue;
            signals.AddRange(ExtractFromText(text, note.Id, "Note"));
        }

        foreach (var doc in documents)
        {
            // Document metadata: type + attachment path are the available text signals.
            var text = BuildDocumentText(doc);
            if (string.IsNullOrWhiteSpace(text)) continue;
            signals.AddRange(ExtractFromText(text, doc.Id, "Document"));
        }

        return new AccessorialReviewContext { Evaluated = true, Signals = signals };
    }

    /// <summary>
    /// Merge AI-derived signals into an already-built deterministic context. The merged list
    /// deduplicates by (Type, SourceId) so a keyword match and an AI match for the same source
    /// don't both appear; the deterministic keyword signal takes precedence.
    /// </summary>
    public static AccessorialReviewContext MergeAiSignals(
        AccessorialReviewContext deterministicContext,
        IReadOnlyList<AccessorialSignal> aiSignals)
    {
        if (!deterministicContext.Evaluated || aiSignals.Count == 0) return deterministicContext;

        // Key = (Type, SourceId) — prefer deterministic (already in list) over AI duplicates.
        var seen = deterministicContext.Signals
            .Select(s => (s.Type, s.SourceId))
            .ToHashSet();

        var merged = deterministicContext.Signals.ToList();
        foreach (var ai in aiSignals)
        {
            if (seen.Add((ai.Type, ai.SourceId)))
                merged.Add(ai);
        }

        return new AccessorialReviewContext { Evaluated = true, Signals = merged };
    }

    private static IEnumerable<AccessorialSignal> ExtractFromText(
        string text, string sourceId, string sourceType)
    {
        foreach (var (type, keywords) in DetectionRules)
        {
            foreach (var keyword in keywords)
            {
                var idx = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;

                // Extract a short evidence snippet around the match.
                var quote = ExtractSnippet(text, idx, keyword.Length);
                yield return new AccessorialSignal
                {
                    Type = type,
                    EvidenceQuote = quote,
                    SourceId = sourceId,
                    SourceType = sourceType,
                    Confidence = 1.0,
                };
                // One signal per type per source (first keyword match wins).
                break;
            }
        }

        // Residual "Other" — any remaining text the billing team should review (only when no
        // typed signal was found for this source and the text is suspiciously cost-related).
        if (HasOtherCostIndicator(text) && !HasTypedSignal(text))
        {
            yield return new AccessorialSignal
            {
                Type = AccessorialSignalType.Other,
                EvidenceQuote = ExtractSnippet(text, 0, Math.Min(text.Length, 80)),
                SourceId = sourceId,
                SourceType = sourceType,
                Confidence = 0.7,
            };
        }
    }

    private static string ExtractSnippet(string text, int matchIndex, int matchLength)
    {
        const int contextChars = 40;
        var start = Math.Max(0, matchIndex - contextChars);
        var end = Math.Min(text.Length, matchIndex + matchLength + contextChars);
        var snippet = text[start..end].Trim();
        if (start > 0) snippet = "…" + snippet;
        if (end < text.Length) snippet += "…";
        return snippet;
    }

    private static string? BuildDocumentText(AlvysLoadDocument doc)
    {
        // Documents carry no free-text body in this slice; the filename/type is the signal.
        var parts = new[] { doc.AttachmentType, doc.AttachmentPath }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        return string.Join(" ", parts);
    }

    private static readonly string[] OtherCostKeywords =
        ["charge", "fee", "extra", "additional", "cost", "billed", "invoice"];

    private static bool HasOtherCostIndicator(string text) =>
        OtherCostKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static bool HasTypedSignal(string text) =>
        DetectionRules.SelectMany(r => r.Keywords)
            .Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
}
