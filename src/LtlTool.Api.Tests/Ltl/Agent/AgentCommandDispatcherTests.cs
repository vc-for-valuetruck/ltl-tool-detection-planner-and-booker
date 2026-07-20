using System.Text.Json;
using LtlTool.Api.Features.Ltl.Agent;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Agent;

/// <summary>
/// Tests the dispatcher's routing, auditing, and error mapping in isolation from the real handlers,
/// using a stub handler bound to a real catalog command name.
/// </summary>
public sealed class AgentCommandDispatcherTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private sealed class StubHandler(string command, Func<object> body) : IAgentCommandHandler
    {
        public string Command { get; } = command;
        public Task<object> HandleAsync(JsonElement args, CancellationToken ct) => Task.FromResult(body());
    }

    private static (AgentCommandDispatcher dispatcher, InMemoryAgentCommandAuditStore audits) Build(
        IAgentCommandHandler handler)
    {
        var audits = new InMemoryAgentCommandAuditStore(new FixedTimeProvider(LtlTestFactory.Now));
        var dispatcher = new AgentCommandDispatcher(
            [handler], audits, new FixedTimeProvider(LtlTestFactory.Now));
        return (dispatcher, audits);
    }

    [Fact]
    public async Task Successful_dispatch_returns_ok_and_audits_the_invocation()
    {
        var (dispatcher, audits) = Build(
            new StubHandler(AgentCommandCatalog.EstimateQuote, () => new { value = 42 }));

        var result = await dispatcher.DispatchAsync(
            AgentCommandCatalog.EstimateQuote, Args("{\"origin\":\"TX\"}"), "u@vt.com", default);

        Assert.True(result.Ok);
        Assert.Equal(AgentCommandCatalog.EstimateQuote, result.Command);
        Assert.NotNull(result.Data);

        var row = Assert.Single(audits.All());
        Assert.True(row.Ok);
        Assert.Equal("u@vt.com", row.ActingUser);
        Assert.Equal(AgentCommandCatalog.EstimateQuote, row.Command);
    }

    [Fact]
    public async Task Unknown_command_throws_and_is_not_audited()
    {
        var (dispatcher, audits) = Build(
            new StubHandler(AgentCommandCatalog.EstimateQuote, () => new { }));

        await Assert.ThrowsAsync<UnknownAgentCommandException>(() =>
            dispatcher.DispatchAsync("no-such-command", Args("{}"), "u", default));

        Assert.Empty(audits.All());
    }

    [Fact]
    public async Task Validation_failure_is_audited_as_not_ok_and_rethrown()
    {
        var (dispatcher, audits) = Build(new StubHandler(
            AgentCommandCatalog.EstimateQuote,
            () => throw new AgentCommandValidationException("bad args")));

        await Assert.ThrowsAsync<AgentCommandValidationException>(() =>
            dispatcher.DispatchAsync(AgentCommandCatalog.EstimateQuote, Args("{}"), "u", default));

        var row = Assert.Single(audits.All());
        Assert.False(row.Ok);
    }

    [Fact]
    public async Task Null_dispatcher_is_disabled_but_still_serves_the_catalog()
    {
        var dispatcher = new NullAgentCommandDispatcher();
        Assert.False(dispatcher.IsEnabled);
        Assert.Equal(AgentCommandCatalog.All.Count, dispatcher.Catalog.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DispatchAsync("estimate-quote", default, "u", default));
    }
}
