using System.Text.Json;
using LtlTool.Api.Features.Ltl.Agent;
using LtlTool.Api.Features.Ltl.Agent.Handlers;
using LtlTool.Api.Features.Ltl.Consolidation;
using LtlTool.Api.Features.Ltl.Optimization;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Agent;

public sealed class CheckFitHandlerTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    // The explicit-loads and validation paths never touch ConsolidationPlanService (only the planId
    // path does), so it is left null for these unit tests; the audit store is real for planId lookup.
    private static CheckFitHandler Build() => new(
        new NullTrailerFitService(new FixedTimeProvider(LtlTestFactory.Now)),
        new InMemoryConsolidationAuditStore(new FixedTimeProvider(LtlTestFactory.Now)),
        plans: null!);

    [Fact]
    public async Task Explicit_loads_with_fit_engine_disabled_returns_honest_unknown()
    {
        var result = (CheckFitResult)await Build().HandleAsync(
            Args("{\"loads\":[{\"loadRef\":\"L-1\",\"weightLbs\":5000}]}"), default);

        Assert.False(result.Enabled);
        Assert.Equal(TrailerFitVerdict.Unknown.ToString(), result.Verdict);
        Assert.Contains("verify fit at the dock", result.Rationale);
    }

    [Fact]
    public async Task Requires_exactly_one_of_plan_or_loads()
    {
        var handler = Build();
        // Neither.
        await Assert.ThrowsAsync<AgentCommandValidationException>(() =>
            handler.HandleAsync(Args("{}"), default));
        // Both.
        await Assert.ThrowsAsync<AgentCommandValidationException>(() =>
            handler.HandleAsync(Args("{\"planId\":\"p-1\",\"loads\":[{\"loadRef\":\"L-1\"}]}"), default));
    }

    [Fact]
    public async Task Load_without_a_ref_is_rejected()
    {
        await Assert.ThrowsAsync<AgentCommandValidationException>(() =>
            Build().HandleAsync(Args("{\"loads\":[{\"weightLbs\":100}]}"), default));
    }

    [Fact]
    public async Task Unknown_plan_id_is_a_validation_error()
    {
        await Assert.ThrowsAsync<AgentCommandValidationException>(() =>
            Build().HandleAsync(Args("{\"planId\":\"does-not-exist\"}"), default));
    }
}
