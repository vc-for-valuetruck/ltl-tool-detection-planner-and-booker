using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl.Notifications;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Agents;

/// <summary>
/// Background agent that, once per day at/after the configured local hour (default 08:00), fires a
/// single in-app AR / billing-attention digest summarising the billing worklist. It polls on a short
/// internal cadence but is time-gated and deduped to one notification per calendar day, so it never
/// spams — and it is <b>in-app only</b>: it passes <c>inAppOnly: true</c> to the dispatcher so it can
/// never trigger a Teams/email/Graph send.
///
/// <para>Alvys posture: read-only. Reuses <see cref="LtlLoadService.BillingWorklistAsync"/>. No Alvys writes.</para>
/// </summary>
public sealed class ArDigestService(
    IServiceProvider services,
    TimeProvider clock,
    IOptions<AgentsOptions> options,
    ILogger<ArDigestService> logger)
    : AgentBackgroundService(services, clock, logger)
{
    public const string Name = "ar-digest";

    private readonly ArDigestOptions _options = options.Value.ArDigest;

    public override string AgentName => Name;
    public override bool Enabled => _options.Enabled;

    // Time-gated, not cadence-driven: poll every 30 min and let the once-per-day dedupe + local-hour
    // gate decide when the digest actually fires.
    protected override TimeSpan Interval => TimeSpan.FromMinutes(30);

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

        var worklist = await loads.BillingWorklistAsync(badge: null, ct);

        // Only fire at/after the configured local hour. Before then we still record a healthy
        // heartbeat (the sweep ran) but hold the digest so it lands in the morning.
        var localNow = clock.GetLocalNow();
        if (localNow.Hour >= _options.HourLocal)
        {
            // in-app only — an AR digest must never page an external channel.
            await dispatcher.DispatchAsync(ToTrigger(worklist, localNow), ct, inAppOnly: true);
        }

        return AgentSweepResult.Healthy(worklist.Count);
    }

    /// <summary>
    /// Builds the daily digest trigger. <c>SourceKey</c> is <c>ar-digest</c> and <c>OccurredAt</c> is
    /// the local date at the configured hour, so the dispatcher idempotency key is one-per-calendar-day.
    /// Exposed for unit testing.
    /// </summary>
    public static NotificationTrigger ToTrigger(IReadOnlyList<LtlLoadSummary> worklist, DateTimeOffset localNow)
    {
        var total = worklist.Count;
        var notReady = worklist.Count(l => !l.Billing.IsReadyToBill && !l.Billing.IsAlreadyInvoiced);
        var readyToBill = worklist.Count(l => l.Billing.IsReadyToBill);

        var summary = total == 0
            ? "Daily AR digest: no loads currently need billing attention."
            : $"Daily AR digest: {total} load(s) on the billing worklist — "
              + $"{readyToBill} ready to bill, {notReady} needing data/review before billing.";

        var dateAtHour = new DateTimeOffset(
            localNow.Year, localNow.Month, localNow.Day, 0, 0, 0, localNow.Offset)
            .AddHours(localNow.Hour);

        return new NotificationTrigger
        {
            Stage = NotificationStage.ArDigest,
            SourceKey = "ar-digest",
            Title = $"AR digest · {localNow:yyyy-MM-dd}",
            Summary = summary,
            LinkPath = "/ltl/billing",
            OccurredAt = dateAtHour,
        };
    }
}
