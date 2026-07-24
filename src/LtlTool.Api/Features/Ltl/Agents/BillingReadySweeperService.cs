using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl.Notifications;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Agents;

/// <summary>
/// Background agent (default every 180s) that sweeps the billing worklist for loads that have just
/// cleared every billing-readiness gate (<see cref="BillingBadge.ReadyToBill"/>) and raises one
/// in-app <see cref="NotificationStage.BillingReady"/> notification per load. Dedupe is per load
/// number, for the lifetime of the store — the goal is a single "this load is ready to bill" ping,
/// not a re-fire on every poll.
///
/// <para>
/// Closes the automation gap between a load becoming billable and someone on the billing team
/// noticing: today that state is only visible by opening the Billing tab, or the next morning's
/// <see cref="ArDigestService"/> aggregate count. <see cref="NotificationStage.BillingReady"/> (T6)
/// was declared in the owner spec but never fired before this agent — this wires it from the same
/// real, already-computed <see cref="BillingReadinessService"/> result the Billing worklist renders;
/// nothing new is derived or invented.
/// </para>
///
/// <para>Alvys posture: read-only. No Alvys writes. Alvys unavailable → 'degraded' heartbeat, no notification.</para>
/// </summary>
public sealed class BillingReadySweeperService(
    IServiceProvider services,
    TimeProvider clock,
    IOptions<AgentsOptions> options,
    ILogger<BillingReadySweeperService> logger)
    : AgentBackgroundService(services, clock, logger)
{
    public const string Name = "billing-ready-sweeper";

    private readonly BillingReadySweeperOptions _options = options.Value.BillingReadySweeper;

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

        var readyLoads = await loads.BillingWorklistAsync(BillingBadge.ReadyToBill, ct);
        foreach (var load in readyLoads)
        {
            ct.ThrowIfCancellationRequested();

            // A load with no stable identifier cannot be deduped safely; skip rather than risk a re-fire storm.
            if (string.IsNullOrWhiteSpace(load.LoadNumber))
            {
                continue;
            }

            await dispatcher.DispatchAsync(ToTrigger(load), ct);
        }

        return AgentSweepResult.Healthy(readyLoads.Count);
    }

    /// <summary>
    /// Maps a ready-to-bill load to a <see cref="NotificationStage.BillingReady"/> trigger.
    /// <c>SourceKey</c> is <c>agent-billing-ready:{loadNumber}</c> and <c>OccurredAt</c> is the Unix
    /// epoch (a fixed sentinel), so the dispatcher idempotency key is one-per-load-forever — a load
    /// that stays ready to bill (or is later invoiced) fires exactly once. Exposed for unit testing.
    /// </summary>
    public static NotificationTrigger ToTrigger(LtlLoadSummary load)
    {
        var loadLabel = load.LoadNumber!;
        var lane = FormatLane(load);
        var customer = string.IsNullOrWhiteSpace(load.CustomerName) ? null : load.CustomerName;
        var revenue = load.Revenue is > 0 ? $" (revenue {load.Revenue.Value:0.##})" : string.Empty;

        var summary = customer is null
            ? $"Load {loadLabel}{lane} is ready to bill{revenue}."
            : $"Load {loadLabel}{lane} for {customer} is ready to bill{revenue}.";

        return new NotificationTrigger
        {
            Stage = NotificationStage.BillingReady,
            SourceKey = $"agent-billing-ready:{loadLabel}",
            Title = $"Ready to bill · {loadLabel}",
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
