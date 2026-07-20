namespace LtlTool.Api.Features.Ltl.Optimization;

/// <summary>
/// Phase 2 capacity/cost solver: choose which candidate loads combine onto a trailer subject to
/// weight/cube/pallet capacity while optimizing an objective (minimize deadhead/cost or maximize
/// margin). This is the seam that will replace the deterministic <c>GroupBy</c>/<c>OrderBy</c>
/// ranking in <c>ConsolidationOpportunityService</c>. Selected at startup by
/// <c>Ltl:Optimization:Solver:Enabled</c> — when disabled the <see cref="NullCapacityCostSolver"/>
/// is registered and no solve ever runs.
///
/// <para>Pure compute: all inputs are Alvys-derived. The solver never fetches data itself.</para>
/// </summary>
public interface ICapacityCostSolver
{
    /// <summary>True when a real solver is wired; false for the null implementation.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Solve the capacity/cost problem. Returns a result whose <see cref="CapacityCostResult.Solved"/>
    /// is false when no solver ran or no feasible plan exists — never a fabricated assignment.
    /// </summary>
    Task<CapacityCostResult> SolveAsync(CapacityCostRequest request, CancellationToken ct = default);
}

/// <summary>A load offered to the solver, with its Alvys-derived capacity draw and economics. Nulls are honestly-missing.</summary>
public sealed record CapacityCostCandidate(
    string LoadRef,
    decimal? WeightLbs,
    int? Pallets,
    decimal? Revenue,
    decimal? Miles);

/// <summary>Inputs to a capacity/cost solve: the candidates and the capacity envelope they must fit within.</summary>
public sealed record CapacityCostRequest(
    TrailerCapacitySpec Trailer,
    IReadOnlyList<CapacityCostCandidate> Candidates);

/// <summary>The chosen combination of load refs plus the objective value the solver assigned to it.</summary>
public sealed record CapacityCostPlan(IReadOnlyList<string> SelectedLoadRefs, decimal ObjectiveValue);

/// <summary>Result of a capacity/cost solve. When <see cref="Solved"/> is false, <see cref="Plan"/> is null.</summary>
public sealed record CapacityCostResult(
    bool Solved,
    CapacityCostPlan? Plan,
    string Rationale,
    DateTimeOffset SolvedAt);

/// <summary>
/// No-op <see cref="ICapacityCostSolver"/> registered when <c>Ltl:Optimization:Solver:Enabled = false</c>
/// (the default). Returns an unsolved result so callers fall back to the existing deterministic
/// ranking rather than acting on a fabricated plan.
/// </summary>
public sealed class NullCapacityCostSolver(TimeProvider timeProvider) : ICapacityCostSolver
{
    public bool IsEnabled => false;

    public Task<CapacityCostResult> SolveAsync(CapacityCostRequest request, CancellationToken ct = default)
        => Task.FromResult(new CapacityCostResult(
            Solved: false,
            Plan: null,
            "Capacity/cost solver is not enabled — using deterministic ranking.",
            timeProvider.GetUtcNow()));
}
