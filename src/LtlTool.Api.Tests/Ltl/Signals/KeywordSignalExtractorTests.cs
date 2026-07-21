using LtlTool.Api.Features.Ltl.Signals;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Signals;

/// <summary>
/// Verifies the deterministic keyword extractor: it classifies text into typed signals, every signal
/// carries a verbatim excerpt of the source as its evidence quote, and it never asserts a numeric
/// operational value. Fully offline and reproducible — no LLM, no network.
/// </summary>
public sealed class KeywordSignalExtractorTests
{
    private readonly KeywordSignalExtractor _extractor = new();

    private async Task<IReadOnlyList<ExtractedSignal>> Extract(string text) =>
        await _extractor.ExtractAsync("note", "src-1", text);

    [Fact]
    public void Name_is_honest_and_deterministic()
        => Assert.Equal("deterministic-keyword", _extractor.Name);

    [Fact]
    public async Task Empty_text_yields_no_signals()
    {
        Assert.Empty(await Extract(""));
        Assert.Empty(await Extract("   "));
    }

    [Fact]
    public async Task Detention_mention_produces_accessorial_evidence_signal()
    {
        var signals = await Extract("Driver was detained 3 hours at the dock waiting on a lumper.");

        var accessorial = Assert.Single(signals, s => s.Type == SignalType.AccessorialEvidence);
        Assert.Equal(LtlSurface.BillingWorklistBadge, accessorial.SuggestedSurface);
        Assert.False(string.IsNullOrWhiteSpace(accessorial.EvidenceQuote));
    }

    [Fact]
    public async Task Evidence_quote_is_a_verbatim_excerpt_of_the_source()
    {
        const string text = "The customer is disputing the rate. They say they won't pay the detention.";

        var signals = await Extract(text);

        Assert.NotEmpty(signals);
        foreach (var signal in signals)
        {
            // Every quote must be found verbatim (whitespace-collapsed) inside the source text.
            var collapsedSource = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var collapsedQuote = string.Join(' ', signal.EvidenceQuote.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains(collapsedQuote, collapsedSource, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Confidence_is_an_extraction_score_never_an_operational_number()
    {
        var signals = await Extract("This load is running late and will be late to delivery.");

        Assert.NotEmpty(signals);
        // Confidence is bounded to [0,1]; it is not weight/revenue/miles.
        Assert.All(signals, s => Assert.InRange(s.Confidence, 0.0, 1.0));
    }

    [Fact]
    public async Task Multiple_signal_types_are_detected_across_sentences()
    {
        const string text =
            "Customer is disputing the invoice. " +
            "We could consolidate these two partials into one linehaul. " +
            "Missing POD on the second load.";

        var signals = await Extract(text);

        Assert.Contains(signals, s => s.Type == SignalType.BillingRisk);
        Assert.Contains(signals, s => s.Type == SignalType.ConsolidationOpportunity);
        Assert.Contains(signals, s => s.Type == SignalType.MissingDocs);
    }

    [Fact]
    public async Task Same_type_does_not_flood_within_one_sentence()
    {
        // A sentence with two accessorial keywords still yields only one accessorial signal.
        var signals = await Extract("Detention and layover both apply here.");

        Assert.Single(signals, s => s.Type == SignalType.AccessorialEvidence);
    }

    [Fact]
    public async Task Extraction_is_reproducible()
    {
        const string text = "Customer won't pay; they are disputing the rate.";

        var first = await Extract(text);
        var second = await Extract(text);

        Assert.Equal(
            first.Select(s => (s.Type, s.EvidenceQuote)).ToArray(),
            second.Select(s => (s.Type, s.EvidenceQuote)).ToArray());
    }
}
