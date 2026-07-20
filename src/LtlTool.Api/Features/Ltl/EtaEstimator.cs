namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// The outcome of a delivery-ETA prediction. <see cref="PredictedDeliveryAt"/> is null when the
/// load is not in transit or carries no mileage to estimate from — never guessed. Every populated
/// estimate carries a <see cref="Basis"/> string stating exactly how it was derived so the UI can
/// present it as an estimate, not a routing-API promise.
/// </summary>
public sealed record EtaEstimate
{
    public static readonly EtaEstimate None = new();

    /// <summary>Predicted delivery instant, or null when not computable.</summary>
    public DateTimeOffset? PredictedDeliveryAt { get; init; }

    /// <summary>
    /// True when the predicted arrival is past the scheduled delivery window plus the grace
    /// allowance. Only ever true when both an ETA and a scheduled delivery are known.
    /// </summary>
    public bool PredictedLate { get; init; }

    /// <summary>Miles the estimate was computed from (loaded miles preferred, else billing miles).</summary>
    public decimal? MilesUsed { get; init; }

    /// <summary>Human-readable provenance/rationale, always set when an ETA or a reason exists.</summary>
    public string? Basis { get; init; }
}

/// <summary>
/// Deterministic delivery-ETA predictor (Phase 7.3, issue #107 scope 1). Pure arithmetic over
/// Alvys-derived signals — no external routing API. The estimate is deliberately simple and honest:
/// anchor at the <em>actual</em> pickup time, add (PCMiler loaded miles ÷ configured average
/// line-haul speed) as transit time. It is only produced for a load that is in transit (picked up,
/// not yet delivered); anything else returns <see cref="EtaEstimate.None"/> rather than a guess.
/// </summary>
public static class EtaEstimator
{
    public static EtaEstimate Estimate(
        DateTimeOffset now,
        DateTimeOffset? actualPickupAt,
        DateTimeOffset? actualDeliveryAt,
        DateTimeOffset? scheduledDeliveryAt,
        bool delivered,
        decimal? loadedMiles,
        decimal? billingMiles,
        EtaOptions options)
    {
        // Only in-transit loads have a meaningful ETA: picked up, not yet delivered.
        if (delivered || actualDeliveryAt is not null || actualPickupAt is null)
            return EtaEstimate.None;

        var miles = loadedMiles is > 0 ? loadedMiles : (billingMiles is > 0 ? billingMiles : null);
        if (miles is null || options.AverageSpeedMph <= 0)
        {
            return new EtaEstimate
            {
                Basis = "No PCMiler miles on the trip — ETA unavailable. Verify progress in Alvys.",
            };
        }

        var transitHours = (double)(miles.Value / options.AverageSpeedMph);
        var eta = actualPickupAt.Value.AddHours(transitHours);
        var milesLabel = loadedMiles is > 0 ? "loaded miles" : "billing miles";
        var late = scheduledDeliveryAt is not null
            && eta > scheduledDeliveryAt.Value.AddMinutes(options.LateGraceMinutes);

        return new EtaEstimate
        {
            PredictedDeliveryAt = eta,
            PredictedLate = late,
            MilesUsed = miles,
            Basis =
                $"Derived from PCMiler {milesLabel} ({miles.Value:0} mi) via Alvys ÷ "
                + $"{options.AverageSpeedMph:0} mph average, anchored at actual pickup. Estimate — not a routing-API ETA.",
        };
    }
}
