using LtlTool.Api.Features.Ltl;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Covers the deterministic delivery-ETA predictor (Phase 7.3): it only estimates for in-transit
/// loads, derives the ETA from PCMiler miles ÷ average speed anchored at actual pickup, flags a
/// predicted-late arrival past the delivery window + grace, and degrades honestly (no guess) when
/// mileage is missing or the load is not in transit. Ground-truth cases use the real va336
/// trips_in_transit_page1.json trip 1003703 timings/mileage.
/// </summary>
public sealed class EtaEstimatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
    private static readonly EtaOptions Options = new(); // 47 mph, 30 min grace

    [Fact]
    public void In_transit_load_gets_an_eta_anchored_at_pickup()
    {
        // 470 mi ÷ 47 mph = 10h transit; pickup at Now → ETA Now+10h.
        var eta = EtaEstimator.Estimate(
            Now, actualPickupAt: Now, actualDeliveryAt: null,
            scheduledDeliveryAt: Now.AddHours(12), delivered: false,
            loadedMiles: 470m, billingMiles: null, Options);

        Assert.Equal(Now.AddHours(10), eta.PredictedDeliveryAt);
        Assert.False(eta.PredictedLate); // 10:00 < 12:30 (window + grace)
        Assert.Equal(470m, eta.MilesUsed);
        Assert.Contains("PCMiler", eta.Basis);
    }

    [Fact]
    public void Eta_past_the_window_plus_grace_is_predicted_late()
    {
        var eta = EtaEstimator.Estimate(
            Now, actualPickupAt: Now, actualDeliveryAt: null,
            scheduledDeliveryAt: Now.AddHours(8), delivered: false,
            loadedMiles: 470m, billingMiles: null, Options);

        // ETA Now+10h is past the 08:00 window + 30 min grace.
        Assert.True(eta.PredictedLate);
    }

    [Fact]
    public void Falls_back_to_billing_miles_when_no_loaded_miles()
    {
        var eta = EtaEstimator.Estimate(
            Now, actualPickupAt: Now, actualDeliveryAt: null,
            scheduledDeliveryAt: null, delivered: false,
            loadedMiles: null, billingMiles: 94m, Options);

        Assert.Equal(Now.AddHours(2), eta.PredictedDeliveryAt); // 94 / 47 = 2h
        Assert.Contains("billing miles", eta.Basis);
    }

    [Fact]
    public void No_mileage_yields_no_eta_with_an_honest_reason()
    {
        var eta = EtaEstimator.Estimate(
            Now, actualPickupAt: Now, actualDeliveryAt: null,
            scheduledDeliveryAt: Now.AddHours(4), delivered: false,
            loadedMiles: null, billingMiles: null, Options);

        Assert.Null(eta.PredictedDeliveryAt);
        Assert.False(eta.PredictedLate);
        Assert.Contains("No PCMiler miles", eta.Basis);
    }

    [Fact]
    public void Not_yet_picked_up_yields_no_eta()
    {
        var eta = EtaEstimator.Estimate(
            Now, actualPickupAt: null, actualDeliveryAt: null,
            scheduledDeliveryAt: Now.AddHours(4), delivered: false,
            loadedMiles: 470m, billingMiles: null, Options);

        Assert.Same(EtaEstimate.None, eta);
    }

    [Fact]
    public void Delivered_load_yields_no_eta()
    {
        var eta = EtaEstimator.Estimate(
            Now, actualPickupAt: Now.AddHours(-20), actualDeliveryAt: Now.AddHours(-2),
            scheduledDeliveryAt: Now.AddHours(-1), delivered: true,
            loadedMiles: 470m, billingMiles: null, Options);

        Assert.Same(EtaEstimate.None, eta);
    }

    [Fact]
    public void GroundTruth_trip1003703_predicts_on_time_not_a_false_alarm()
    {
        // Real trip 1003703: 2,220 PCMiler loaded miles, actual pickup 2026-07-16T16:43:14-04:00,
        // delivery appt 2026-07-20T08:00:00-06:00. 2220 / 47 ≈ 47.2h → ETA ~2026-07-18, well before
        // the 07-20 appointment, so the estimator must NOT raise a false predicted-late.
        var pickup = new DateTimeOffset(2026, 7, 16, 16, 43, 14, TimeSpan.FromHours(-4));
        var appt = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.FromHours(-6));
        var now = new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero);

        var eta = EtaEstimator.Estimate(
            now, actualPickupAt: pickup, actualDeliveryAt: null,
            scheduledDeliveryAt: appt, delivered: false,
            loadedMiles: 2220m, billingMiles: null, Options);

        Assert.NotNull(eta.PredictedDeliveryAt);
        Assert.True(eta.PredictedDeliveryAt < appt);
        Assert.False(eta.PredictedLate);
        Assert.Equal(2220m, eta.MilesUsed);
    }
}
