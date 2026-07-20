using System.Text.Json;
using LtlTool.Api.Features.Ltl.Agent;
using LtlTool.Api.Features.Ltl.Agent.Handlers;
using LtlTool.Api.Features.Ltl.Consolidation;
using LtlTool.Api.Features.Ltl.Optimization;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Agent;

public sealed class SequenceStopsHandlerTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    // Explicit-stops and validation paths don't use ConsolidationPlanService (only the planId path
    // does); left null here. Audit store is real for the planId lookup test.
    private static SequenceStopsHandler Build() => new(
        new NullStopSequencer(new FixedTimeProvider(LtlTestFactory.Now)),
        new InMemoryConsolidationAuditStore(new FixedTimeProvider(LtlTestFactory.Now)),
        plans: null!);

    [Fact]
    public async Task Explicit_stops_with_sequencer_disabled_preserves_input_order()
    {
        var result = (SequenceStopsResult)await Build().HandleAsync(
            Args("{\"stops\":[{\"stopRef\":\"A\"},{\"stopRef\":\"B\"},{\"stopRef\":\"C\"}]}"), default);

        Assert.False(result.Optimized);
        Assert.Equal(["A", "B", "C"], result.OrderedStopRefs);
        Assert.Contains("not enabled", result.Rationale);
    }

    [Fact]
    public async Task Requires_exactly_one_of_plan_or_stops()
    {
        var handler = Build();
        await Assert.ThrowsAsync<AgentCommandValidationException>(() =>
            handler.HandleAsync(Args("{}"), default));
        await Assert.ThrowsAsync<AgentCommandValidationException>(() =>
            handler.HandleAsync(Args("{\"planId\":\"p-1\",\"stops\":[{\"stopRef\":\"A\"}]}"), default));
    }

    [Fact]
    public async Task Stop_without_a_ref_is_rejected()
    {
        await Assert.ThrowsAsync<AgentCommandValidationException>(() =>
            Build().HandleAsync(Args("{\"stops\":[{\"city\":\"Dallas\"}]}"), default));
    }

    [Fact]
    public async Task Unknown_plan_id_is_a_validation_error()
    {
        await Assert.ThrowsAsync<AgentCommandValidationException>(() =>
            Build().HandleAsync(Args("{\"planId\":\"nope\"}"), default));
    }
}
