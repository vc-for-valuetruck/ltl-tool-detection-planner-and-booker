using System.Text.Json;
using LtlTool.Api.Features.Ltl.Consolidation;
using LtlTool.Api.Features.Ltl.Optimization;

namespace LtlTool.Api.Features.Ltl.Agent.Handlers;

/// <summary>
/// <c>sequence-stops</c> — orders a plan's stops (or an explicit stop list) via
/// <see cref="IStopSequencer"/>. When the sequencer is disabled or has no coordinates to work with it
/// preserves input order and reports <c>optimized = false</c> honestly.
/// </summary>
public sealed class SequenceStopsHandler(
    IStopSequencer sequencer,
    IConsolidationAuditStore audits,
    ConsolidationPlanService plans) : IAgentCommandHandler
{
    public string Command => AgentCommandCatalog.SequenceStops;

    public async Task<object> HandleAsync(JsonElement args, CancellationToken ct)
    {
        var request = AgentCommandJson.Deserialize<SequenceStopsArgs>(args);
        Validate(request);

        IReadOnlyList<StopToSequence> stops;
        if (!string.IsNullOrWhiteSpace(request.PlanId))
        {
            var planRequest = PlanLookup.ResolveRequest(audits, request.PlanId);
            var plan = await plans.BuildAsync(planRequest, ct);
            var built = new List<StopToSequence>
            {
                new(plan.Parent.Id, plan.Parent.Destination?.City, plan.Parent.Destination?.State, null, null),
            };
            built.AddRange(plan.Siblings.Select(s =>
            {
                var city = s.DestinationLabel?.Split(',')[0].Trim();
                return new StopToSequence(s.LoadId, city, null, null, null);
            }));
            stops = built;
        }
        else
        {
            stops = request.Stops!
                .Select(s => new StopToSequence(s.StopRef, s.City, s.State, s.Latitude, s.Longitude))
                .ToArray();
        }

        var result = await sequencer.SequenceAsync(new StopSequenceRequest(stops), ct);

        return new SequenceStopsResult
        {
            OrderedStopRefs = result.OrderedStopRefs,
            Optimized = result.Optimized,
            Rationale = result.Rationale,
        };
    }

    private static void Validate(SequenceStopsArgs args)
    {
        var hasPlan = !string.IsNullOrWhiteSpace(args.PlanId);
        var hasStops = args.Stops is { Count: > 0 };
        if (hasPlan == hasStops)
        {
            throw new AgentCommandValidationException(
                "Provide exactly one of planId or a non-empty stops list.");
        }
        if (hasStops && args.Stops!.Any(s => string.IsNullOrWhiteSpace(s.StopRef)))
        {
            throw new AgentCommandValidationException("Every stop requires a stopRef.");
        }
    }
}
