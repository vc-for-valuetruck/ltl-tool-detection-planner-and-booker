using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the deterministic accessorial-review analyzer (Phase 3.5): stop-timing detention /
/// layover / weekend / reconsignment candidates each citing a real Alvys stop id, the folding of
/// note/document keyword signals, and the two honesty invariants —
/// <see cref="AccessorialReviewResult.NotEvaluated"/> when there is nothing to inspect, and the
/// <see cref="AccessorialCandidateStatus.CannotEvaluate"/> detention fallback when a customer's
/// free-time term is not configured (never an assumed number).
/// </summary>
public sealed class AccessorialReviewAnalyzerTests
{
    private static AlvysLoad LoadFor(string? customerId = null, string? customerName = null) =>
        new() { CustomerId = customerId, CustomerName = customerName };

    private static LtlOptions OptionsWithFreeTime(string key, int minutes)
    {
        var opts = new LtlOptions();
        opts.AccessorialReview.CustomerFreeTimeMinutes[key] = minutes;
        return opts;
    }

    // ──────────────────────────────────────────────────────────────────
    // NotEvaluated semantics
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void No_stops_and_not_evaluated_keywords_returns_NotEvaluated_singleton()
    {
        var analyzer = LtlTestFactory.AccessorialReview();

        var result = analyzer.Analyze(LoadFor(), [], AccessorialReviewContext.NotEvaluated);

        Assert.Same(AccessorialReviewResult.NotEvaluated, result);
        Assert.False(result.Evaluated);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Keywords_evaluated_with_no_stops_still_evaluates()
    {
        var analyzer = LtlTestFactory.AccessorialReview();
        var keywordContext = new AccessorialReviewContext { Evaluated = true, Signals = [] };

        var result = analyzer.Analyze(LoadFor(), [], keywordContext);

        Assert.True(result.Evaluated);
        Assert.Empty(result.Candidates);
    }

    // ──────────────────────────────────────────────────────────────────
    // Detention
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Dwell_over_configured_free_time_surfaces_Likely_detention()
    {
        // 6h dwell, 3h (180m) free time → 3h over → Likely detention, mirrors the ROADMAP UAT bar.
        var analyzer = LtlTestFactory.AccessorialReview(OptionsWithFreeTime("CUST-1", 180));
        var stops = new List<AlvysTripStop>
        {
            new()
            {
                Id = "S2",
                StopType = "Delivery",
                ArrivedAt = new DateTimeOffset(2026, 6, 20, 10, 0, 0, TimeSpan.Zero),
                DepartedAt = new DateTimeOffset(2026, 6, 20, 16, 0, 0, TimeSpan.Zero),
            },
        };

        var result = analyzer.Analyze(LoadFor(customerId: "CUST-1"), stops, AccessorialReviewContext.NotEvaluated);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(AccessorialSignalType.Detention, candidate.Type);
        Assert.Equal(AccessorialCandidateStatus.Likely, candidate.Status);
        Assert.Equal("S2", candidate.SourceId);
        Assert.Equal("Stop", candidate.SourceType);
        Assert.Contains("over free time", candidate.Reason);
        Assert.Contains("free time = 180m", candidate.Evidence);
        Assert.True(result.HasLikelyCandidate);
    }

    [Fact]
    public void Free_time_resolves_by_customer_name_when_id_absent()
    {
        var analyzer = LtlTestFactory.AccessorialReview(OptionsWithFreeTime("Acme Freight", 60));
        var stops = new List<AlvysTripStop>
        {
            new()
            {
                Id = "S1",
                ArrivedAt = new DateTimeOffset(2026, 6, 20, 8, 0, 0, TimeSpan.Zero),
                DepartedAt = new DateTimeOffset(2026, 6, 20, 11, 0, 0, TimeSpan.Zero),
            },
        };

        var result = analyzer.Analyze(LoadFor(customerName: "Acme Freight"), stops, AccessorialReviewContext.NotEvaluated);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(AccessorialSignalType.Detention, candidate.Type);
        Assert.Equal(AccessorialCandidateStatus.Likely, candidate.Status);
    }

    [Fact]
    public void Closed_dwell_without_configured_free_time_yields_CannotEvaluate()
    {
        // A measurable dwell exists but no free-time term is configured for the customer:
        // the analyzer must flag "can't evaluate", never assume a number.
        var analyzer = LtlTestFactory.AccessorialReview();
        var stops = new List<AlvysTripStop>
        {
            new()
            {
                Id = "S3",
                ArrivedAt = new DateTimeOffset(2026, 6, 20, 9, 0, 0, TimeSpan.Zero),
                DepartedAt = new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero),
            },
        };

        var result = analyzer.Analyze(LoadFor(customerId: "UNCONFIGURED"), stops, AccessorialReviewContext.NotEvaluated);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(AccessorialSignalType.Detention, candidate.Type);
        Assert.Equal(AccessorialCandidateStatus.CannotEvaluate, candidate.Status);
        Assert.Contains("free time not configured", candidate.Reason);
        Assert.False(result.HasLikelyCandidate);
    }

