using System.Text.RegularExpressions;

namespace LtlTool.Api.Features.Ltl.Bol;

/// <summary>
/// Deterministic regex/keyword <see cref="IBolFieldExtractor"/> — the always-registered default. It
/// scans the BOL text line-by-line against a fixed, ordered rule set and, for each field it finds,
/// captures the matched line verbatim as the evidence quote. No network call, no LLM, no numeric
/// assertion beyond echoing what the document literally says — the output is fully reproducible from
/// the input, which keeps every suggestion explainable to the human reviewer.
///
/// <para>At most one suggestion per <see cref="BolField"/> (the first, highest-priority match wins) so
/// a busy BOL doesn't flood the review queue with the same field. Missing fields are simply absent —
/// never invented, never defaulted to 0 / false / "good".</para>
/// </summary>
public sealed partial class RegexBolFieldExtractor : IBolFieldExtractor
{
    public string Name => "deterministic-regex";

    private sealed record Rule(BolField Field, Regex Pattern, double Confidence, Func<Match, string?> Value);

    private static readonly Rule[] Rules =
    [
        // Pallet count — requires an explicit pallet/skid word so it never grabs a weight/piece number.
        new(BolField.PalletCount, PalletRegex(), 0.9,
            m => m.Groups["v"].Value),

        // Piece / handling-unit count.
        new(BolField.PieceCount, PieceRegex(), 0.85,
            m => m.Groups["v"].Value),

        // Weight — keep the value AND its unit verbatim; never convert.
        new(BolField.Weight, WeightRegex(), 0.9,
            m => $"{m.Groups["v"].Value} {m.Groups["u"].Value}".Trim()),

        // NMFC freight class (integer or decimal like 92.5).
        new(BolField.FreightClass, ClassRegex(), 0.9,
            m => m.Groups["v"].Value),

        // Commodity / description of goods — labeled value, trimmed and length-bounded.
        new(BolField.CommodityDescription, CommodityRegex(), 0.7,
            m =>
            {
                var v = m.Groups["v"].Value.Trim();
                return v.Length is > 1 and <= 120 ? v : null;
            }),

        // Hazmat — presence of a hazmat marker or a UN number. Value is a plain "Yes".
        new(BolField.HazmatFlag, HazmatRegex(), 0.8, _ => "Yes"),
    ];

    public IReadOnlyList<ExtractedBolField> Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var found = new Dictionary<BolField, ExtractedBolField>();

        foreach (var line in SplitLines(text))
        {
            foreach (var rule in Rules)
            {
                if (found.ContainsKey(rule.Field)) continue;

                var match = rule.Pattern.Match(line);
                if (!match.Success) continue;

                var value = rule.Value(match);
                if (string.IsNullOrWhiteSpace(value)) continue;

                found[rule.Field] = new ExtractedBolField
                {
                    Field = rule.Field,
                    Value = value.Trim(),
                    // Verbatim excerpt of the source line — never fabricated.
                    EvidenceQuote = Clip(line),
                    Confidence = rule.Confidence,
                };
            }
        }

        // Stable order by enum for a predictable review list.
        return found.Values.OrderBy(f => f.Field).ToArray();
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0);

    private static string Clip(string line)
    {
        const int maxLen = 240;
        var s = line.Trim();
        return s.Length <= maxLen ? s : s[..maxLen].TrimEnd() + "…";
    }

    [GeneratedRegex(@"(?:pallet|skid|plt)\w*\D{0,12}?(?<v>\d{1,4})|(?<v>\d{1,4})\s*(?:pallet|skid|plt)s?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PalletRegex();

    [GeneratedRegex(@"(?:pieces?|pcs|cartons?|handling\s+units?|\bhu\b|\bunits?\b)\D{0,12}?(?<v>\d{1,5})|(?<v>\d{1,5})\s*(?:pieces?|pcs|cartons?|handling\s+units?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PieceRegex();

    [GeneratedRegex(@"(?<v>\d{1,3}(?:,\d{3})*(?:\.\d+)?)\s*(?<u>lbs?|pounds?|kg|kgs|kilograms?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeightRegex();

    [GeneratedRegex(@"(?:freight\s+)?(?:nmfc\s+)?class\b\D{0,6}?(?<v>\d{2,3}(?:\.\d)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClassRegex();

    [GeneratedRegex(@"(?:commodity|description\s+of\s+goods|description|goods)\s*[:\-]\s*(?<v>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommodityRegex();

    [GeneratedRegex(@"hazmat|hazardous\s+materials?|\bun\s?\d{4}\b|dangerous\s+goods",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HazmatRegex();
}
