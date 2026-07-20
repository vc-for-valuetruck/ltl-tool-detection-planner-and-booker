using LtlTool.Api.Features.Ltl.Consolidation;

namespace LtlTool.Api.Features.Ltl.Agent.Handlers;

/// <summary>
/// Shared resolution of a <c>planId</c> (a recorded consolidation audit entry) back into a
/// <see cref="ConsolidationPlanRequest"/> so explain-plan / check-fit / sequence-stops can re-derive
/// the plan from live Alvys data. Reused by three handlers so the id → request mapping lives in one
/// place.
/// </summary>
internal static class PlanLookup
{
    /// <summary>
    /// Find the audit record by id and rebuild the plan request that produced it. Throws
    /// <see cref="AgentCommandValidationException"/> (→ 400) when the id is unknown.
    /// </summary>
    public static ConsolidationPlanRequest ResolveRequest(IConsolidationAuditStore audits, string planId)
    {
        if (string.IsNullOrWhiteSpace(planId))
        {
            throw new AgentCommandValidationException("planId is required.");
        }

        var record = audits.All().FirstOrDefault(
            r => string.Equals(r.Id, planId, StringComparison.OrdinalIgnoreCase))
            ?? throw new AgentCommandValidationException(
                $"Unknown planId '{planId}'. Record a consolidation plan audit first.");

        return new ConsolidationPlanRequest
        {
            ParentLoadId = record.ParentLoadId,
            SiblingLoadIds = [.. record.SiblingLoadIds],
            CorridorCode = record.CorridorCode,
        };
    }
}
