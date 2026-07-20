using System.Text.Json;
using LtlTool.Api.Features.Ltl.Consolidation;
using LtlTool.Api.Features.Ltl.Optimization;

namespace LtlTool.Api.Features.Ltl.Agent.Handlers;

/// <summary>
/// <c>check-fit</c> — runs <see cref="ITrailerFitService"/> over either a recorded plan's loads
/// (resolved live from Alvys) or an explicit caller-supplied load list. When the fit engine is
/// disabled the service returns an honest <c>Unknown</c> ("verify at dock") rather than a fabricated
/// verdict; this handler surfaces that verbatim.
/// </summary>
public sealed class CheckFitHandler(
    ITrailerFitService trailerFit,
    IConsolidationAuditStore audits,
    ConsolidationPlanService plans) : IAgentCommandHandler
{
    public string Command => AgentCommandCatalog.CheckFit;

    public async Task<object> HandleAsync(JsonElement args, CancellationToken ct)
    {
        var request = AgentCommandJson.Deserialize<CheckFitArgs>(args);
        Validate(request);

        IReadOnlyList<TrailerFitItem> items;
        if (!string.IsNullOrWhiteSpace(request.PlanId))
        {
            var planRequest = PlanLookup.ResolveRequest(audits, request.PlanId);
            var plan = await plans.BuildAsync(planRequest, ct);
            var built = new List<TrailerFitItem>
            {
                new(plan.Parent.LoadNumber ?? plan.Parent.Id, plan.Parent.WeightLbs, null, plan.Parent.Volume),
            };
            built.AddRange(plan.Siblings.Select(s =>
                new TrailerFitItem(s.LoadNumber ?? s.LoadId, s.WeightLbs, null, null)));
            items = built;
        }
        else
        {
            items = request.Loads!
                .Select(l => new TrailerFitItem(l.LoadRef, l.WeightLbs, l.Pallets, l.Volume))
                .ToArray();
        }

        var trailer = new TrailerCapacitySpec(
            request.Trailer?.MaxWeightLbs,
            request.Trailer?.MaxPallets,
            request.Trailer?.MaxVolume);

        var result = await trailerFit.EvaluateAsync(new TrailerFitRequest(trailer, items), ct);

        return new CheckFitResult
        {
            Enabled = trailerFit.IsEnabled,
            Verdict = result.Verdict.ToString(),
            Rationale = result.Rationale,
            EstimatedFit = result.EstimatedFit,
            TotalWeightLbs = result.TotalWeightLbs,
            TrailerMaxWeightLbs = result.TrailerMaxWeightLbs,
            TotalPallets = result.TotalPallets,
            TrailerMaxPallets = result.TrailerMaxPallets,
            CapacityExceeded = result.CapacityExceeded,
            WeightUnknown = result.WeightUnknown,
            LinearFeet = result.LinearFeet,
            WeightUtilization = result.WeightUtilization,
            CubeUtilization = result.CubeUtilization,
        };
    }

    private static void Validate(CheckFitArgs args)
    {
        var hasPlan = !string.IsNullOrWhiteSpace(args.PlanId);
        var hasLoads = args.Loads is { Count: > 0 };
        if (hasPlan == hasLoads)
        {
            throw new AgentCommandValidationException(
                "Provide exactly one of planId or a non-empty loads list.");
        }
        if (hasLoads && args.Loads!.Any(l => string.IsNullOrWhiteSpace(l.LoadRef)))
        {
            throw new AgentCommandValidationException("Every load requires a loadRef.");
        }
    }
}
