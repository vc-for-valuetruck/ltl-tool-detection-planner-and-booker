using LtlTool.Api.Features.Ltl.Agent;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Agent;

/// <summary>
/// Golden-value tests for the reference quote estimator. The pricing math is deterministic given the
/// inputs (surge is a caller argument), so we can pin exact dollar/CO₂ figures against the default
/// rate card and lock the ported LogisticsRoute formula in place.
/// </summary>
public sealed class QuoteEstimatorServiceTests
{
    private static QuoteEstimatorService Build(QuoteEstimatorOptions? opts = null) =>
        new(Microsoft.Extensions.Options.Options.Create(opts ?? new QuoteEstimatorOptions()));

    private static QuoteEstimateInput Input(
        string origin = "TX",
        string destination = "TX",
        decimal weightLbs = 10000m,
        decimal? distanceMiles = 500m,
        QuoteTransportMode? mode = null,
        bool perishable = false,
        bool hazmat = false) => new()
        {
            Origin = origin,
            Destination = destination,
            WeightLbs = weightLbs,
            DistanceMiles = distanceMiles,
            Mode = mode,
            Perishable = perishable,
            Hazmat = hazmat,
        };

    [Fact]
    public void Truck_golden_values_match_default_rate_card()
    {
        var estimate = Build().Estimate(Input());

        // weightTons=5; linehaul=500*(1.75+0.12*5)=1175; fuel=1175*0.28=329; handling=45 (>2205 lbs);
        // no accessorials; TX has no congestion premium; subtotal=1549; surge=1.0;
        // insurance=1549*0.02=30.98; total=1579.98; co2=5*500*0.1618=404.50.
        Assert.Equal(QuoteTransportMode.Truck, estimate.Mode);
        Assert.Equal(500m, estimate.DistanceMiles);
        Assert.Equal("caller-supplied", estimate.DistanceSource);
        Assert.Equal(1175.00m, estimate.Linehaul);
        Assert.Equal(329.00m, estimate.FuelSurcharge);
        Assert.Equal(45.00m, estimate.HandlingFee);
        Assert.Equal(0.00m, estimate.AccessorialSurcharge);
        Assert.Equal(0.00m, estimate.CongestionPremium);
        Assert.Equal(1.000m, estimate.SurgeMultiplier);
        Assert.Equal(30.98m, estimate.Insurance);
        Assert.Equal(1579.98m, estimate.TotalCost);
        Assert.Equal(404.50m, estimate.Co2Kg);
    }

    [Fact]
    public void Light_freight_below_threshold_gets_no_handling_fee()
    {
        var estimate = Build().Estimate(Input(weightLbs: 2000m, distanceMiles: 100m));
        Assert.Equal(0.00m, estimate.HandlingFee);
    }

    [Fact]
    public void Perishable_and_hazmat_stack_additively()
    {
        var estimate = Build().Estimate(Input(perishable: true, hazmat: true));
        Assert.Equal(370.00m, estimate.AccessorialSurcharge); // 120 + 250
    }

    [Fact]
    public void Congested_destination_state_adds_a_premium_over_linehaul()
    {
        // IL congestion multiplier is 1.06 → premium = linehaul * 0.06.
        var estimate = Build().Estimate(Input(destination: "IL"));
        Assert.Equal(70.50m, estimate.CongestionPremium); // 1175 * 0.06
    }

    [Fact]
    public void Surge_multiplier_raises_total_and_is_never_below_one()
    {
        var baseline = Build().Estimate(Input());
        var surged = Build().Estimate(Input(), surgeMultiplier: 1.2);
        Assert.True(surged.TotalCost > baseline.TotalCost);
        Assert.Equal(1.200m, surged.SurgeMultiplier);

        // A sub-1.0 surge is clamped to 1.0 (never a discount).
        var clamped = Build().Estimate(Input(), surgeMultiplier: 0.5);
        Assert.Equal(1.000m, clamped.SurgeMultiplier);
        Assert.Equal(baseline.TotalCost, clamped.TotalCost);
    }

    [Fact]
    public void Rail_is_cheaper_and_lower_co2_than_truck_for_same_lane()
    {
        var truck = Build().Estimate(Input(mode: QuoteTransportMode.Truck));
        var rail = Build().Estimate(Input(mode: QuoteTransportMode.Rail));
        Assert.True(rail.TotalCost < truck.TotalCost);
        Assert.True(rail.Co2Kg < truck.Co2Kg);
    }

    [Fact]
    public void Missing_distance_falls_back_to_reference_estimate_between_states()
    {
        var estimate = Build().Estimate(Input(origin: "TX", destination: "CA", distanceMiles: null));
        Assert.Equal("reference-haversine-estimate", estimate.DistanceSource);
        Assert.True(estimate.DistanceMiles > 0);
    }

    [Fact]
    public void Unresolvable_distance_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Build().Estimate(Input(origin: "Narnia", destination: "Oz", distanceMiles: null)));
    }

    [Fact]
    public void Non_positive_weight_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Build().Estimate(Input(weightLbs: 0m)));
    }

    [Fact]
    public void Estimate_always_carries_a_reference_only_disclaimer()
    {
        var estimate = Build().Estimate(Input());
        Assert.Contains("Reference estimate only", estimate.Disclaimer);
        Assert.DoesNotContain("Alvys rate", estimate.Disclaimer[..15]);
    }

    [Fact]
    public void Reference_distance_is_null_for_unknown_states()
    {
        Assert.Null(Build().ReferenceDistanceMiles("XX", "TX"));
        Assert.NotNull(Build().ReferenceDistanceMiles("Texas", "California"));
    }
}
