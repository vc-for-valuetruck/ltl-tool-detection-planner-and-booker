namespace LtlTool.Api.Features.Ltl.Consolidation;

/// <summary>
/// Thrown when a caller tries to commit an action (record an audit, combine at the dock) against a
/// plan that carries hard <see cref="ConsolidationPlanResponse.Blockers"/>. Mirrors the Phase 3
/// assignment semantics: a blocked plan must NOT be recorded — the commit path fails closed
/// (surfaced as HTTP 422) and writes nothing. The offending <see cref="Plan"/> is carried so the
/// API can echo the blockers back to the UI, which surfaces them at the review step.
/// </summary>
public sealed class ConsolidationPlanBlockedException(ConsolidationPlanResponse plan)
    : InvalidOperationException(BuildMessage(plan))
{
    /// <summary>The blocked plan preview — its <see cref="ConsolidationPlanResponse.Blockers"/> are non-empty.</summary>
    public ConsolidationPlanResponse Plan { get; } = plan;

    private static string BuildMessage(ConsolidationPlanResponse plan) =>
        "Plan has blockers and cannot be combined: " + string.Join("; ", plan.Blockers);
}
