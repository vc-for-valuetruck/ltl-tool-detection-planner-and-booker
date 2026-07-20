namespace LtlTool.Api.Features.Ltl.Optimization;

/// <summary>
/// Phase 2 trailer-fit engine: given the loads a dispatcher wants to consolidate and the target
/// trailer's capacity, decide whether they fit (by weight/cube/pallets) and, later, in what
/// arrangement. Selected at startup by <c>Ltl:Optimization:TrailerFit:Enabled</c> — when disabled
/// the <see cref="NullTrailerFitService"/> is registered and no computation ever runs.
///
/// <para>
/// The engine is a pure compute function: every input is Alvys-derived operational data supplied
/// by the API. It never fetches load/customer data itself, keeping "Alvys is the only source of
/// truth" intact. It never invents missing dimensions — an item with no dims is reported as
/// unverifiable, not assumed to fit.
/// </para>
/// </summary>
public interface ITrailerFitService
{
    /// <summary>True when a real fit engine is wired; false for the null implementation.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Evaluate whether the requested loads fit the trailer. Never throws for a business reason —
    /// an unknown result is expressed via <see cref="TrailerFitResult.Verdict"/>, not an exception.
    /// </summary>
    Task<TrailerFitResult> EvaluateAsync(TrailerFitRequest request, CancellationToken ct = default);
}

/// <summary>Capacity of the trailer the loads are being fit into. Any field may be null when Alvys did not supply it.</summary>
public sealed record TrailerCapacitySpec(decimal? MaxWeightLbs, int? MaxPallets, decimal? MaxVolume);

/// <summary>One load's dimensional footprint. Null members are honestly-missing Alvys values, never zero-filled.</summary>
public sealed record TrailerFitItem(string LoadRef, decimal? WeightLbs, int? Pallets, decimal? Volume);

/// <summary>Inputs to a trailer-fit evaluation. All values originate from Alvys reads.</summary>
public sealed record TrailerFitRequest(TrailerCapacitySpec Trailer, IReadOnlyList<TrailerFitItem> Items);

/// <summary>Coarse fit outcome. <see cref="Unknown"/> is used whenever required dimensions are missing.</summary>
public enum TrailerFitVerdict
{
    /// <summary>No fit engine ran (feature disabled) or required dimensions were unavailable.</summary>
    Unknown,
    Fits,
    DoesNotFit,
}

/// <summary>
/// Result of a trailer-fit evaluation, including a plain-language rationale for the UI and the
/// packer/capacity metrics that back the verdict. Metric fields are nullable: they stay null when
/// the fit engine did not run (feature disabled) or degraded (sidecar unreachable/timed out), so the
/// UI never shows a fabricated linear-feet or utilization number. Weight/capacity fields are pure
/// arithmetic over Alvys-supplied values, so they may be populated even when the packer degraded —
/// a combined weight that already exceeds the trailer maximum is a real "does not fit".
/// </summary>
public sealed record TrailerFitResult(
    TrailerFitVerdict Verdict,
    string Rationale,
    DateTimeOffset EvaluatedAt)
{
    /// <summary>
    /// True when the verdict was computed from <i>derived/assumed</i> dimensions (aggregate
    /// pallets/weight/volume expanded into standard 48×40 pallets) rather than real per-item
    /// dims — which are not available from Alvys today. The UI labels these "estimated fit".
    /// </summary>
    public bool EstimatedFit { get; init; }

    /// <summary>Linear feet of trailer floor the plan occupies (packer KPI). Null when the packer did not run.</summary>
    public decimal? LinearFeet { get; init; }

    /// <summary>Weight utilization (0–1): combined weight ÷ trailer max weight. Null when weight is unknown.</summary>
    public decimal? WeightUtilization { get; init; }

    /// <summary>Cube/floor utilization (0–1) from the packer (stacked-cube portion of the trailer). Null when the packer did not run.</summary>
    public decimal? CubeUtilization { get; init; }

    /// <summary>Combined known weight (lbs) across the loads. A floor when <see cref="WeightUnknown"/> is true.</summary>
    public decimal? TotalWeightLbs { get; init; }

    /// <summary>Trailer max payload (lbs) the plan was checked against.</summary>
    public decimal? TrailerMaxWeightLbs { get; init; }

    /// <summary>Combined pallet count across the loads, when derivable. Null when no load carried a pallet/volume signal.</summary>
    public int? TotalPallets { get; init; }

    /// <summary>Trailer pallet positions the plan was checked against.</summary>
    public int? TrailerMaxPallets { get; init; }

    /// <summary>True when the combined weight or pallet count exceeds the trailer capacity (arithmetic over Alvys values).</summary>
    public bool CapacityExceeded { get; init; }

    /// <summary>
    /// True when one or more loads had no weight on file. <see cref="TotalWeightLbs"/> is then a
    /// lower bound and the UI shows "≥ N lb" rather than an exact total.
    /// </summary>
    public bool WeightUnknown { get; init; }
}

/// <summary>
/// No-op <see cref="ITrailerFitService"/> registered when <c>Ltl:Optimization:TrailerFit:Enabled = false</c>
/// (the default). Always returns <see cref="TrailerFitVerdict.Unknown"/> so the caller degrades to
/// "verify at dock" rather than a fabricated fit verdict.
/// </summary>
public sealed class NullTrailerFitService(TimeProvider timeProvider) : ITrailerFitService
{
    public bool IsEnabled => false;

    public Task<TrailerFitResult> EvaluateAsync(TrailerFitRequest request, CancellationToken ct = default)
        => Task.FromResult(new TrailerFitResult(
            TrailerFitVerdict.Unknown,
            "Trailer-fit engine is not enabled — verify fit at the dock.",
            timeProvider.GetUtcNow()));
}
