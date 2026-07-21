using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Covers the deterministic actual-late DELIVERY detector: a delivery stop whose appointment/window
/// end has passed with no arrival recorded is flagged; already-arrived, sentinel (year 9999), and
/// within-grace cases are not. Ground-truth case mirrors the fresh 2026-07-20T23:51Z in-transit pull
/// (load 1004253, Laredo TX, delivery window end 2026-07-20T17:00-05:00, no arrival) which the owner
/// requires to be flagged.
/// </summary>
public sealed class LateDeliveryDetectorTests
{
    private const int Grace = 30;

    private static AlvysTrip TripWithDeliveryStop(
        AlvysTripStop stop, string? pickupType = "Pickup") => new()
    {
        Stops =
        [
            new AlvysTripStop { StopType = pickupType, Sequence = 0 },
            stop,
        ],
    };

    [Fact]
    public void GroundTruth_load1004253_is_flagged_late()
    {
        // Live wire shape: inline StopWindow{End}, AppointmentDate, ArrivedAt — window end
        // 2026-07-20T17:00-05:00 (= 22:00Z), evaluated at the 23:51Z pull, no arrival recorded.
        var stop = new AlvysTripStop
        {
            Id = "stop-1004253",
            StopType = "Delivery",
            Sequence = 1,
            Status = "Covered",
            Address = new AlvysAddress { City = "Laredo", State = "TX" },
            StopWindow = new AlvysStopWindow
            {
                Begin = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.FromHours(-5)),
                End = new DateTimeOffset(2026, 7, 20, 17, 0, 0, TimeSpan.FromHours(-5)),
            },
        };
        var now = new DateTimeOffset(2026, 7, 20, 23, 51, 0, TimeSpan.Zero);

        var late = LateDeliveryDetector.Detect(TripWithDeliveryStop(stop), now, Grace);

        Assert.NotNull(late);
        Assert.Equal("stop-1004253", late!.StopId);
        Assert.Equal("Laredo", late.DestinationCity);
        Assert.Equal("TX", late.DestinationState);
        Assert.Equal(1.9, late.HoursOverdue); // 23:51Z − 22:00Z = 1h51m ≈ 1.9h
        Assert.Contains("no arrival recorded (per Alvys stop status)", late.Message);
        Assert.Contains("delivery window", late.Message);
    }

    [Fact]
    public void Appointment_date_takes_precedence_over_window_for_basis()
    {
        var stop = new AlvysTripStop
        {
            Id = "s",
            StopType = "Delivery",
            Sequence = 1,
            AppointmentDate = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero),
            StopWindow = new AlvysStopWindow { End = new DateTimeOffset(2026, 7, 20, 18, 0, 0, TimeSpan.Zero) },
        };
        var now = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);

        var late = LateDeliveryDetector.Detect(TripWithDeliveryStop(stop), now, Grace);

        Assert.NotNull(late);
        Assert.Equal("appointment", late!.WindowBasis);
    }

    [Fact]
    public void Recorded_arrival_clears_the_flag()
    {
        var stop = new AlvysTripStop
        {
            Id = "s",
            StopType = "Delivery",
            Sequence = 1,
            ArrivedAt = new DateTimeOffset(2026, 7, 20, 16, 0, 0, TimeSpan.Zero),
            StopWindow = new AlvysStopWindow { End = new DateTimeOffset(2026, 7, 20, 17, 0, 0, TimeSpan.Zero) },
        };
        var now = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);

        Assert.Null(LateDeliveryDetector.Detect(TripWithDeliveryStop(stop), now, Grace));
    }

    [Fact]
    public void Legacy_fixture_arrival_key_also_clears_the_flag()
    {
        var stop = new AlvysTripStop
        {
            Id = "s",
            StopType = "Delivery",
            Sequence = 1,
            ArrivedDate = new DateTimeOffset(2026, 7, 20, 16, 0, 0, TimeSpan.Zero),
            StopWindowEnd = new DateTimeOffset(2026, 7, 20, 17, 0, 0, TimeSpan.Zero),
        };
        var now = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);

        Assert.Null(LateDeliveryDetector.Detect(TripWithDeliveryStop(stop), now, Grace));
    }

    [Fact]
    public void Sentinel_far_future_window_is_never_flagged()
    {
        var stop = new AlvysTripStop
        {
            Id = "s",
            StopType = "Delivery",
            Sequence = 1,
            StopWindow = new AlvysStopWindow { End = new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero) },
        };
        var now = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);

        Assert.Null(LateDeliveryDetector.Detect(TripWithDeliveryStop(stop), now, Grace));
    }

    [Fact]
    public void Within_grace_window_does_not_flap()
    {
        var end = new DateTimeOffset(2026, 7, 20, 17, 0, 0, TimeSpan.Zero);
        var stop = new AlvysTripStop
        {
            Id = "s",
            StopType = "Delivery",
            Sequence = 1,
            StopWindow = new AlvysStopWindow { End = end },
        };

        // 20 min past the window (< 30 min grace) → not yet flagged.
        Assert.Null(LateDeliveryDetector.Detect(TripWithDeliveryStop(stop), end.AddMinutes(20), Grace));
        // 40 min past → beyond grace, flagged.
        Assert.NotNull(LateDeliveryDetector.Detect(TripWithDeliveryStop(stop), end.AddMinutes(40), Grace));
    }

    [Fact]
    public void No_delivery_stop_yields_null()
    {
        var trip = new AlvysTrip
        {
            Stops = [new AlvysTripStop { StopType = "Pickup", Sequence = 0 }],
        };

        Assert.Null(LateDeliveryDetector.Detect(trip, new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero), Grace));
    }

    [Fact]
    public void No_usable_window_yields_null()
    {
        var stop = new AlvysTripStop { Id = "s", StopType = "Delivery", Sequence = 1 };

        Assert.Null(LateDeliveryDetector.Detect(TripWithDeliveryStop(stop), new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero), Grace));
    }
}
