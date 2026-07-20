using System.Text.Json;

namespace LtlTool.Api.Features.Ltl.Agent.Handlers;

/// <summary>
/// <c>estimate-quote</c> — a reference-only cost/CO₂ estimate for a lane via
/// <see cref="QuoteEstimatorService"/>, risk-adjusted by any incidents recorded for the corridor in
/// <see cref="IncidentStore"/>. Every value is a rate-card estimate, never an Alvys rate or mileage;
/// the response carries that disclaimer verbatim.
/// </summary>
public sealed class EstimateQuoteHandler(
    QuoteEstimatorService estimator,
    IncidentStore incidents) : IAgentCommandHandler
{
    public string Command => AgentCommandCatalog.EstimateQuote;

    public Task<object> HandleAsync(JsonElement args, CancellationToken ct)
    {
        var request = AgentCommandJson.Deserialize<EstimateQuoteArgs>(args);
        Validate(request);

        var input = new QuoteEstimateInput
        {
            Origin = request.Origin,
            Destination = request.Destination,
            WeightLbs = request.WeightLbs,
            Mode = request.Mode,
            DistanceMiles = request.DistanceMiles,
            Perishable = request.Perishable,
            Hazmat = request.Hazmat,
        };

        var risk = incidents.GetRisk(request.Origin, request.Destination);

        QuoteEstimate estimate;
        try
        {
            estimate = estimator.Estimate(input, (double)risk.SurgeMultiplier);
        }
        catch (ArgumentException ex)
        {
            // Unresolvable distance / non-positive weight are caller errors → 400, not 500.
            throw new AgentCommandValidationException(ex.Message);
        }

        return Task.FromResult<object>(estimate with { Risk = risk });
    }

    private static void Validate(EstimateQuoteArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Origin) || string.IsNullOrWhiteSpace(args.Destination))
        {
            throw new AgentCommandValidationException("origin and destination are required.");
        }
        if (args.WeightLbs <= 0)
        {
            throw new AgentCommandValidationException("weightLbs must be positive.");
        }
        if (args.DistanceMiles is <= 0)
        {
            throw new AgentCommandValidationException("distanceMiles, when supplied, must be positive.");
        }
    }
}
