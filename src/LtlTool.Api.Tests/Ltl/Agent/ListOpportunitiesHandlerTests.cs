using System.Text.Json;
using LtlTool.Api.Features.Ltl.Agent;
using LtlTool.Api.Features.Ltl.Agent.Handlers;
using LtlTool.Api.Features.Ltl.Consolidation;
using LtlTool.Api.Features.Ltl.Optimization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Agent;

public sealed class ListOpportunitiesHandlerTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static ListOpportunitiesHandler Build(ConsolidationOptions? consolidation = null)
    {
        var opportunities = new ConsolidationOpportunityService(
            new FakeAlvysClient(), // empty corpus → empty opportunity set
            new FixedTimeProvider(LtlTestFactory.Now),
            new NullCapacityCostSolver(new FixedTimeProvider(LtlTestFactory.Now)),
            Microsoft.Extensions.Options.Options.Create(new CapacityCostSolverOptions()),
            NullLogger<ConsolidationOpportunityService>.Instance);

        return new ListOpportunitiesHandler(
            opportunities, Microsoft.Extensions.Options.Options.Create(consolidation ?? new ConsolidationOptions()));
    }

    [Theory]
    [InlineData("{\"limit\":0}")]
    [InlineData("{\"limit\":51}")]
    [InlineData("{\"lookbackDays\":0}")]
    [InlineData("{\"lookbackDays\":91}")]
    public async Task Out_of_range_bounds_throw_validation(string json)
    {
        await Assert.ThrowsAsync<AgentCommandValidationException>(() =>
            Build().HandleAsync(Args(json), default));
    }

    [Fact]
    public async Task Unknown_corridor_filter_is_a_validation_error()
    {
        await Assert.ThrowsAsync<AgentCommandValidationException>(() =>
            Build().HandleAsync(Args("{\"corridor\":\"NOT_A_CORRIDOR\"}"), default));
    }

    [Fact]
    public async Task No_corridor_filter_returns_the_service_response_as_is()
    {
        var result = (ConsolidationOpportunitiesResponse)await Build().HandleAsync(Args("{}"), default);
        Assert.Empty(result.Opportunities);
    }
}
