using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl.Bol;
using LtlTool.Api.Features.Ltl.Dock;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Bol;

/// <summary>
/// The fail-closed read orchestrator. It fetches the document read-only, extracts text, runs the
/// pluggable field extractor, verifies every suggestion carries a verbatim quote, and persists the
/// batch atomically. Any failure along the way records NOTHING — no partial suggestions.
/// </summary>
public sealed class BolReadServiceTests
{
    private const string LoadNumber = "L-100234";
    private const string DocumentId = "doc-1";

    // --- test doubles --------------------------------------------------------

    private sealed class InMemoryStore : IBolSuggestionStore
    {
        public List<BolFieldSuggestionRecord> Records { get; } = [];
        public void AddBatch(IReadOnlyList<BolFieldSuggestionRecord> records) => Records.AddRange(records);
        public BolFieldSuggestionRecord? Get(string id) => Records.FirstOrDefault(r => r.Id == id);
        public IReadOnlyList<BolFieldSuggestionRecord> Query(BolSuggestionQuery query) => Records;
        public BolFieldSuggestionRecord? UpdateStatus(
            string id, BolSuggestionStatus status, string decidedBy, DateTimeOffset decidedAt) => Get(id);
    }

    private sealed class ThrowingTextExtractor : IPdfTextExtractor
    {
        public string Name => "throwing";
        public Task<string?> ExtractTextAsync(byte[] content, string? contentType, CancellationToken ct = default)
            => throw new PdfTextExtractionException("malformed");
    }

    private sealed class FixedTextExtractor(string? text) : IPdfTextExtractor
    {
        public string Name => "fixed";
        public Task<string?> ExtractTextAsync(byte[] content, string? contentType, CancellationToken ct = default)
            => Task.FromResult(text);
    }

    private sealed class FabricatingExtractor : IBolFieldExtractor
    {
        public string Name => "fabricating";
        public IReadOnlyList<ExtractedBolField> Extract(string text) =>
        [
            // Evidence quote is NOT present in the source text — must fail the whole read.
            new() { Field = BolField.Weight, Value = "99,999 lbs", EvidenceQuote = "not in the document", Confidence = 0.9 },
        ];
    }

    private sealed class NoEvidenceExtractor : IBolFieldExtractor
    {
        public string Name => "no-evidence";
        public IReadOnlyList<ExtractedBolField> Extract(string text) =>
        [
            new() { Field = BolField.PalletCount, Value = "12", EvidenceQuote = "   ", Confidence = 0.9 },
        ];
    }

    // --- helpers -------------------------------------------------------------

    private static FakeAlvysClient ClientWithPdf(string text)
    {
        var pdf = new SimplePdfDocument().Line(text).Build();
        return new FakeAlvysClient
        {
            DownloadableDocuments =
            {
                [DocumentId] = new AlvysDocumentContent(DocumentId, pdf, "application/pdf", "BOL.pdf"),
            },
        };
    }

    private static BolReadService Service(
        IAlvysClient client,
        IPdfTextExtractor? textExtractor = null,
        IBolFieldExtractor? fieldExtractor = null,
        IBolSuggestionStore? store = null)
        => new(
            client,
            textExtractor ?? new BuiltInPdfTextExtractor(),
            fieldExtractor ?? new RegexBolFieldExtractor(),
            store ?? new InMemoryStore(),
            LtlTestFactory.Clock(),
            NullLogger<BolReadService>.Instance);

    // --- happy path ----------------------------------------------------------

    [Fact]
    public async Task Reads_a_bol_and_persists_evidence_backed_suggestions()
    {
        var store = new InMemoryStore();
        var service = Service(ClientWithPdf("Pallet count: 12"), store: store);

        var response = await service.ReadAsync(LoadNumber, DocumentId, "dispatch@valuetruck.com", default);

        Assert.Equal(1, response.Count);
        var record = Assert.Single(store.Records);
        Assert.Equal(BolField.PalletCount.ToString(), record.Field);
        Assert.Equal("12", record.Value);
        Assert.Equal(LoadNumber, record.LoadNumber);
        Assert.Equal(DocumentId, record.DocumentId);
        Assert.Equal("BOL.pdf", record.DocumentName);
        Assert.Equal(BolSuggestionStatus.Pending.ToString(), record.Status);
        Assert.Equal("dispatch@valuetruck.com", record.CreatedBy);
        Assert.False(string.IsNullOrWhiteSpace(record.EvidenceQuote));
    }

    // --- fail-closed guardrails ---------------------------------------------

    [Theory]
    [InlineData("", DocumentId)]
    [InlineData(LoadNumber, "")]
    public async Task Blank_ids_fail_closed(string loadNumber, string documentId)
    {
        var service = Service(ClientWithPdf("Pallet count: 12"));

        await Assert.ThrowsAsync<BolReadException>(
            () => service.ReadAsync(loadNumber, documentId, "u", default));
    }

    [Fact]
    public async Task Missing_document_fails_closed_and_persists_nothing()
    {
        var store = new InMemoryStore();
        // Empty DownloadableDocuments → the fetch returns null.
        var service = Service(new FakeAlvysClient(), store: store);

        await Assert.ThrowsAsync<BolReadException>(
            () => service.ReadAsync(LoadNumber, DocumentId, "u", default));
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task A_malformed_pdf_extraction_error_fails_closed()
    {
        var store = new InMemoryStore();
        var service = Service(ClientWithPdf("x"), textExtractor: new ThrowingTextExtractor(), store: store);

        await Assert.ThrowsAsync<BolReadException>(
            () => service.ReadAsync(LoadNumber, DocumentId, "u", default));
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task An_image_only_scan_with_no_text_layer_fails_closed()
    {
        var store = new InMemoryStore();
        var service = Service(ClientWithPdf("x"), textExtractor: new FixedTextExtractor(null), store: store);

        var ex = await Assert.ThrowsAsync<BolReadException>(
            () => service.ReadAsync(LoadNumber, DocumentId, "u", default));
        Assert.Contains("OCR", ex.Message);
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task A_fabricated_evidence_quote_fails_the_whole_read()
    {
        var store = new InMemoryStore();
        var service = Service(
            ClientWithPdf("Pallet count: 12"),
            textExtractor: new FixedTextExtractor("Pallet count: 12"),
            fieldExtractor: new FabricatingExtractor(),
            store: store);

        await Assert.ThrowsAsync<BolReadException>(
            () => service.ReadAsync(LoadNumber, DocumentId, "u", default));
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task A_suggestion_without_an_evidence_quote_fails_the_whole_read()
    {
        var store = new InMemoryStore();
        var service = Service(
            ClientWithPdf("Pallet count: 12"),
            textExtractor: new FixedTextExtractor("Pallet count: 12"),
            fieldExtractor: new NoEvidenceExtractor(),
            store: store);

        await Assert.ThrowsAsync<BolReadException>(
            () => service.ReadAsync(LoadNumber, DocumentId, "u", default));
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task A_read_with_no_recognized_fields_is_a_valid_empty_outcome()
    {
        var store = new InMemoryStore();
        var service = Service(ClientWithPdf("nothing structured here"), store: store);

        var response = await service.ReadAsync(LoadNumber, DocumentId, "u", default);

        Assert.Equal(0, response.Count);
        Assert.Empty(store.Records);
    }
}