    [Fact]
    public void Dwell_within_free_time_produces_no_detention_candidate()
    {
        var analyzer = LtlTestFactory.AccessorialReview(OptionsWithFreeTime("CUST-1", 240));
        var stops = new List<AlvysTripStop>
        {
            new()
            {
                Id = "S1",
                ArrivedAt = new DateTimeOffset(2026, 6, 20, 10, 0, 0, TimeSpan.Zero),
                DepartedAt = new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero),
            },
        };

        var result = analyzer.Analyze(LoadFor(customerId: "CUST-1"), stops, AccessorialReviewContext.NotEvaluated);

        Assert.True(result.Evaluated);
        Assert.Empty(result.Candidates);
    }

    // ──────────────────────────────────────────────────────────────────
    // Layover
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Dwell_over_24h_surfaces_Likely_layover_not_detention()
    {
        // A 30h dwell is a layover even with free time configured — layover takes precedence.
        var analyzer = LtlTestFactory.AccessorialReview(OptionsWithFreeTime("CUST-1", 180));
        var stops = new List<AlvysTripStop>
        {
            new()
            {
                Id = "S5",
                ArrivedAt = new DateTimeOffset(2026, 6, 20, 6, 0, 0, TimeSpan.Zero),
                DepartedAt = new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero),
            },
        };

        var result = analyzer.Analyze(LoadFor(customerId: "CUST-1"), stops, AccessorialReviewContext.NotEvaluated);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(AccessorialSignalType.Layover, candidate.Type);
        Assert.Equal(AccessorialCandidateStatus.Likely, candidate.Status);
        Assert.Equal("S5", candidate.SourceId);
    }

    // ──────────────────────────────────────────────────────────────────
    // Reconsignment
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Stop_reference_flagged_reconsign_surfaces_reconsignment()
    {
        var analyzer = LtlTestFactory.AccessorialReview();
        var stops = new List<AlvysTripStop>
        {
            new()
            {
                Id = "S7",
                References = [new AlvysReference { Name = "Reconsignment", Value = "New address confirmed" }],
            },
        };

        var result = analyzer.Analyze(LoadFor(), stops, AccessorialReviewContext.NotEvaluated);

        var candidate = Assert.Single(result.Candidates, c => c.Type == AccessorialSignalType.Reconsignment);
        Assert.Equal(AccessorialCandidateStatus.Likely, candidate.Status);
        Assert.Equal("S7", candidate.SourceId);
        Assert.Contains("Reconsignment", candidate.Evidence);
    }

    // ──────────────────────────────────────────────────────────────────
    // Weekend / after-hours scheduling
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Saturday_scheduled_stop_surfaces_weekend_candidate()
    {
        // 2026-06-20 is a Saturday.
        var analyzer = LtlTestFactory.AccessorialReview();
        var stops = new List<AlvysTripStop>
        {
            new()
            {
                Id = "S9",
                StopType = "Delivery",
                Appointment = new DateTimeOffset(2026, 6, 20, 9, 0, 0, TimeSpan.Zero),
            },
        };

        var result = analyzer.Analyze(LoadFor(), stops, AccessorialReviewContext.NotEvaluated);

        var candidate = Assert.Single(result.Candidates, c => c.Type == AccessorialSignalType.WeekendDelivery);
        Assert.Equal(AccessorialCandidateStatus.Likely, candidate.Status);
        Assert.Equal("S9", candidate.SourceId);
    }

    [Fact]
    public void Weekday_scheduled_stop_produces_no_weekend_candidate()
    {
        // 2026-06-22 is a Monday.
        var analyzer = LtlTestFactory.AccessorialReview();
        var stops = new List<AlvysTripStop>
        {
            new()
            {
                Id = "S10",
                Appointment = new DateTimeOffset(2026, 6, 22, 9, 0, 0, TimeSpan.Zero),
            },
        };

        var result = analyzer.Analyze(LoadFor(), stops, AccessorialReviewContext.NotEvaluated);

        Assert.DoesNotContain(result.Candidates, c => c.Type == AccessorialSignalType.WeekendDelivery);
    }

    // ──────────────────────────────────────────────────────────────────
    // Keyword folding
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Note_keyword_signal_is_folded_in_with_its_cited_source()
    {
        var analyzer = LtlTestFactory.AccessorialReview();
        var keywordContext = new AccessorialReviewContext
        {
            Evaluated = true,
            Signals =
            [
                new AccessorialSignal
                {
                    Type = AccessorialSignalType.Handling,
                    EvidenceQuote = "hand unload at dock",
                    SourceId = "NC0034",
                    SourceType = "Note",
                    Confidence = 1.0,
                },
            ],
        };

        var result = analyzer.Analyze(LoadFor(), [], keywordContext);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(AccessorialSignalType.Handling, candidate.Type);
        Assert.Equal(AccessorialCandidateStatus.Likely, candidate.Status);
        Assert.Equal("NC0034", candidate.SourceId);
        Assert.Equal("Note", candidate.SourceType);
        Assert.Contains("NC0034", candidate.Evidence);
        Assert.Contains("hand unload at dock", candidate.Evidence);
    }

    [Fact]
    public void Other_type_keyword_signal_is_folded_as_Unknown_not_Likely()
    {
        var analyzer = LtlTestFactory.AccessorialReview();
        var keywordContext = new AccessorialReviewContext
        {
            Evaluated = true,
            Signals =
            [
                new AccessorialSignal
                {
                    Type = AccessorialSignalType.Other,
                    EvidenceQuote = "misc fee",
                    SourceId = "N9",
                    SourceType = "Note",
                    Confidence = 1.0,
                },
            ],
        };

        var result = analyzer.Analyze(LoadFor(), [], keywordContext);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(AccessorialCandidateStatus.Unknown, candidate.Status);
        Assert.False(result.HasLikelyCandidate);
    }

    [Fact]
    public void Stop_timing_and_keyword_evidence_combine_into_one_candidate_list()
    {
        var analyzer = LtlTestFactory.AccessorialReview(OptionsWithFreeTime("CUST-1", 60));
        var stops = new List<AlvysTripStop>
        {
            new()
            {
                Id = "S1",
                ArrivedAt = new DateTimeOffset(2026, 6, 20, 8, 0, 0, TimeSpan.Zero),
                DepartedAt = new DateTimeOffset(2026, 6, 20, 11, 0, 0, TimeSpan.Zero),
            },
        };
        var keywordContext = new AccessorialReviewContext
        {
            Evaluated = true,
            Signals =
            [
                new AccessorialSignal
                {
                    Type = AccessorialSignalType.Lumper,
                    EvidenceQuote = "lumper paid",
                    SourceId = "N1",
                    SourceType = "Note",
                    Confidence = 1.0,
                },
            ],
        };

        var result = analyzer.Analyze(LoadFor(customerId: "CUST-1"), stops, keywordContext);

        Assert.Contains(result.Candidates, c => c.Type == AccessorialSignalType.Detention && c.SourceType == "Stop");
        Assert.Contains(result.Candidates, c => c.Type == AccessorialSignalType.Lumper && c.SourceType == "Note");
    }
}
