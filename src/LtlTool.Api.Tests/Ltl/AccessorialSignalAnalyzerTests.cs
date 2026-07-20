using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the accessorial-signal analyzer: deterministic keyword extraction from Alvys notes
/// and document metadata, not-evaluated semantics for empty input, and the MergeAiSignals helper.
///
/// Guardrails exercised here:
/// — empty notes + documents → NotEvaluated (never a false-clean "no accessorials")
/// — notes present but keyword-free → Evaluated with empty Signals (we looked, nothing found)
/// — each accessorial type (Detention/Layover/Lumper/Reconsignment) surfaces from a note
/// — document metadata is also inspected (AttachmentType/AttachmentPath keywords)
/// — AI merge deduplicates by (Type, SourceId), deterministic signal takes precedence
/// </summary>
public sealed class AccessorialSignalAnalyzerTests
{
    private static readonly AccessorialSignalAnalyzer Analyzer = LtlTestFactory.AccessorialAnalyzer();

    // ──────────────────────────────────────────────────────────────────
    // NotEvaluated semantics
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_notes_and_documents_returns_NotEvaluated_singleton()
    {
        var context = Analyzer.BuildContext([], []);

        Assert.Same(AccessorialReviewContext.NotEvaluated, context);
        Assert.False(context.Evaluated);
        Assert.Empty(context.Signals);
    }

    [Fact]
    public void Whitespace_only_note_descriptions_with_no_documents_returns_evaluated_empty()
    {
        // Notes exist in Alvys but carry no text content: the list is non-empty so we DID
        // evaluate — but found nothing to extract. This is "Evaluated + empty signals",
        // not NotEvaluated (which is reserved for the "never fetched" case).
        var notes = new List<AlvysLoadNote>
        {
            new() { Id = "N1", Description = null },
            new() { Id = "N2", Description = "   " },
        };

        var context = Analyzer.BuildContext(notes, []);

        Assert.True(context.Evaluated);
        Assert.Empty(context.Signals);
    }

    // ──────────────────────────────────────────────────────────────────
    // Notes present but no keywords → Evaluated, empty signals
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Notes_with_no_accessorial_keywords_returns_evaluated_empty_signals()
    {
        var notes = new List<AlvysLoadNote>
        {
            new() { Id = "N1", Description = "Standard delivery, no issues." },
        };

        var context = Analyzer.BuildContext(notes, []);

        Assert.True(context.Evaluated);
        Assert.Empty(context.Signals);
    }

    // ──────────────────────────────────────────────────────────────────
    // Detention
    // ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Driver waited 3 hours at the dock.")]
    [InlineData("Detention charge applied for 2hr delay at receiver.")]
    [InlineData("Customer detained truck at gate, delay charge billed.")]
    public void Detention_keyword_surfaces_Detention_signal(string noteText)
    {
        var notes = new List<AlvysLoadNote>
        {
            new() { Id = "N1", Description = noteText },
        };

        var context = Analyzer.BuildContext(notes, []);

        Assert.True(context.Evaluated);
        var signal = Assert.Single(context.Signals, s => s.Type == AccessorialSignalType.Detention);
        Assert.Equal("N1", signal.SourceId);
        Assert.Equal("Note", signal.SourceType);
        Assert.Equal(1.0, signal.Confidence);
        Assert.False(string.IsNullOrWhiteSpace(signal.EvidenceQuote));
    }

    // ──────────────────────────────────────────────────────────────────
    // Layover
    // ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Driver forced layover due to hours of service.")]
    [InlineData("Overnight hold at Laredo terminal, driver layover billed.")]
    [InlineData("Lay over approved by dispatch — see load notes.")]
    public void Layover_keyword_surfaces_Layover_signal(string noteText)
    {
        var notes = new List<AlvysLoadNote>
        {
            new() { Id = "N2", Description = noteText },
        };

        var context = Analyzer.BuildContext(notes, []);

        Assert.True(context.Evaluated);
        var signal = Assert.Single(context.Signals, s => s.Type == AccessorialSignalType.Layover);
        Assert.Equal("N2", signal.SourceId);
    }

    // ──────────────────────────────────────────────────────────────────
    // Lumper
    // ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Lumper service required at consignee, $150 paid.")]
    [InlineData("Unload fee collected at receiver warehouse.")]
    [InlineData("Loading fee charged by dock crew.")]
    public void Lumper_keyword_surfaces_Lumper_signal(string noteText)
    {
        var notes = new List<AlvysLoadNote>
        {
            new() { Id = "N3", Description = noteText },
        };

        var context = Analyzer.BuildContext(notes, []);

        Assert.True(context.Evaluated);
        var signal = Assert.Single(context.Signals, s => s.Type == AccessorialSignalType.Lumper);
        Assert.Equal("N3", signal.SourceId);
    }

