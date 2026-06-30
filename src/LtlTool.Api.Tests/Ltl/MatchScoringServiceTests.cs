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
}
