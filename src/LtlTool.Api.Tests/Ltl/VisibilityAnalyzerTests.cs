using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the visibility-history interpreter: failed/errored tracking shares become non-blocking
/// exception flags, noteworthy milestones land in the detail timeline, and an empty history is
/// reported as evaluated-with-nothing rather than fabricated.
/// </summary>
public sealed class VisibilityAnalyzerTests
{
    private static readonly DateTimeOffset T = new(2026, 6, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Failed_status_becomes_a_non_blocking_exception_with_context()
    {
        var inbound = new List<AlvysVisibilityHistoryEvent>
        {
            new()
            {
                LoadNumber = "100", EventType = "Arrival", Status = "Failed",
                SharedAt = T, Destination = "MacroPoint", Reason = "no auth", Error = "401 Unauthorized",
            },
        };

        var exceptions = new VisibilityAnalyzer().DeriveExceptions("100", inbound, []);

        var flag = Assert.Single(exceptions);
        Assert.Equal("VISIBILITY_FAILED", flag.Code);
        Assert.False(flag.BlocksBilling);
        Assert.Contains("load 100", flag.Message);
        Assert.Contains("MacroPoint", flag.Message);
        Assert.Contains("401 Unauthorized", flag.Message);
    }

    [Fact]
    public void Non_empty_error_is_a_failure_even_without_a_failure_status()
    {
        var outbound = new List<AlvysVisibilityHistoryEvent>
        {
            new() { LoadNumber = "100", EventType = "Departure", Status = "Shared", Error = "timeout" },
        };

        var exceptions = new VisibilityAnalyzer().DeriveExceptions("100", [], outbound);

        var flag = Assert.Single(exceptions);
        Assert.Contains("Outbound", flag.Message);
        Assert.Contains("timeout", flag.Message);
    }

    [Fact]
    public void Successful_shares_produce_no_exceptions()
    {
        var inbound = new List<AlvysVisibilityHistoryEvent>
        {
            new() { LoadNumber = "100", EventType = "Delivery", Status = "Shared" },
        };

        Assert.Empty(new VisibilityAnalyzer().DeriveExceptions("100", inbound, []));
    }

    [Fact]
    public void Context_keeps_failures_and_noteworthy_events_newest_first()
    {
        var inbound = new List<AlvysVisibilityHistoryEvent>
        {
            new() { EventType = "Appointment", Status = "Shared", SharedAt = T.AddHours(-2) },
            new() { EventType = "Heartbeat", Status = "Shared", SharedAt = T.AddHours(-1) }, // not noteworthy, not failed
            new() { EventType = "Arrival", Status = "Failed", SharedAt = T },
        };

        var context = new VisibilityAnalyzer().BuildContext(inbound, []);

        Assert.True(context.Evaluated);
        Assert.True(context.HasFailures);
        Assert.Equal(2, context.Events.Count); // heartbeat dropped
        Assert.Equal("Arrival", context.Events[0].EventType); // newest first
        Assert.True(context.Events[0].IsFailure);
    }

    [Fact]
    public void Empty_history_is_evaluated_with_no_events_or_failures()
    {
        var context = new VisibilityAnalyzer().BuildContext([], []);

        Assert.True(context.Evaluated);
        Assert.Empty(context.Events);
        Assert.False(context.HasFailures);
    }
}
