using System.Collections.Concurrent;
using LtlTool.Api.Features.Ltl.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Tests.Ltl.Notifications;

/// <summary>
/// A scriptable <see cref="IGraphMailClient"/> for channel state-machine tests. Enqueue the outcomes
/// each successive <see cref="SendAsync"/> should return; records how many times it was invoked so a
/// test can assert "no duplicate send" on an idempotent replay.
/// </summary>
public sealed class FakeGraphMailClient : IGraphMailClient
{
    private readonly ConcurrentQueue<GraphMailSendOutcome> _outcomes = new();
    private readonly GraphMailSendOutcome _default;

    public FakeGraphMailClient(GraphMailSendOutcome? @default = null, params GraphMailSendOutcome[] scripted)
    {
        _default = @default ?? GraphMailSendOutcome.Sent();
        foreach (var o in scripted) _outcomes.Enqueue(o);
    }

    public int SendCount { get; private set; }

    public Task<GraphMailSendOutcome> SendAsync(GraphMailMessage message, CancellationToken ct)
    {
        SendCount++;
        var outcome = _outcomes.TryDequeue(out var next) ? next : _default;
        return Task.FromResult(outcome);
    }
}

/// <summary>Factory for a Graph-backed email channel wired with test doubles and zero backoff delay.</summary>
public static class NotificationTestFactory
{
    public static EmailChannelOptions ConfiguredEmail() => new()
    {
        Enabled = true,
        FromAddress = "dispatch@valuetruck.com",
        MaxSendAttempts = 3,
        RetryBaseDelayMs = 0,
        Graph = new GraphMailOptions
        {
            TenantId = "00000000-0000-0000-0000-000000000000",
            ClientId = "11111111-1111-1111-1111-111111111111",
            ClientSecret = "secret",
        },
    };

    public static EmailNotificationChannel EmailChannel(
        IGraphMailClient graph, IMailOutbox outbox, EmailChannelOptions? email = null) =>
        new(
            graph,
            outbox,
            Options.Create(new NotificationOptions { Email = email ?? ConfiguredEmail() }),
            TimeProvider.System,
            NullLogger<EmailNotificationChannel>.Instance);

    /// <summary>An unconfigured (disabled) email channel — the dock/CI default. Never calls Graph.</summary>
    public static EmailNotificationChannel UnconfiguredEmailChannel() =>
        EmailChannel(new FakeGraphMailClient(), new InMemoryMailOutbox(), new EmailChannelOptions());
}
