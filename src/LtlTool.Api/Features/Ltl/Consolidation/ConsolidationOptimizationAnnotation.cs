namespace LtlTool.Api.Features.Ltl.Consolidation;

/// <summary>
/// Solver-derived annotation attached to a consolidation opportunity when
/// <c>Ltl:Optimization:Solver:Enabled</c> is on. The GROUP BY heuristic still generates the
/// opportunity (candidate generation); the OR-Tools capacity/cost solver then decides which
/// siblings actually fit a standard trailer and prices the trade, letting the queue be re-ranked by
/// real captured uplift instead of raw linehaul sums. Every field is derived from the same
/// Alvys-sourced loads the opportunity already carries — nothing new is fetched or invented.
/// </summary>
public sealed record ConsolidationOptimizationAnnotation
{
    /// <summary>Sibling load numbers the solver kept on the trailer within the capacity envelope.</summary>
    public required IReadOnlyList<string> SelectedSiblingLoadNumbers { get; init; }

    /// <summary>Sibling load numbers the solver dropped because they did not fit / did not pay.</summary>
    public required IReadOnlyList<string> DroppedSiblingLoadNumbers { get; init; }

    /// <summary>Revenue actually captured by the solver-selected siblings (the re-ranking key).</summary>
    public required decimal CapturedUplift { get; init; }

    /// <summary>The solver's objective value (routing + fixed cost + drop penalties). Lower is better.</summary>
    public required decimal ObjectiveValue { get; init; }

    /// <summary>Plain-language capacity/cost/estimate rationale for the UI.</summary>
    public required string Rationale { get; init; }

    /// <summary>
    /// True when the capacity envelope came from a standard-trailer planning assumption rather than
    /// a specific assigned Alvys trailer, so the UI can label the ranking as an estimate.
    /// </summary>
    public required bool UsedAssumedTrailer { get; init; }
}