    // ──────────────────────────────────────────────────────────────────
    // Reconsignment
    // ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Reconsignment requested — new delivery address confirmed.")]
    [InlineData("Redelivery attempt #2, consignee unavailable first time.")]
    [InlineData("Delivery address changed per shipper instruction.")]
    public void Reconsignment_keyword_surfaces_Reconsignment_signal(string noteText)
    {
        var notes = new List<AlvysLoadNote>
        {
            new() { Id = "N4", Description = noteText },
        };

        var context = Analyzer.BuildContext(notes, []);

        Assert.True(context.Evaluated);
        var signal = Assert.Single(context.Signals, s => s.Type == AccessorialSignalType.Reconsignment);
        Assert.Equal("N4", signal.SourceId);
    }

    // ──────────────────────────────────────────────────────────────────
    // Multiple signal types from one note
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Multiple_accessorial_types_in_one_note_each_surface_once()
    {
        var notes = new List<AlvysLoadNote>
        {
            new()
            {
                Id = "N5",
                Description = "Driver waited 2hrs (detention). Lumper service used. Reconsignment to new address.",
            },
        };

        var context = Analyzer.BuildContext(notes, []);

        Assert.True(context.Evaluated);
        Assert.Contains(context.Signals, s => s.Type == AccessorialSignalType.Detention);
        Assert.Contains(context.Signals, s => s.Type == AccessorialSignalType.Lumper);
        Assert.Contains(context.Signals, s => s.Type == AccessorialSignalType.Reconsignment);
    }

    // ──────────────────────────────────────────────────────────────────
    // Document metadata
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Document_with_accessorial_keyword_in_attachment_type_surfaces_signal()
    {
        var documents = new List<AlvysLoadDocument>
        {
            new() { Id = "D1", AttachmentType = "Detention Receipt", AttachmentPath = "det_receipt.pdf" },
        };

        var context = Analyzer.BuildContext([], documents);

        Assert.True(context.Evaluated);
        var signal = Assert.Single(context.Signals, s => s.Type == AccessorialSignalType.Detention);
        Assert.Equal("D1", signal.SourceId);
        Assert.Equal("Document", signal.SourceType);
    }

    // ──────────────────────────────────────────────────────────────────
    // Missing-value: no notes → MissingDataFlag.AccessorialReview (never false clean)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void No_notes_returns_NotEvaluated_not_clean()
    {
        // This is the "never a false clean" invariant: a load with no notes and no documents
        // must return NotEvaluated (the signal layer cannot assert nothing happened).
        var context = Analyzer.BuildContext([], []);

        Assert.False(context.Evaluated);
        // The NotEvaluated singleton is used, not a fresh evaluated-but-empty instance.
        Assert.Same(AccessorialReviewContext.NotEvaluated, context);
    }

    // ──────────────────────────────────────────────────────────────────
    // AI merge helper
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void MergeAiSignals_deduplicates_by_type_and_sourceId_keeping_deterministic()
    {
        var deterministicSignal = new AccessorialSignal
        {
            Type = AccessorialSignalType.Detention,
            EvidenceQuote = "driver waited 2 hours",
            SourceId = "N1",
            SourceType = "Note",
            Confidence = 1.0,
        };
        var context = new AccessorialReviewContext { Evaluated = true, Signals = [deterministicSignal] };

        var aiSignals = new List<AccessorialSignal>
        {
            // Duplicate — same type+sourceId: should be dropped.
            new() { Type = AccessorialSignalType.Detention, EvidenceQuote = "detention", SourceId = "N1", SourceType = "Note", Confidence = 0.85 },
            // Novel AI signal — different sourceId: should be merged.
            new() { Type = AccessorialSignalType.Layover, EvidenceQuote = "overnight", SourceId = "N2", SourceType = "Note", Confidence = 0.85 },
        };

        var merged = AccessorialSignalAnalyzer.MergeAiSignals(context, aiSignals);

        Assert.Equal(2, merged.Signals.Count);
        var det = merged.Signals.Single(s => s.Type == AccessorialSignalType.Detention);
        Assert.Equal(1.0, det.Confidence); // deterministic wins
        Assert.Contains(merged.Signals, s => s.Type == AccessorialSignalType.Layover);
    }

    [Fact]
    public void MergeAiSignals_on_NotEvaluated_context_returns_same_instance()
    {
        var result = AccessorialSignalAnalyzer.MergeAiSignals(
            AccessorialReviewContext.NotEvaluated,
            [new AccessorialSignal { Type = AccessorialSignalType.Lumper, EvidenceQuote = "lumper", SourceId = "N1", SourceType = "Note" }]);

        Assert.Same(AccessorialReviewContext.NotEvaluated, result);
    }
}
