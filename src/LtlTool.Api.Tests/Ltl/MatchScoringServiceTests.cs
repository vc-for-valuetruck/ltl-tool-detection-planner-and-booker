using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the deterministic, explainable match scorer: hard disqualifiers cap the label,
/// unavailable factors (HOS/historical) are reported but excluded from the score denominator,
/// and a clean candidate earns a high label.
/// </summary>
public sealed class MatchScoringServiceTests
{
    private static LtlLoadSummary Load() => new()
    {
        Id = "L1",
        Status = "Open",
        Equipment = ["Dry Van"],
        WeightLbs = 8000m,
        Origin = new LtlPlace { City = "Dallas", State = "TX" },
    };

    private static AlvysDriver GoodDriver() => new()
    {
        Id = "DR1",
        Name = "Sam Driver",
        IsActive = true,
        SubsidiaryId = "SUB-1",
        Address = new AlvysContextAddress { State = "TX" },
        LicenseExpiresAt = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
        MedicalExpiresAt = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private static AlvysTrailerEquipment GoodTrailer() => new()
    {
        Id = "TR1",
        TrailerNum = "900",
        EquipmentType = "Dry Van",
        SubsidiaryId = "SUB-1",
        Capacity = new AlvysTrailerCapacity { Weight = 40000m },
    };

    [Fact]
    public void Clean_candidate_scores_excellent()
    {
        var candidate = new MatchCandidate { Driver = GoodDriver(), Trailer = GoodTrailer() };

        var result = LtlTestFactory.Scorer().Score(Load(), candidate);

        Assert.Equal(MatchLabel.Excellent, result.Label);
        Assert.Equal("Excellent Match", result.LabelText);
        Assert.Empty(result.Disqualifiers);
    }

    [Fact]
    public void Hos_and_historical_are_reported_unavailable_not_invented()
    {
        var result = LtlTestFactory.Scorer().Score(Load(), new MatchCandidate { Driver = GoodDriver(), Trailer = GoodTrailer() });

        Assert.Contains(result.Factors, f => f.Name == "Hours of Service" && f.Status == MatchFactorStatus.Unavailable);
        Assert.Contains(result.Factors, f => f.Name == "Historical performance" && f.Status == MatchFactorStatus.Unavailable);
        // Unavailable factors contribute 0 max points (excluded from denominator).
        Assert.All(result.Factors.Where(f => f.Status == MatchFactorStatus.Unavailable),
            f => Assert.Equal(0, f.MaxPoints));
    }

    [Fact]
    public void Over_capacity_disqualifies_to_not_recommended()
    {
        var trailer = GoodTrailer();
        trailer.Capacity = new AlvysTrailerCapacity { Weight = 5000m }; // load is 8000

        var result = LtlTestFactory.Scorer().Score(Load(), new MatchCandidate { Driver = GoodDriver(), Trailer = trailer });

        Assert.Equal(MatchLabel.NotRecommended, result.Label);
        Assert.Contains(result.Disqualifiers, d => d.Contains("exceeds trailer capacity"));
    }

    [Fact]
    public void Expired_license_disqualifies_driver()
    {
        var driver = GoodDriver();
        driver.LicenseExpiresAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); // past the fixed clock

        var result = LtlTestFactory.Scorer().Score(Load(), new MatchCandidate { Driver = driver, Trailer = GoodTrailer() });

        Assert.Equal(MatchLabel.NotRecommended, result.Label);
        Assert.Contains(result.Disqualifiers, d => d.Contains("License expired"));
    }

    [Fact]
    public void Terminated_driver_is_disqualified()
    {
        var driver = GoodDriver();
        driver.TerminatedAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

        var result = LtlTestFactory.Scorer().Score(Load(), new MatchCandidate { Driver = driver, Trailer = GoodTrailer() });

        Assert.Equal(MatchLabel.NotRecommended, result.Label);
        Assert.Contains(result.Disqualifiers, d => d.Contains("terminated"));
    }

    [Fact]
    public void Wrong_equipment_lowers_the_label()
    {
        var trailer = GoodTrailer();
        trailer.EquipmentType = "Reefer";

        var result = LtlTestFactory.Scorer().Score(Load(), new MatchCandidate { Driver = GoodDriver(), Trailer = trailer });

        Assert.True(result.Score < 85, $"expected non-excellent score, got {result.Score}");
        Assert.Contains(result.Factors, f => f.Name == "Equipment match" && f.Status == MatchFactorStatus.Weak);
    }

    [Fact]
    public void Equipment_availability_is_unavailable_when_not_evaluated()
    {
        var candidate = new MatchCandidate { Driver = GoodDriver(), Trailer = GoodTrailer() };

        // No assessment / NotEvaluated → factor reported unavailable and excluded from denominator.
        var result = LtlTestFactory.Scorer().Score(Load(), candidate, EquipmentEventAssessment.NotEvaluated);

        var factor = Assert.Single(result.Factors, f => f.Name == "Equipment availability");
        Assert.Equal(MatchFactorStatus.Unavailable, factor.Status);
        Assert.Equal(0, factor.MaxPoints);
    }

    [Fact]
    public void Equipment_event_conflict_scores_the_availability_factor_weak()
    {
        var candidate = new MatchCandidate { Driver = GoodDriver(), Trailer = GoodTrailer() };
        var events = new EquipmentEventAssessment { Evaluated = true, Conflicts = ["Truck Repair overlaps the load window."] };

        var result = LtlTestFactory.Scorer().Score(Load(), candidate, events);

        var factor = Assert.Single(result.Factors, f => f.Name == "Equipment availability");
        Assert.Equal(MatchFactorStatus.Weak, factor.Status);
        Assert.True(factor.MaxPoints > 0); // counted in the denominator now that it was evaluated
        Assert.Equal(0, factor.Points);
    }

