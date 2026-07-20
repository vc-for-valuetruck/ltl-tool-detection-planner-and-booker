using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the normalization layer derives origin/destination/schedule/revenue correctly and —
/// critically — surfaces missing values as <see cref="MissingDataFlag"/>s rather than defaulting.
/// </summary>
public sealed class LtlNormalizationServiceTests
{
    private static AlvysLoad FullLoad() => new()
    {
        Id = "L1",
        LoadNumber = "100",
        CustomerId = "CUST-1",
        CustomerName = "Acme",
        Status = "Delivered",
        LoadType = "LTL",
        CustomerRate = 1000m,
        CustomerMileage = 500m,
        Weight = 8000m,
        RequiredEquipment = ["Dry Van"],
        ScheduledPickupAt = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero),
        ScheduledDeliveryAt = new DateTimeOffset(2026, 6, 3, 8, 0, 0, TimeSpan.Zero),
        Stops =
        [
            new AlvysLoadStop { Sequence = 1, StopType = "Pickup", Address = new AlvysAddress { City = "Dallas", State = "TX" } },
            new AlvysLoadStop { Sequence = 2, StopType = "Delivery", Address = new AlvysAddress { City = "Atlanta", State = "GA" } },
        ],
    };

    [Fact]
    public void Normalize_derives_origin_destination_revenue_and_rpm()
    {
        var summary = LtlTestFactory.Normalizer().Normalize(FullLoad());

        Assert.Equal("Dallas", summary.Origin?.City);
        Assert.Equal("GA", summary.Destination?.State);
        Assert.Equal(1000m, summary.Revenue);
        Assert.Equal(2m, summary.RevenuePerMile);
        Assert.Equal(AssignmentState.Assigned, summary.Assignment);
        Assert.True(summary.IsLtl);
    }

    [Fact]
    public void Normalize_flags_missing_rate_weight_and_customer_without_defaulting()
    {
        var load = new AlvysLoad { Id = "L2", Status = "Open" };

        var summary = LtlTestFactory.Normalizer().Normalize(load);

        Assert.Null(summary.Revenue);
        Assert.Null(summary.WeightLbs);
        Assert.Contains(MissingDataFlag.Rate, summary.MissingData);
        Assert.Contains(MissingDataFlag.Weight, summary.MissingData);
        Assert.Contains(MissingDataFlag.Customer, summary.MissingData);
        Assert.Contains(MissingDataFlag.Origin, summary.MissingData);
        Assert.Contains(MissingDataFlag.Destination, summary.MissingData);
    }

    [Fact]
    public void Normalize_always_flags_commodity_as_missing()
    {
        // Commodity is not on the Alvys projection — must never be invented.
        var summary = LtlTestFactory.Normalizer().Normalize(FullLoad());
        Assert.Contains(MissingDataFlag.Commodity, summary.MissingData);
    }

    [Fact]
    public void Normalize_always_flags_dimensions_as_missing()
    {
        // Per-item freight dimensions (LxWxH / class) are not on the Alvys projection — a 3D
        // trailer-fit verdict is not computable today, so dimensions are always flagged missing.
        var summary = LtlTestFactory.Normalizer().Normalize(FullLoad());
        Assert.Contains(MissingDataFlag.Dimensions, summary.MissingData);
    }

    [Fact]
    public void Normalize_sums_rate_components_when_no_explicit_customer_rate()
    {
        var load = FullLoad();
        load.CustomerRate = null;
        load.Linehaul = 800m;
        load.FuelSurcharge = 150m;
        load.CustomerAccessorials = 50m;

        var summary = LtlTestFactory.Normalizer().Normalize(load);

        Assert.Equal(1000m, summary.Revenue);
    }

    [Fact]
    public void Normalize_leaves_ltl_classification_null_when_no_signal()
    {
        var load = FullLoad();
        load.LoadType = null;
        load.RequiredEquipment = [];

        var summary = LtlTestFactory.Normalizer().Normalize(load);

        Assert.Null(summary.IsLtl);
        Assert.Contains(MissingDataFlag.Equipment, summary.MissingData);
    }

    [Fact]
    public void Normalize_flags_predicted_late_in_transit_load_as_non_blocking_exception()
    {
        // Clock is pinned at LtlTestFactory.Now (2026-06-30). Anchor pickup at Now with 470 loaded
        // miles ÷ 47 mph = 10h transit → ETA Now+10h, well past a Now+1h delivery window + grace.
        var load = FullLoad();
        load.Status = "In Transit";
        load.ActualPickupAt = LtlTestFactory.Now;
        load.ActualDeliveryAt = null;
        load.DeliveredAt = null;
        load.ScheduledDeliveryAt = LtlTestFactory.Now.AddHours(1);
        load.CustomerMileage = null; // force use of the loadedMiles argument

        var summary = LtlTestFactory.Normalizer().Normalize(load, loadedMiles: 470m);

        Assert.True(summary.PredictedLate);
        Assert.Equal(LtlTestFactory.Now.AddHours(10), summary.PredictedDeliveryAt);
        Assert.Contains("PCMiler", summary.EtaBasis);

        var flag = Assert.Single(
            summary.Exceptions,
            e => e.Code == LtlNormalizationService.PredictedLateExceptionCode);
        Assert.False(flag.BlocksBilling);
    }

    [Fact]
    public void Normalize_does_not_predict_eta_for_delivered_load()
    {
        // FullLoad() is Delivered — no in-transit ETA should be produced, and no predicted-late flag.
        var summary = LtlTestFactory.Normalizer().Normalize(FullLoad(), loadedMiles: 470m);

        Assert.Null(summary.PredictedDeliveryAt);
        Assert.False(summary.PredictedLate);
        Assert.DoesNotContain(
            summary.Exceptions,
            e => e.Code == LtlNormalizationService.PredictedLateExceptionCode);
    }
}
