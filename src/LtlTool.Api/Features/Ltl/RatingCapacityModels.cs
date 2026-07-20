namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// A point-in-time snapshot of internal fleet capacity, read live from Alvys (Phase 7.4): how many
/// trucks are active, how the trailer pool breaks down by equipment type, and how many trips are
/// currently in transit. Every number is a live Alvys read — nothing is fabricated, and a bounded
/// sweep that hits its cap is reported via <see cref="Truncated"/> so the UI can say "at least N".
/// </summary>
public sealed class CapacitySnapshot
{
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Trucks whose Alvys status reads active/available right now.</summary>
    public int ActiveTrucks { get; init; }

    /// <summary>All trucks seen in the sweep (active + inactive), for an honest denominator.</summary>
    public int TotalTrucks { get; init; }

    /// <summary>Trips whose Alvys status is in-transit/en-route right now.</summary>
    public int InTransitTrips { get; init; }

    /// <summary>All trailers seen in the sweep.</summary>
    public int TotalTrailers { get; init; }

    /// <summary>Trailer pool broken down by Alvys equipment type, most common first.</summary>
    public IReadOnlyList<TrailerTypeCount> TrailersByType { get; init; } = [];

    /// <summary>True when any of the underlying sweeps hit its scan cap; counts are then a floor.</summary>
    public bool Truncated { get; init; }

    public string Source { get; init; } =
        "Live Alvys — active trucks, trailer pool, and in-transit trips. Read-only; no writeback.";
}

/// <summary>One equipment-type bucket in the trailer pool breakdown.</summary>
public sealed class TrailerTypeCount
{
    public required string EquipmentType { get; init; }
    public int Count { get; init; }
}

/// <summary>
/// Recent lane rate context (Phase 7.4): the revenue-per-mile spread across recently <em>Delivered</em>
/// loads on the same origin→destination state pair in Alvys. Deliberately labelled "recent tenant
/// history, not market rate" — this is what Value Truck itself has recently billed on the lane, NOT a
/// DAT/Greenscreens market feed (explicit non-goal for this slice). Null RPM figures mean too few
/// samples to show a range — surfaced honestly, never guessed.
/// </summary>
public sealed class LaneRateContext
{
    public required string OriginState { get; init; }
    public required string DestinationState { get; init; }

    /// <summary>Number of delivered loads on the lane that had both a rate and mileage to price.</summary>
    public int SampleSize { get; init; }

    public decimal? MedianRpm { get; init; }
    public decimal? MinRpm { get; init; }
    public decimal? MaxRpm { get; init; }

    public required string Basis { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Not enough priced deliveries on the lane to show a range — an honest, non-guessing verdict.</summary>
    public static LaneRateContext Insufficient(
        string originState, string destinationState, int sampleSize, DateTimeOffset now) => new()
    {
        OriginState = originState,
        DestinationState = destinationState,
        SampleSize = sampleSize,
        MedianRpm = null,
        MinRpm = null,
        MaxRpm = null,
        Basis =
            "Not enough recent delivered loads with a rate and mileage on this lane to show a "
            + "revenue-per-mile range. Recent tenant history, not a market rate.",
        GeneratedAt = now,
    };
}
