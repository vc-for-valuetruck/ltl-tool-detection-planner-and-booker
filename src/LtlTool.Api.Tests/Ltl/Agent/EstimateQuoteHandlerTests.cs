using System.Text.Json;
using LtlTool.Api.Features.Ltl.Agent;
using LtlTool.Api.Features.Ltl.Agent.Handlers;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Agent;

public sealed class EstimateQuoteHandlerTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static (EstimateQuoteHandler handler, IncidentStore incidents) Build()
    {
        var estimator = new QuoteEstimatorService(Microsoft.Extensions.Options.Options.Create(new QuoteEstimatorOptions()));
        var incidents = new IncidentStore(
            new FixedTimeProvider(LtlTestFactory.Now), Microsoft.Extensions.Options.Options.Create(new IncidentRiskOptions()));
        return (new EstimateQuoteHandler(estimator, incidents), incidents);
    }

    [Fact]
    public async Task Returns_a_reference_estimate_with_baseline_risk_when_no_incidents()
    {
        var (handler, _) = Build();
        var result = (QuoteEstimate)await handler.HandleAsync(
            Args("{\"origin\":\"TX\",\"destination\":\"TX\",\"weightLbs\":10000,\"distanceMiles\":500}"),
            default);

        Assert.Equal(1579.98m, result.TotalCost);
        Assert.Equal(1.000m, result.SurgeMultiplier);
        Assert.NotNull(result.Risk);
        Assert.Equal(IncidentRiskLevel.Low, result.Risk!.Level);
        Assert.Contains("Reference estimate only", result.Disclaimer);
    }

    [Fact]
    public async Task Applies_corridor_incident_surge_to_the_quote()
    {
        var (handler, incidents) = Build();
        incidents.Report("TX", "IL", severity: 3, note: null, reportedBy: "t"); // surge 1.15

        var result = (QuoteEstimate)await handler.HandleAsync(
            Args("{\"origin\":\"TX\",\"destination\":\"IL\",\"weightLbs\":10000,\"distanceMiles\":500}"),
            default);

        Assert.Equal(1.150m, result.SurgeMultiplier);
        Assert.NotNull(result.Risk);
        Assert.Equal(1, result.Risk!.IncidentCount);
    }

    [Theory]
    [InlineData("{\"origin\":\"\",\"destination\":\"IL\",\"weightLbs\":100}")]
    [InlineData("{\"origin\":\"TX\",\"destination\":\"\",\"weightLbs\":100}")]
    [InlineData("{\"origin\":\"TX\",\"destination\":\"IL\",\"weightLbs\":0}")]
    [InlineData("{\"origin\":\"TX\",\"destination\":\"IL\",\"weightLbs\":100,\"distanceMiles\":-5}")]
    public async Task Invalid_args_throw_validation(string json)
    {
        var (handler, _) = Build();
        await Assert.ThrowsAsync<AgentCommandValidationException>(() => handler.HandleAsync(Args(json), default));
    }

    [Fact]
    public async Task Unresolvable_distance_surfaces_as_validation_not_500()
    {
        var (handler, _) = Build();
        await Assert.ThrowsAsync<AgentCommandValidationException>(() => handler.HandleAsync(
            Args("{\"origin\":\"Narnia\",\"destination\":\"Oz\",\"weightLbs\":100}"), default));
    }
}
