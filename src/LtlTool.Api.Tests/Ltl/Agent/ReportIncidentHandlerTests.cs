using System.Text.Json;
using LtlTool.Api.Features.Ltl.Agent;
using LtlTool.Api.Features.Ltl.Agent.Handlers;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Agent;

public sealed class ReportIncidentHandlerTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static (ReportIncidentHandler handler, IncidentStore incidents) Build()
    {
        var incidents = new IncidentStore(
            new FixedTimeProvider(LtlTestFactory.Now), Microsoft.Extensions.Options.Options.Create(new IncidentRiskOptions()));
        return (new ReportIncidentHandler(incidents), incidents);
    }

    [Fact]
    public async Task Records_incident_and_returns_updated_corridor_risk()
    {
        var (handler, incidents) = Build();
        var risk = (CorridorRisk)await handler.HandleAsync(
            Args("{\"origin\":\"TX\",\"destination\":\"IL\",\"severity\":3,\"note\":\"flooding\"}"),
            default);

        Assert.Equal(1, risk.IncidentCount);
        Assert.Equal(1.150m, risk.SurgeMultiplier);
        Assert.Equal("flooding", risk.LatestNote);

        // The incident is persisted in the store, visible to a later GetRisk.
        Assert.Equal(1, incidents.GetRisk("TX", "IL").IncidentCount);
    }

    [Theory]
    [InlineData("{\"origin\":\"\",\"destination\":\"IL\",\"severity\":3}")]
    [InlineData("{\"origin\":\"TX\",\"destination\":\"\",\"severity\":3}")]
    [InlineData("{\"origin\":\"TX\",\"destination\":\"IL\",\"severity\":0}")]
    [InlineData("{\"origin\":\"TX\",\"destination\":\"IL\",\"severity\":6}")]
    public async Task Invalid_args_throw_validation(string json)
    {
        var (handler, _) = Build();
        await Assert.ThrowsAsync<AgentCommandValidationException>(() => handler.HandleAsync(Args(json), default));
    }
}
