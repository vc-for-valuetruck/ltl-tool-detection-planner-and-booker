using System.Text;
using System.Text.Json;
using LtlTool.Api.Features.Ltl.Consolidation;

namespace LtlTool.Api.Features.Ltl.Agent.Handlers;

/// <summary>
/// <c>explain-plan</c> — re-derives a recorded plan from live Alvys data and explains it: a plain-
/// language solver rationale, the trailer-fit verdict, and the per-sibling Lane/Timing/Customer fit
/// chips reused verbatim from <see cref="ConsolidationCandidateService"/> so the agent explanation and
/// the workbench chips never drift apart.
/// </summary>
public sealed class ExplainPlanHandler(
    IConsolidationAuditStore audits,
    ConsolidationPlanService plans,
    ConsolidationCandidateService candidates) : IAgentCommandHandler
{
    public string Command => AgentCommandCatalog.ExplainPlan;

    public async Task<object> HandleAsync(JsonElement args, CancellationToken ct)
    {
        var request = AgentCommandJson.Deserialize<ExplainPlanArgs>(args);
        var planRequest = PlanLookup.ResolveRequest(audits, request.PlanId);

        var plan = await plans.BuildAsync(planRequest, ct);

        // Pull the Lane/Timing/Customer chips for each sibling from the candidate service, keyed by
        // load id. One extra Alvys sweep is acceptable for an explain (low-frequency) command and it
        // guarantees the chip language is identical to the workbench.
        var chipsByLoadId = await BuildChipIndexAsync(planRequest, ct);

        var siblings = plan.Siblings.Select(s => new ExplainPlanSibling
        {
            LoadRef = s.LoadNumber ?? s.LoadId,
            CustomerName = s.CustomerName,
            Chips = chipsByLoadId.TryGetValue(s.LoadId, out var chips) ? chips : [],
            Cautions = s.Cautions,
        }).ToArray();

        return new ExplainPlanResult
        {
            PlanId = request.PlanId,
            CorridorCode = plan.CorridorCode,
            ParentLoadRef = plan.Parent.LoadNumber ?? plan.Parent.Id,
            Siblings = siblings,
            SolverRationale = BuildRationale(plan),
            TrailerFitVerdict = plan.TrailerFit?.Verdict ?? "Unknown",
            TrailerFitRationale = plan.TrailerFit?.Rationale
                ?? "Trailer-fit engine disabled — verify fit at the dock.",
            CombinedRevenue = plan.CombinedRevenue,
            CombinedDriverRpm = plan.CombinedRevenuePerMile,
            StopsOptimized = plan.StopsOptimized,
            Blockers = plan.Blockers,
        };
    }

    private async Task<Dictionary<string, IReadOnlyList<ConsolidationFactor>>> BuildChipIndexAsync(
        ConsolidationPlanRequest planRequest, CancellationToken ct)
    {
        var index = new Dictionary<string, IReadOnlyList<ConsolidationFactor>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var corridor = string.IsNullOrWhiteSpace(planRequest.CorridorCode)
                ? "LAREDO_TO_DALLAS"
                : planRequest.CorridorCode;
            var candidateResponse = await candidates.GetCandidatesAsync(planRequest.ParentLoadId, corridor, ct);
            foreach (var c in candidateResponse.Candidates)
            {
                index[c.LoadId] = c.Factors;
            }
        }
        catch (InvalidOperationException)
        {
            // A candidate-sweep failure (e.g. unknown corridor) must not sink the explanation — the
            // plan itself already resolved. Siblings simply carry no chips in that degraded case.
        }
        return index;
    }

    private static string BuildRationale(ConsolidationPlanResponse plan)
    {
        var sb = new StringBuilder();
        sb.Append($"Plan combines parent {plan.Parent.LoadNumber ?? plan.Parent.Id} with ")
          .Append(plan.Siblings.Count)
          .Append(plan.Siblings.Count == 1 ? " sibling" : " siblings")
          .Append(" on the ").Append(plan.CorridorCode).Append(" corridor. ");

        sb.Append(plan.StopsOptimized
            ? "Stop order was optimized by the sequencer. "
            : "Stop order preserved as-is (no stop coordinates to optimize). ");

        if (plan.TrailerFit is not null)
        {
            sb.Append($"Trailer fit: {plan.TrailerFit.Verdict}");
            if (plan.TrailerFit.CapacityExceeded) sb.Append(" (capacity exceeded)");
            sb.Append(". ");
        }
        else
        {
            sb.Append("Trailer fit not evaluated (engine disabled). ");
        }

        if (plan.CombinedRevenuePerMile is not null)
        {
            sb.Append($"Combined driver RPM ${plan.CombinedRevenuePerMile:N2}/mi. ");
        }

        sb.Append(plan.Blockers.Count == 0
            ? "No blockers — plan is executable."
            : $"{plan.Blockers.Count} blocker(s) present — plan is not executable as-is.");

        return sb.ToString();
    }
}
