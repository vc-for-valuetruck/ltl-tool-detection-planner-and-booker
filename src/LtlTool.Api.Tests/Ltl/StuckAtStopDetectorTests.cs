using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Covers the deterministic stuck-at-stop detector: a stop the truck arrived at but never recorded a
/// departure from, dwelling past the threshold, is flagged with the mandatory honest caveat.
/// Already-departed, sentinel (year 9999), and within-threshold cases are not flagged; the
/// greatest-dwell qualifying stop wins. Ground-truth case mirrors the fresh 2026-07-20T23:51Z
/// in-transit pull (load 1003339, Williamston, ~164.8h since arrival, no departure) which the owner
/// requires to be flagged.
/// </summary>
public sealed class StuckAtStopDetectorTests
{
    private const int Threshold = 6;

    private static AlvysTrip TripWith(params AlvysTripStop[] stops) => new() { Stops = [.. stops] };

    [Fact]
    public void GroundTruth_load1003339_is_flagged_stuck()
    {
        // Arrived 2026-07-14T03:00Z, no departure, evaluated at the 23:51Z pull on 2026-07-20
        // → ~164.85h dwell. Owner cites ~164.8h; assert a tolerant range around it.
        var stop = new AlvysTripStop
        {
            Id = "stop-1003339",
            StopType = "Delivery",
            Sequence = 1,
            Status = "Covered",
            Address = new AlvysAddress { City = "Williamston", State = "NC" },
            ArrivedAt = new DateTimeOffset(2026, 7, 14, 3, 0, 0, TimeSpan.Zero),
        };
        var now = new DateTimeOffset(2026, 7, 20, 23, 51, 0, TimeSpan.Zero);

        var stuck = StuckAtStopDetector.Detect(TripWith(stop), now, Threshold);

        Assert.NotNull(stuck);
        Assert.Equal("stop-1003339", stuck!.StopId);
        Assert.Equal("Williamston", stuck.City);
        Assert.Equal("NC", stuck.State);
        Assert.InRange(stuck.HoursSinceArrival, 160, 168);
        Assert.Contains("driver may not have closed the stop", stuck.Message);
        Assert.Contains("no departure recorded", stuck.Message);
    }

    [Fact]
    public void Arrived_no_departure_past_threshold_is_flagged()
    {
        var stop = new AlvysTripStop
        {
            Id = "s",
            StopType = "Pickup",
            Sequence = 1,
            ArrivedAt = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero),
        };
        var now = new DateTimeOffset(2026, 7, 20, 15, 0, 0, TimeSpan.Zero); // 7h dwell > 6h

        Assert.NotNull(StuckAtStopDetector.Detect(TripWith(stop), now, Threshold));
    }

    [Fact]
    public void Recorded_departure_clears_the_flag()
    {
        var stop = new AlvysTripStop
        {
            Id = "s",
            StopType = "Delivery",
            Sequence = 1,
            ArrivedAt = new DateTimeOffset(2026, 7, 14, 3, 0, 0, TimeSpan.Zero),
            DepartedAt = new DateTimeOffset(2026, 7, 14, 5, 0, 0, TimeSpan.Zero),
        };
        var now = new DateTimeOffset(2026, 7, 20, 23, 51, 0, TimeSpan.Zero);

        Assert.Null(StuckAtStopDetector.Detect(TripWith(stop), now, Threshold));
    }

    [Fact]
    public void Legacy_fixture_departure_key_also_clears_the_flag()
    {
        var stop = new AlvysTripStop
        {
            Id = "s",
            StopType = "Delivery",
            Sequence = 1,
            ArrivedDate = new DateTimeOffset(2026, 7, 14, 3, 0, 0, TimeSpan.Zero),
            DepartedDate = new DateTimeOffset(2026, 7, 14, 5, 0, 0, TimeSpan.Zero),
        };
        var now = new DateTimeOffset(2026, 7, 20, 23, 51, 0, TimeSpan.Zero);

        Assert.Null(StuckAtStopDetector.Detect(TripWith(stop), now, Threshold));
    }

    [Fact]
    public void Legacy_fixture_arrival_key_is_read()
    {
        var stop = new AlvysTripStop
        {
            Id = "s",
            StopType = "Delivery",
            Sequence = 1,
            ArrivedDate = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero),
        };
        var now = new DateTimeOffset(2026, 7, 20, 15, 0, 0, TimeSpan.Zero);

        Assert.NotNull(StuckAtStopDetector.Detect(TripWith(stop), now, Threshold));
    }

    [Fact]
    public void Within_threshold_yields_null()
    {
        var stop = new AlvysTripStop
        {
            Id = "s",
            StopType = "Delivery",
            Sequence = 1,
            ArrivedAt = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero),
        };
        var now = new DateTimeOffset(2026, 7, 20, 13, 0, 0, TimeSpan.Zero); // 5h dwell < 6h

        Assert.Null(StuckAtStopDetector.Detect(TripWith(stop), now, Threshold));
    }

    [Fact]
    public void Sentinel_far_future_arrival_is_never_flagged()
    {
        var stop = new AlvysTripStop
        {
            Id = "s",
            StopType = "Delivery",
            Sequence = 1,
            ArrivedAt = new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero),
        };
        var now = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);

        Assert.Null(StuckAtStopDetector.Detect(TripWith(stop), now, Threshold));
    }

    [Fact]
    public void No_arrival_yields_null()
    {
        var stop = new AlvysTripStop { Id = "s", StopType = "Delivery", Sequence = 1 };
        var now = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);

        Assert.Null(StuckAtStopDetector.Detect(TripWith(stop), now, Threshold));
    }

    [Fact]
    public void Greatest_dwell_qualifying_stop_is_surfaced()
    {
        var now = new DateTimeOffset(2026, 7, 20, 23, 0, 0, TimeSpan.Zero);
        var mild = new AlvysTripStop
        {
            Id = "mild",
            StopType = "Pickup",
            Sequence = 0,
            ArrivedAt = now.AddHours(-8), // 8h
        };
        var worst = new AlvysTripStop
        {
            Id = "worst",
            StopType = "Delivery",
            Sequence = 1,
            ArrivedAt = now.AddHours(-40), // 40h
        };

        var stuck = StuckAtStopDetector.Detect(TripWith(mild, worst), now, Threshold);

        Assert.NotNull(stuck);
        Assert.Equal("worst", stuck!.StopId);
        Assert.InRange(stuck.HoursSinceArrival, 39.9, 40.1);
    }
}
