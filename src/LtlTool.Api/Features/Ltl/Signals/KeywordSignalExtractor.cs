namespace LtlTool.Api.Features.Ltl.Signals;

/// <summary>
/// Deterministic keyword/dictionary <see cref="ISignalExtractor"/> — the always-registered default.
/// It classifies text against a fixed dictionary per <see cref="SignalType"/>, and every emitted
/// signal carries a verbatim excerpt of the matched sentence as its evidence quote. No network call,
/// no LLM, no numeric assertion — the output is fully reproducible from the input, which keeps the
/// dispatch decision explainable and the tests offline.
///
/// <para>The extractor operates sentence-by-sentence so an evidence quote is a real, human-readable
/// span of the source rather than a fixed-width window. At most one signal per (type, sentence) so a
/// paragraph packed with the same keyword doesn't flood the queue.</para>
/// </summary>
public sealed class KeywordSignalExtractor : ISignalExtractor
{
    public string Name => "deterministic-keyword";

    // (type, suggested surface, keyword dictionary). Substring, case-insensitive.
    private static readonly (SignalType Type, LtlSurface Surface, string[] Keywords)[] Rules =
    [
        (SignalType.AccessorialEvidence, LtlSurface.BillingWorklistBadge,
            ["detention", "detained", "layover", "lumper", "held at", "waited", "wait time",
             "reconsign", "redelivery", "inside delivery", "liftgate fee", "after hours", "appointment fee",
             "driver assist", "hand unload", "hand-unload", "sort and segregate"]),
        (SignalType.ConsolidationOpportunity, LtlSurface.NextBestAction,
            ["consolidate", "consolidation", "combine", "share a trailer", "share trailer",
             "same trailer", "one linehaul", "partial", "a few pallets", "couple pallets"]),
        (SignalType.CustomerVisibilityPosture, LtlSurface.MatchWarning,
            ["don't split", "do not split", "no consolidation", "never consolidate", "keep it separate",
             "ok'd cross-dock", "okayed cross-dock", "approved cross-dock", "don't break the seal",
             "do not break the seal", "seal must stay", "dedicated trailer"]),
        (SignalType.BillingRisk, LtlSurface.BillingWorklistBadge,
            ["won't pay", "will not pay", "disputing", "dispute", "short pay", "short-pay",
             "rate mismatch", "billed wrong", "overbilled", "past terms", "past due", "chargeback"]),
        (SignalType.DelayedLoad, LtlSurface.Exception,
            ["running late", "delayed", "behind schedule", "missed appointment", "missed the appointment",
             "stuck at", "broke down", "breakdown", "will be late", "eta slipped"]),
        (SignalType.MissingDocs, LtlSurface.BillingWorklistBadge,
            ["missing pod", "no pod", "missing bol", "no bol", "missing paperwork", "no paperwork",
             "no signature", "missing rate con", "no rate confirmation", "missing document"]),
        (SignalType.NewLane, LtlSurface.SavedView,
            ["new lane", "we don't run", "we do not run", "start running", "weekly move",
             "regular move", "recurring lane", "wants to move", "pallets a week"]),
        (SignalType.NewSite, LtlSurface.SearchFilter,
            ["new facility", "new site", "new warehouse", "new dc", "new distribution center",
             "new pickup location", "new delivery location", "opening a"]),
        (SignalType.EquipmentNeed, LtlSurface.MatchWarning,
            ["needs a reefer", "reefer required", "needs a flatbed", "flatbed required",
             "liftgate required", "needs liftgate", "temp controlled", "temperature controlled",
             "hazmat", "team drivers required"]),
        (SignalType.ContractSignal, LtlSurface.AuditNote,
            ["contract", "renewal", "renewing", "volume commitment", "dedicated lane", "rfp", "bid",
             "annual agreement", "committed volume"]),
        (SignalType.CompetitiveIntel, LtlSurface.AuditNote,
            ["competitor", "intermediary", "consolidation broker", "another carrier", "incumbent",
             "greensboro", "wilmington"]),
        (SignalType.ServiceIssue, LtlSurface.Exception,
            ["complaint", "unhappy", "damaged", "damage claim", "osd", "os&d", "refused",
             "service failure", "escalate", "escalation"]),
        (SignalType.ContactSuggestion, LtlSurface.AuditNote,
            ["new dispatcher", "new account owner", "new contact", "reach out to", "contact is now",
             "handing off to", "new point of contact"]),
    ];

    public Task<IReadOnlyList<ExtractedSignal>> ExtractAsync(
        string sourceType, string sourceId, string text, CancellationToken ct = default)
    {
        var signals = new List<ExtractedSignal>();
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult<IReadOnlyList<ExtractedSignal>>(signals);

        foreach (var sentence in SplitSentences(text))
        {
            foreach (var (type, surface, keywords) in Rules)
            {
                var matched = keywords.FirstOrDefault(
                    k => sentence.Contains(k, StringComparison.OrdinalIgnoreCase));
                if (matched is null) continue;

                signals.Add(new ExtractedSignal
                {
                    Type = type,
                    // Verbatim excerpt of the source — never fabricated, never numeric.
                    EvidenceQuote = Trim(sentence),
                    SuggestedSurface = surface,
                    Confidence = 1.0,
                    Summary = $"{type} keyword match: \"{matched}\"",
                });
            }
        }

        return Task.FromResult<IReadOnlyList<ExtractedSignal>>(signals);
    }

    // Naive sentence split on ., !, ?, and newlines — enough to give a legible evidence span while
    // staying fully deterministic. Empty fragments are dropped.
    private static IEnumerable<string> SplitSentences(string text) =>
        text.Split(['.', '!', '?', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0);

    private static string Trim(string sentence)
    {
        const int maxLen = 300;
        var s = sentence.Trim();
        return s.Length <= maxLen ? s : s[..maxLen].TrimEnd() + "…";
    }
}
