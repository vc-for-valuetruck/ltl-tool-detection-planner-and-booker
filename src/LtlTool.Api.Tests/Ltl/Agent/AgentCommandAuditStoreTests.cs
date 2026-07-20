using LtlTool.Api.Features.Ltl.Agent;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Agent;

/// <summary>Tests the in-memory agent-command audit store (args hashed, never stored raw).</summary>
public sealed class AgentCommandAuditStoreTests
{
    private static InMemoryAgentCommandAuditStore Build() =>
        new(new FixedTimeProvider(LtlTestFactory.Now));

    [Fact]
    public void Record_captures_command_user_and_hashes_args()
    {
        var store = Build();
        var raw = "{\"planId\":\"p-1\"}";
        var record = store.Record("explain-plan", raw, ok: true, actingUser: "dispatcher@vt.com");

        Assert.Equal("explain-plan", record.Command);
        Assert.Equal("dispatcher@vt.com", record.ActingUser);
        Assert.True(record.Ok);
        Assert.Equal(LtlTestFactory.Now, record.InvokedAt);
        Assert.False(string.IsNullOrWhiteSpace(record.Id));

        // The stored hash is the SHA-256 of the raw args, not the args themselves.
        Assert.Equal(IAgentCommandAuditStore.HashArgs(raw), record.ArgsHash);
        Assert.DoesNotContain("planId", record.ArgsHash);
    }

    [Fact]
    public void HashArgs_is_deterministic_and_differs_by_payload()
    {
        Assert.Equal(IAgentCommandAuditStore.HashArgs("{\"a\":1}"), IAgentCommandAuditStore.HashArgs("{\"a\":1}"));
        Assert.NotEqual(IAgentCommandAuditStore.HashArgs("{\"a\":1}"), IAgentCommandAuditStore.HashArgs("{\"a\":2}"));
    }

    [Fact]
    public void All_returns_every_recorded_invocation()
    {
        var store = Build();
        store.Record("check-fit", "{}", ok: true, actingUser: "u1");
        store.Record("estimate-quote", "{}", ok: false, actingUser: "u2");

        var all = store.All();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, r => r.Command == "check-fit" && r.Ok);
        Assert.Contains(all, r => r.Command == "estimate-quote" && !r.Ok);
    }
}
