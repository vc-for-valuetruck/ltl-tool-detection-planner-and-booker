using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl.Notifications;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Agents;

/// <summary>
/// Background agent (default every 120s) that sweeps the read-only exception worklist
/// (<see cref="LtlLoadService.ExceptionsAsync"/>) and raises one in-app notification per load that
/// carries open exceptions. Dedupe is per load number, for the lifetime of the store — the goal is a
/// single "this load needs attention" ping, not a re-fire on every poll.
///
/// <para>
/// Overlap note: the existing <see cref="NotificationTriggerEngine"/> also reads the exception
/// worklist, but it fires the fine-grained trip-stop signals (predicted-late / late-delivery /
/// stuck-stop) keyed on load+stop+window. This agent fires a distinct, coarser per-load summary and
/// namespaces its <c>SourceKey</c> as <c>agent-exc:{loadNumber}</c> so the two never collide in the
/// idempotency store.
/// </para>
///
/// <para>Alvys posture: read-only. No Alvys writes. Alvys unavailable → 'degraded' heartbeat, no notification.</para>
/// </summary>
public sealed class ExceptionSweeperService(
    IServiceProvider services,
    TimeProvider clock,
    IOptions<AgentsOptions> options,
    ILogger<ExceptionSweeperService> logger)
    : AgentBackgroundService(services, clock, logger)
{
    public const string Name = "exception-sweeper";

    private readonly ExceptionSweeperOptions _options = options.Value.ExceptionSweeper;

    public override string AgentName => Name;
    public override bool Enabled => _options.Enabled;
    protected override TimeSpan Interval => TimeSpan.FromSeconds(_options.IntervalSeconds);

    protected override async Task<AgentSweepResult> SweepAsync(IServiceProvider scope, CancellationToken ct)
    {
        var alvys = scope.GetRequiredService<IAlvysClient>();
        var probe = await alvys.SearchLoadsAsync(page: 1, pageSize: 1, ct: ct);
        if (probe is null)
        {
            return AgentSweepResult.Degraded("AlvysNullResponse");
        }

        var loads = scope.GetRequiredService<LtlLoadService>();
        var dispatcher = scope.GetRequiredService<NotificationDispatcher>();

        var exceptionLoads = await loads.ExceptionsAsync(ct);
        foreach (var load in exceptionLoads)
        {
            ct.ThrowIfCancellationRequested();

            // A load with no stable identifier cannot be deduped safely; skip rather than risk a re-fire storm.
            if (string.IsNullOrWhiteSpace(load.LoadNumber))
            {
                continue;
            }

            await dispatcher.DispatchAsync(ToTrigger(load), ct);
        }

        return AgentSweepResult.Healthy(exceptionLoads.Count);
    }

    /// <summary>
    /// Maps an exception-bearing load to a per-load <see cref="NotificationStage.ExceptionRaised"/>
    /// trigger. <c>SourceKey</c> is <c>agent-exc:{loadNumber}</c> and <c>OccurredAt</c> is the Unix
    /// epoch (a fixed sentinel), so the dispatcher idempotency key is one-per-load-forever — a load
    /// that stays in exception fires exactly once. Exposed for unit testing.
    /// </summary>
    public static NotificationTrigger ToTrigger(LtlLoadSummary load)
    {
        var loadLabel = load.LoadNumber!;
        var codes = load.Exceptions.Count == 0
            ? null
            : string.Join(", ", load.Exceptions.Take(3).Select(e => e.Code));
        var lane = FormatLane(load);

        var summary = $"Load {loadLabel}{lane} has {load.Exceptions.Count} open exception(s)"
            + (codes is null ? "." : $": {codes}.");

        return new NotificationTrigger
        {
            Stage = NotificationStage.ExceptionRaised,
            SourceKey = $"agent-exc:{loadLabel}",
            Title = $"Exception · {loadLabel}",
            Summary = summary,
            LoadId = load.Id,
            LoadNumber = load.LoadNumber,
            LinkPath = $"/ltl/loads/{Uri.EscapeDataString(loadLabel)}",
            OccurredAt = DateTimeOffset.UnixEpoch,
        };
    }

    private static string FormatLane(LtlLoadSummary load)
    {
        var origin = load.Origin?.Label;
        var destination = load.Destination?.Label;
        if (origin is not null && destination is not null) return $" ({origin} → {destination})";
        if (origin is not null) return $" (from {origin})";
        if (destination is not null) return $" (to {destination})";
        return string.Empty;
    }
}
