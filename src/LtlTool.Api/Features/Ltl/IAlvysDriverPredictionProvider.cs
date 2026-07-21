namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// The result of consulting Alvys' beta best-driver prediction for a load. When
/// <see cref="Available"/> is false the tool must fall back to its own deterministic factor-based
/// ranking and label the result <see cref="Basis"/> = <c>"AlvysPredictionUnavailable"</c> — never
/// silently substitute one ranking for the other (ROADMAP Phase 2).
/// </summary>
public sealed class AlvysDriverPrediction
{
    /// <summary>Honest "no prediction" result: the Public API does not expose the beta endpoint today.</summary>
    public static readonly AlvysDriverPrediction Unavailable = new();

    public bool Available { get; init; }

    /// <summary>Driver ids ranked best-first by the prediction. Empty when unavailable.</summary>
    public IReadOnlyList<string> RankedDriverIds { get; init; } = [];

    /// <summary>
    /// Provenance label surfaced on every <see cref="MatchResult.PredictionBasis"/>. Defaults to the
    /// honest fallback label so an unconfigured environment is never mislabeled as prediction-backed.
    /// </summary>
    public string Basis { get; init; } = "AlvysPredictionUnavailable";
}

/// <summary>
/// Abstraction over Alvys' beta best-driver prediction. Implemented behind an interface because the
/// prediction is <b>not</b> exposed by the read-only Public API the LTL tool uses today; when it is
/// wired (internal API + confirmed contract) a real provider replaces the null fallback. Callers must
/// never invent an endpoint — an absent prediction returns <see cref="AlvysDriverPrediction.Unavailable"/>.
/// </summary>
public interface IAlvysDriverPredictionProvider
{
    Task<AlvysDriverPrediction> PredictAsync(
        LtlLoadSummary load, IReadOnlyList<MatchCandidate> candidates, CancellationToken ct);
}

/// <summary>
/// Default provider registered until a real prediction contract is wired: always returns
/// <see cref="AlvysDriverPrediction.Unavailable"/> so ranking falls back to the deterministic
/// factor scorer, clearly labeled. Issues no upstream call.
/// </summary>
public sealed class NullAlvysDriverPredictionProvider : IAlvysDriverPredictionProvider
{
    public Task<AlvysDriverPrediction> PredictAsync(
        LtlLoadSummary load, IReadOnlyList<MatchCandidate> candidates, CancellationToken ct)
        => Task.FromResult(AlvysDriverPrediction.Unavailable);
}
