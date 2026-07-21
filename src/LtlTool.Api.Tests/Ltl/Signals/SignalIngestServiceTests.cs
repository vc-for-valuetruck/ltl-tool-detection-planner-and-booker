using LtlTool.Api.Features.Ltl.Signals;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Signals;

/// <summary>
/// Exercises the fail-closed contract of <see cref="SignalIngestService"/> (anti-failure map 3g / 3o
/// boundary): a request either records every produced signal or records nothing — never a partial
/// write, never a silent drop. Covers request validation, an extractor that throws, an extractor that
/// emits an un-evidenced or fabricated quote, and the deterministic happy path.
/// </summary>
public sealed class SignalIngestServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    private sealed class CapturingStore : ISignalStore
    {
        public readonly List<SignalRecord> Records = [];
        public void AddBatch(IReadOnlyList<SignalRecord> records) => Records.AddRange(records);
        public SignalRecord? Get(string id) => Records.FirstOrDefault(r => r.Id == id);
        public IReadOnlyList<SignalRecord> Query(SignalQuery query) => Records;
        public SignalRecord? UpdateStatus(string id, SignalStatus status, string decidedBy, DateTimeOffset decidedAt)
            => null;
    }

    private sealed class ThrowingExtractor : ISignalExtractor
    {
        public string Name => "throwing";
        public Task<IReadOnlyList<ExtractedSignal>> ExtractAsync(
            string sourceType, string sourceId, string text, CancellationToken ct = default)
            => throw new SignalExtractorException("model unavailable");
    }

    private sealed class StubExtractor(IReadOnlyList<ExtractedSignal> signals) : ISignalExtractor
    {
        public string Name => "stub";
        public Task<IReadOnlyList<ExtractedSignal>> ExtractAsync(
            string sourceType, string sourceId, string text, CancellationToken ct = default)
            => Task.FromResult(signals);
    }

    private static SignalIngestService Service(ISignalExtractor extractor, ISignalStore store) =>
        new(extractor, store, new FixedTimeProvider(Now), NullLogger<SignalIngestService>.Instance);

    private static SignalIngestRequest Request(string? text = "Driver was detained 3 hours.", string? sourceType = "note")
        => new() { SourceType = sourceType, SourceId = "src-1", Text = text };

    [Fact]
    public async Task Missing_source_type_fails_closed()
    {
        var store = new CapturingStore();
        var svc = Service(new KeywordSignalExtractor(), store);

        await Assert.ThrowsAsync<SignalIngestException>(
            () => svc.IngestAsync(Request(sourceType: null), "u@vt.com", default));
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task Unknown_source_type_fails_closed()
    {
        var store = new CapturingStore();
        var svc = Service(new KeywordSignalExtractor(), store);

        await Assert.ThrowsAsync<SignalIngestException>(
            () => svc.IngestAsync(Request(sourceType: "carrier-pigeon"), "u@vt.com", default));
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task Blank_text_fails_closed()
    {
        var store = new CapturingStore();
        var svc = Service(new KeywordSignalExtractor(), store);

        await Assert.ThrowsAsync<SignalIngestException>(
            () => svc.IngestAsync(Request(text: "   "), "u@vt.com", default));
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task Extractor_throw_fails_closed_and_records_nothing()
    {
        var store = new CapturingStore();
        var svc = Service(new ThrowingExtractor(), store);

        var ex = await Assert.ThrowsAsync<SignalIngestException>(
            () => svc.IngestAsync(Request(), "u@vt.com", default));
        Assert.Contains("Nothing was recorded", ex.Message);
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task Signal_without_evidence_quote_fails_the_whole_batch()
    {
        var store = new CapturingStore();
        // One good signal + one un-evidenced signal → the whole request must fail, nothing persisted.
        var extractor = new StubExtractor(
        [
            new ExtractedSignal
            {
                Type = SignalType.AccessorialEvidence,
                EvidenceQuote = "Driver was detained 3 hours.",
                SuggestedSurface = LtlSurface.BillingWorklistBadge,
            },
            new ExtractedSignal
            {
                Type = SignalType.BillingRisk,
                EvidenceQuote = "   ",
                SuggestedSurface = LtlSurface.BillingWorklistBadge,
            },
        ]);
        var svc = Service(extractor, store);

        await Assert.ThrowsAsync<SignalIngestException>(
            () => svc.IngestAsync(Request(), "u@vt.com", default));
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task Fabricated_quote_not_present_in_source_fails_closed()
    {
        var store = new CapturingStore();
        var extractor = new StubExtractor(
        [
            new ExtractedSignal
            {
                Type = SignalType.BillingRisk,
                EvidenceQuote = "Customer promised to pay double next week.",
                SuggestedSurface = LtlSurface.BillingWorklistBadge,
            },
        ]);
        var svc = Service(extractor, store);

        var ex = await Assert.ThrowsAsync<SignalIngestException>(
            () => svc.IngestAsync(Request(text: "Driver was detained 3 hours."), "u@vt.com", default));
        Assert.Contains("not present in the", ex.Message);
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task Happy_path_records_typed_pending_signals_with_evidence()
    {
        var store = new CapturingStore();
        var svc = Service(new KeywordSignalExtractor(), store);

        var result = await svc.IngestAsync(
            Request(text: "Driver was detained 3 hours waiting on a lumper."), "dispatcher@vt.com", default);

        Assert.True(result.Count >= 1);
        Assert.All(result.Signals, s =>
        {
            Assert.Equal(SignalStatus.Pending, s.Status);
            Assert.False(string.IsNullOrWhiteSpace(s.EvidenceQuote));
            Assert.Equal("dispatcher@vt.com", s.IngestedBy);
            Assert.Equal(Now, s.CreatedAt);
        });
        Assert.Equal(result.Count, store.Records.Count);
        Assert.Contains(store.Records, r => r.SignalType == SignalType.AccessorialEvidence.ToString());
    }

    [Fact]
    public async Task Load_number_is_carried_through_when_supplied()
    {
        var store = new CapturingStore();
        var svc = Service(new KeywordSignalExtractor(), store);

        var request = Request(text: "Load is running late.");
        request.LoadNumber = "L-100234";

        await svc.IngestAsync(request, "u@vt.com", default);

        Assert.All(store.Records, r => Assert.Equal("L-100234", r.LoadNumber));
    }

    [Fact]
    public async Task No_signals_found_is_a_valid_empty_outcome()
    {
        var store = new CapturingStore();
        var svc = Service(new KeywordSignalExtractor(), store);

        var result = await svc.IngestAsync(
            Request(text: "Thanks for the quick turnaround yesterday."), "u@vt.com", default);

        Assert.Equal(0, result.Count);
        Assert.Empty(store.Records);
    }
}
