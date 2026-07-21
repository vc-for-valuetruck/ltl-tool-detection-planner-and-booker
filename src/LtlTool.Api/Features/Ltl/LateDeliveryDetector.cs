using System.Globalization;
using LtlTool.Api.Features.Integrations.Alvys;

namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// Deterministic actual-late DELIVERY detector. Read-only over a single live Alvys trip: a
/// delivery stop whose appointment date / window end has already passed, with no arrival recorded
/// on the stop, is an actual-late delivery. This is a past fact taken straight from Alvys stop
/// status — NOT a projection (that is <see cref="EtaEstimator"/>'s predicted-late job).
///
/// <para>
/// Scope is intentionally narrow (owner-approved, 2026-07-20): late DELIVERY only. It does not
/// detect late pickups, idle/stuck-at-stop, or any other stop condition.
/// </para>
///
/// <para>
/// Guards against false positives: sentinel far-future dates (year ≥ 9999) are treated as unknown
/// and never flagged; a configurable grace window absorbs normal check-in slack so the flag does
/// not flap right at the window boundary.
/// </para>
/// </summary>
public static class LateDeliveryDetector
{
    /// <summary>
    /// Detect an actual-late delivery on a trip, or return null when the delivery is on time,
    /// already arrived, has no usable window, or the window is a sentinel/unknown date.
    /// </summary>
    /// <param name="trip">The live Alvys trip (read-only).</param>
    /// <param name="now">Evaluation instant (UTC).</param>
    /// <param name="graceMinutes">Minutes past the window end before flagging (absorbs slack).</param>
    public static LtlLateDelivery? Detect(AlvysTrip trip, DateTimeOffset now, int graceMinutes)
    {
        var stop = LastDeliveryStop(trip);
        if (stop is null) return null;

        // Already arrived → not late. ArrivedAt is the live wire field; ArrivedDate is the legacy
        // key some fixtures populate. Either recorded arrival clears the flag.
        if (stop.ArrivedAt is not null || stop.ArrivedDate is not null) return null;

        var (windowEnd, basis) = ResolveWindow(stop);
        if (windowEnd is null) return null;

        // Sentinel far-future date = unknown, never flagged.
        if (windowEnd.Value.Year >= 9999) return null;

        var grace = TimeSpan.FromMinutes(Math.Max(0, graceMinutes));
        if (now <= windowEnd.Value + grace) return null;

        var hoursOverdue = Math.Round((now - windowEnd.Value).TotalHours, 1);
        var city = string.IsNullOrWhiteSpace(stop.Address?.City) ? null : stop.Address!.City;
        var state = string.IsNullOrWhiteSpace(stop.Address?.State) ? null : stop.Address!.State;

        return new LtlLateDelivery
        {
            StopId = stop.Id ?? $"seq{stop.Sequence?.ToString(CultureInfo.InvariantCulture) ?? "?"}",
            DestinationCity = city,
            DestinationState = state,
            WindowEnd = windowEnd.Value,
            WindowBasis = basis,
            HoursOverdue = hoursOverdue,
            Message =
                $"Late delivery — {basis} ended {FormatLocal(windowEnd.Value)}, "
                + "no arrival recorded (per Alvys stop status)",
        };
    }

    /// <summary>The last delivery stop on the trip (ordered by sequence), or null when none.</summary>
    private static AlvysTripStop? LastDeliveryStop(AlvysTrip trip) =>
        (trip.Stops ?? [])
        .Where(s => s.StopType is not null
            && s.StopType.Contains("Delivery", StringComparison.OrdinalIgnoreCase))
        .OrderBy(s => s.Sequence ?? int.MaxValue)
        .LastOrDefault();

    /// <summary>
    /// The window boundary that must have passed: the delivery appointment date when present, else
    /// the delivery window end. Both the live wire shape (AppointmentDate / StopWindow.End) and the
    /// legacy fixture keys (Appointment / StopWindowEnd) are read so real and unit data both bind.
    /// </summary>
    private static (DateTimeOffset? WindowEnd, string Basis) ResolveWindow(AlvysTripStop stop)
    {
        var appointment = stop.AppointmentDate ?? stop.Appointment;
        if (appointment is not null) return (appointment, "appointment");

        var windowEnd = stop.StopWindow?.End ?? stop.StopWindowEnd;
        if (windowEnd is not null) return (windowEnd, "delivery window");

        return (null, string.Empty);
    }

    /// <summary>
    /// Format the window end in its own recorded local offset (Alvys stamps each stop with the
    /// stop's local zone), so the operator sees the delivery site's local time, not a UTC shift.
    /// </summary>
    private static string FormatLocal(DateTimeOffset value) =>
        value.ToString("MMM d, yyyy h:mm tt 'UTC'zzz", CultureInfo.InvariantCulture);
}
