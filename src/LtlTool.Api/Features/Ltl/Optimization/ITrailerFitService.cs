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

/// <summary>Result of a trailer-fit evaluation, including a plain-language rationale for the UI.</summary>
public sealed record TrailerFitResult(
    TrailerFitVerdict Verdict,
    string Rationale,
    DateTimeOffset EvaluatedAt);

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
