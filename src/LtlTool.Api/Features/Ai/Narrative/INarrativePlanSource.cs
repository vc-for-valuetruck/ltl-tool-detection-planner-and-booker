using LtlTool.Api.Features.Ltl.Consolidation;

namespace LtlTool.Api.Features.Ai.Narrative;

/// <summary>
/// Resolves a <c>planId</c> into the minimal, model-facing plan payload. Abstracted so the
/// <see cref="NarrativeService"/> stays unit-testable without constructing the full consolidation
/// stack, and so the "how a plan is fetched" concern lives in one adapter.
/// </summary>
public interface INarrativePlanSource
{
    /// <summary>
    /// Returns the plan payload for a recorded plan id, or <c>null</c> when the id is unknown /
    /// blank. Read-only against Alvys. May surface as <c>null</c> (via the caller's fail-closed
    /// wrapper) if the plan cannot be rebuilt.
    /// </summary>
    Task<NarrativePlanPayload?> GetPlanPayloadAsync(string planId, CancellationToken ct);
}

/// <summary>
/// Default <see cref="INarrativePlanSource"/>: a <c>planId</c> is a recorded consolidation-plan
/// audit entry (same convention as the agent's plan-lookup). It rebuilds the plan from live Alvys
/// data via <see cref="ConsolidationPlanService"/> and projects it to <see cref="NarrativePlanPayload"/>.
/// Purely read-only — the audit store is read, never written, and no Alvys writeback occurs.
/// </summary>
public sealed class ConsolidationNarrativePlanSource(
    IConsolidationAuditStore audits,
    ConsolidationPlanService plans) : INarrativePlanSource
{
    private readonly IConsolidationAuditStore _audits = audits;
    private readonly ConsolidationPlanService _plans = plans;

    public async Task<NarrativePlanPayload?> GetPlanPayloadAsync(string planId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(planId)) return null;

        var record = _audits.All().FirstOrDefault(
            r => string.Equals(r.Id, planId, StringComparison.OrdinalIgnoreCase));
        if (record is null) return null;

        var plan = await _plans.BuildAsync(
            new ConsolidationPlanRequest
            {
                ParentLoadId = record.ParentLoadId,
                SiblingLoadIds = [.. record.SiblingLoadIds],
                CorridorCode = record.CorridorCode,
            },
            ct);

        return Map(planId, plan);
    }

    private static NarrativePlanPayload Map(string planId, ConsolidationPlanResponse plan) => new()
    {
        PlanId = planId,
        CorridorCode = plan.CorridorCode,
        ParentLoadNumber = plan.Parent.LoadNumber,
        ParentCustomerName = plan.Parent.CustomerName,
        ParentOrigin = plan.Parent.Origin?.Label,
        ParentDestination = plan.Parent.Destination?.Label,
        Siblings = plan.Siblings.Select(s => new NarrativePlanSibling
        {
            LoadId = s.LoadId,
            LoadNumber = s.LoadNumber,
            CustomerName = s.CustomerName,
            DestinationLabel = s.DestinationLabel,
            Revenue = s.Revenue,
            WeightLbs = s.WeightLbs,
        }).ToArray(),
        CombinedRevenue = plan.CombinedRevenue,
        LinehaulMiles = plan.LinehaulMiles,
        DriverLoadedMiles = plan.DriverLoadedMiles,
        CombinedDriverTripValue = plan.CombinedDriverTripValue,
        CombinedRevenuePerMile = plan.CombinedRevenuePerMile,
        RpmWarningStatus = plan.RpmWarning?.Status.ToString(),
        RpmWarningMessage = plan.RpmWarning?.Message,
        TrailerFitVerdict = plan.TrailerFit?.Verdict,
        Blockers = plan.Blockers.ToArray(),
        StopSequence = plan.StopSequence.ToArray(),
    };
}
