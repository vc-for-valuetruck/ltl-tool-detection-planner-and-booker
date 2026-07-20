using System.Text.Json;
using LtlTool.Api.Features.Ltl.Consolidation;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Agent.Handlers;

/// <summary>
/// <c>list-opportunities</c> — thin agent wrapper over <see cref="ConsolidationOpportunityService"/>.
/// Applies the corridor filter locally (by matching each opportunity's origin/destination state to the
/// corridor's configured warehouses); the date window is the service's own lookback. No business logic
/// is duplicated here.
/// </summary>
public sealed class ListOpportunitiesHandler(
    ConsolidationOpportunityService opportunities,
    IOptions<ConsolidationOptions> consolidationOptions) : IAgentCommandHandler
{
    private readonly ConsolidationOptions _opts = consolidationOptions.Value;

    public string Command => AgentCommandCatalog.ListOpportunities;

    public async Task<object> HandleAsync(JsonElement args, CancellationToken ct)
    {
        var request = AgentCommandJson.Deserialize<ListOpportunitiesArgs>(args);
        Validate(request);

        var limit = request.Limit ?? 10;
        var lookbackDays = request.LookbackDays ?? 14;

        var response = await opportunities.FindOpportunitiesAsync(limit, lookbackDays, ct);

        if (string.IsNullOrWhiteSpace(request.Corridor))
        {
            return response;
        }

        var (originState, destinationState) = ResolveCorridorStates(request.Corridor);
        var filtered = response.Opportunities
            .Where(o =>
                (originState is null || string.Equals(o.OriginState, originState, StringComparison.OrdinalIgnoreCase))
                && (destinationState is null || string.Equals(o.DestinationState, destinationState, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        return response with
        {
            Opportunities = filtered,
            TotalPairsFound = filtered.Length,
        };
    }

    private static void Validate(ListOpportunitiesArgs args)
    {
        if (args.Limit is < 1 or > 50)
        {
            throw new AgentCommandValidationException("limit must be between 1 and 50.");
        }
        if (args.LookbackDays is < 1 or > 90)
        {
            throw new AgentCommandValidationException("lookbackDays must be between 1 and 90.");
        }
    }

    private (string? Origin, string? Destination) ResolveCorridorStates(string corridorCode)
    {
        var corridor = _opts.Corridors.FirstOrDefault(
            c => string.Equals(c.Code, corridorCode, StringComparison.OrdinalIgnoreCase))
            ?? throw new AgentCommandValidationException($"Unknown corridor: {corridorCode}");

        var origin = _opts.Warehouses.FirstOrDefault(
            w => string.Equals(w.Code, corridor.OriginWarehouseCode, StringComparison.OrdinalIgnoreCase));
        var destination = _opts.Warehouses.FirstOrDefault(
            w => string.Equals(w.Code, corridor.DestinationWarehouseCode, StringComparison.OrdinalIgnoreCase));

        return (origin?.State, destination?.State);
    }
}
