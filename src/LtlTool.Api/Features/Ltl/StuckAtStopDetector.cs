using System.Globalization;
using LtlTool.Api.Features.Integrations.Alvys;

namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// Deterministic stuck-at-stop detector. Read-only over a single live Alvys trip: a stop the truck
/// recorded an ARRIVAL at but no DEPARTURE from, dwelling past a configured threshold, is surfaced
/// as stuck at that stop.
///
/// <para>
/// This is a data-quality-sensitive signal. A long dwell very often means the driver simply never
/// closed the stop in Alvys, NOT that the truck is physically stranded — so the surfaced
/// <see cref="LtlStuckStop.Message"/> ALWAYS carries the honest caveat "Per Alvys stop status —
/// driver may not have closed the stop". The UI must never present a long dwell as a hard fact.
/// </para>
///
/// <para>
/// When several stops on one trip qualify, the stop with the greatest dwell is surfaced
/// (deterministic, worst-first). Sentinel far-future arrival dates (year ≥ 9999) are treated as
/// unknown and never flagged.
/// </para>
/// </summary>
public static class StuckAtStopDetector
{
    /// <summary>
    /// Detect the worst stuck-at-stop condition on a trip, or return null when no stop has an
    /// arrival with no departure dwelling past the threshold.
    /// </summary>
    /// <param name="trip">The live Alvys trip (read-only).</param>
    /// <param name="now">Evaluation instant (UTC).</param>
    /// <param name="thresholdHours">Dwell hours past arrival before flagging.</param>
    public static LtlStuckStop? Detect(AlvysTrip trip, DateTimeOffset now, int thresholdHours)
    {
        var threshold = TimeSpan.FromHours(Math.Max(0, thresholdHours));

        LtlStuckStop? worst = null;
        foreach (var stop in trip.Stops ?? [])
        {
            // ArrivedAt is the live wire field; ArrivedDate the legacy fixture key. Departure clears.
            var arrived = stop.ArrivedAt ?? stop.ArrivedDate;
            if (arrived is null) continue;
            if (stop.DepartedAt is not null || stop.DepartedDate is not null) continue;

            // Sentinel far-future arrival = unknown, never flagged.
            if (arrived.Value.Year >= 9999) continue;

            var dwell = now - arrived.Value;
            if (dwell <= threshold) continue;

            if (worst is not null && dwell.TotalHours <= worst.HoursSinceArrival) continue;

            var hours = Math.Round(dwell.TotalHours, 1);
            var city = string.IsNullOrWhiteSpace(stop.Address?.City) ? null : stop.Address!.City;
            var state = string.IsNullOrWhiteSpace(stop.Address?.State) ? null : stop.Address!.State;
            var stopType = string.IsNullOrWhiteSpace(stop.StopType) ? null : stop.StopType;
            var place = FormatPlace(stopType, city, state);

            worst = new LtlStuckStop
            {
                StopId = stop.Id ?? $"seq{stop.Sequence?.ToString(CultureInfo.InvariantCulture) ?? "?"}",
                StopType = stopType,
                City = city,
                State = state,
                ArrivedAt = arrived.Value,
                HoursSinceArrival = hours,
                Message =
                    $"Stuck at stop — {place}arrived {FormatLocal(arrived.Value)}, "
                    + $"no departure recorded after {hours:0.#}h. "
                    + "Per Alvys stop status — driver may not have closed the stop",
            };
        }

        return worst;
    }

    /// <summary>"Delivery in Dallas, TX " style prefix; empty when nothing is known.</summary>
    private static string FormatPlace(string? stopType, string? city, string? state)
    {
        var where = string.Join(", ", new[] { city, state }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (stopType is not null && where.Length > 0) return $"{stopType} in {where} ";
        if (stopType is not null) return $"{stopType} ";
        if (where.Length > 0) return $"{where} ";
        return string.Empty;
    }

    /// <summary>
    /// Format the arrival in its own recorded local offset (Alvys stamps each stop with the stop's
    /// local zone), so the operator sees the site's local time, not a UTC shift.
    /// </summary>
    private static string FormatLocal(DateTimeOffset value) =>
        value.ToString("MMM d, yyyy h:mm tt 'UTC'zzz", CultureInfo.InvariantCulture);
}