    [Fact]
    public void Equipment_available_when_evaluated_with_no_conflicts_scores_strong()
    {
        var candidate = new MatchCandidate { Driver = GoodDriver(), Trailer = GoodTrailer() };
        var events = new EquipmentEventAssessment { Evaluated = true, Conflicts = [] };

        var result = LtlTestFactory.Scorer().Score(Load(), candidate, events);

        var factor = Assert.Single(result.Factors, f => f.Name == "Equipment availability");
        Assert.Equal(MatchFactorStatus.Strong, factor.Status);
        Assert.Equal(factor.MaxPoints, factor.Points);
    }

    [Fact]
    public void Window_feasibility_is_unavailable_when_not_evaluated_and_excluded_from_denominator()
    {
        var candidate = new MatchCandidate { Driver = GoodDriver(), Trailer = GoodTrailer() };

        var result = LtlTestFactory.Scorer().Score(
            Load(), candidate, events: null, windowFeasibility: WindowFeasibilityAssessment.NotEvaluated);

        var factor = Assert.Single(result.Factors, f => f.Name == "Window feasibility");
        Assert.Equal(MatchFactorStatus.Unavailable, factor.Status);
        Assert.Equal(0, factor.MaxPoints); // never a penalty
        Assert.Equal(MatchLabel.Excellent, result.Label); // absent feasibility must not cap the label
    }

    [Fact]
    public void Window_feasibility_free_when_no_committed_trip_scores_strong()
    {
        var candidate = new MatchCandidate { Driver = GoodDriver(), Trailer = GoodTrailer() };
        var window = new WindowFeasibilityAssessment
        {
            Evaluated = true,
            PickupAt = new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero),
        };

        var result = LtlTestFactory.Scorer().Score(Load(), candidate, events: null, windowFeasibility: window);

        var factor = Assert.Single(result.Factors, f => f.Name == "Window feasibility");
        Assert.Equal(MatchFactorStatus.Strong, factor.Status);
        Assert.Equal(factor.MaxPoints, factor.Points);
    }

    [Fact]
    public void Infeasible_pickup_window_caps_the_label_at_possible_with_a_stated_reason()
    {
        var candidate = new MatchCandidate { Driver = GoodDriver(), Trailer = GoodTrailer() };
        var window = new WindowFeasibilityAssessment
        {
            Evaluated = true,
            CommittedTripId = "TRIP-1",
            ClearsAt = new DateTimeOffset(2026, 7, 2, 12, 0, 0, TimeSpan.Zero),
            PickupAt = new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero),
            Infeasible = true,
        };

        var result = LtlTestFactory.Scorer().Score(Load(), candidate, events: null, windowFeasibility: window);

        Assert.Equal(MatchLabel.Possible, result.Label);
        var factor = Assert.Single(result.Factors, f => f.Name == "Window feasibility");
        Assert.Equal(MatchFactorStatus.Weak, factor.Status);
        Assert.Contains("not feasible", result.Summary);
        Assert.Contains(result.Warnings, w => w.Contains("Current trip clears"));
    }

    [Fact]
    public void Dispatch_preference_presence_scores_strong_positive()
    {
        var candidate = new MatchCandidate
        {
            Driver = GoodDriver(),
            Trailer = GoodTrailer(),
            Preference = new AlvysDispatchPreference { UpdatedAt = LtlTestFactory.Now },
        };

        var result = LtlTestFactory.Scorer().Score(Load(), candidate);

        var factor = Assert.Single(result.Factors, f => f.Name == "Dispatch preference");
        Assert.Equal(MatchFactorStatus.Strong, factor.Status);
        Assert.Equal(factor.MaxPoints, factor.Points);
    }

    [Fact]
    public void Dispatch_preference_absence_is_unavailable_never_a_penalty()
    {
        var candidate = new MatchCandidate { Driver = GoodDriver(), Trailer = GoodTrailer() };

        var result = LtlTestFactory.Scorer().Score(Load(), candidate);

        var factor = Assert.Single(result.Factors, f => f.Name == "Dispatch preference");
        Assert.Equal(MatchFactorStatus.Unavailable, factor.Status);
        Assert.Equal(0, factor.MaxPoints);
    }

    [Fact]
    public void Terminated_co_driver_raises_a_non_blocking_warning()
    {
        var coDriver = GoodDriver();
        coDriver.Id = "DR2";
        coDriver.Name = "Pat Codriver";
        coDriver.TerminatedAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

        var candidate = new MatchCandidate { Driver = GoodDriver(), Trailer = GoodTrailer(), CoDriver = coDriver };

        var result = LtlTestFactory.Scorer().Score(Load(), candidate);

        Assert.Contains(result.Warnings, w => w.Contains("Co-driver") && w.Contains("terminated"));
        // A co-driver warning is not a hard disqualifier — the primary candidate is still clean.
        Assert.Empty(result.Disqualifiers);
    }

    [Fact]
    public void Prediction_basis_is_recorded_on_the_result()
    {
        var candidate = new MatchCandidate { Driver = GoodDriver(), Trailer = GoodTrailer() };

        var result = LtlTestFactory.Scorer().Score(
            Load(), candidate, events: null, windowFeasibility: null, predictionBasis: "AlvysPredictionUnavailable");

        Assert.Equal("AlvysPredictionUnavailable", result.PredictionBasis);
    }
}
