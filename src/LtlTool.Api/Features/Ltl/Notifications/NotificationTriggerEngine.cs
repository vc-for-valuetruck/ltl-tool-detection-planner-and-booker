using LtlTool.Api.Features.Ltl.Consolidation;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Notifications;

/// <summary>
/// Background poller that diffs internal source state against the notification store and fires
/// any not-yet-seen workflow event through the <see cref="NotificationDispatcher"/>. Idempotency
/// lives in the dispatcher/store, so a re-poll or a process restart never double-fires the same
/// real-world event.
///
/// <para>
/// First slice sources <see cref="NotificationStage.ConsolidationPlanCreated"/> (T1) from the
/// existing <see cref="IConsolidationAuditStore"/> — an internal audit store, so the engine needs
/// no new Alvys read plumbing and the trigger is demoable end-to-end. Stages T2–T8 are declared
/// in <see cref="NotificationStage"/> and will be wired from Alvys trip-stop / invoice reads in
/// later slices; they are NOT fabricated here.
/// </para>
///
/// <para>
/// Alvys posture: read-only. This engine reads an internal audit store and never writes to Alvys.
/// </para>
/// </summary>
public sealed class NotificationTriggerEngine(
    IServiceProvider services,
    IOptions<NotificationOptions> options,
    ILogger<NotificationTriggerEngine> logger) : BackgroundService
{
    private readonly NotificationOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Clamp to a sane floor so a mis-set config can't spin the loop.
        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.PollIntervalSeconds));

        // First sweep immediately so a plan recorded before the first tick still surfaces promptly.
        await SweepSafely(stoppingToken);

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SweepSafely(stoppingToken);
        }
    }

    private async Task SweepSafely(CancellationToken ct)
    {
        try
        {
            await SweepConsolidationPlansAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // A sweep failure must never take the host down or stop future sweeps.
            logger.LogWarning(ex, "Notification sweep failed; will retry on next tick.");
        }
    }

    /// <summary>
    /// T1 — every recorded consolidation plan audit becomes a notification. Dedupe is by the
    /// audit record id (stable, one row per recorded plan), so re-polling the growing audit list
    /// only ever fires the rows we have not fired before.
    /// </summary>
    private async Task SweepConsolidationPlansAsync(CancellationToken ct)
    {
        // Resolve per-sweep from a scope: stores are singletons but resolving through a scope keeps
        // this safe if a future source becomes scoped (e.g. an EF-backed store).
        using var scope = services.CreateScope();
        var audits = scope.ServiceProvider.GetRequiredService<IConsolidationAuditStore>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<NotificationDispatcher>();

        foreach (var record in audits.All())
        {
            ct.ThrowIfCancellationRequested();
            await dispatcher.DispatchAsync(ToTrigger(record), ct);
        }
    }

    /// <summary>
    /// Maps a recorded consolidation-plan audit to a T1 notification trigger. The audit id is the
    /// dedupe identity (one row per recorded plan), so re-polling the growing audit list only ever
    /// fires rows not fired before. Exposed for unit testing the mapping in isolation.
    /// </summary>
    public static NotificationTrigger ToTrigger(ConsolidationAuditRecord record)
    {
        var loadLabel = record.ParentLoadNumber ?? record.ParentLoadId;
        var siblingCount = record.SiblingLoadNumbers.Count;
        var customer = string.IsNullOrWhiteSpace(record.ParentCustomerName)
            ? null
            : record.ParentCustomerName;

        var summary = customer is null
            ? $"Consolidation plan recorded for load {loadLabel} with {siblingCount} sibling load(s)."
            : $"Consolidation plan recorded for {customer} load {loadLabel} with {siblingCount} sibling load(s).";

        return new NotificationTrigger
        {
            Stage = NotificationStage.ConsolidationPlanCreated,
            SourceKey = record.Id,
            Title = $"Consolidation plan recorded · {loadLabel}",
            Summary = summary,
            LoadId = record.ParentLoadId,
            LoadNumber = record.ParentLoadNumber,
            LinkPath = $"/ltl/loads/{Uri.EscapeDataString(loadLabel)}",
            OccurredAt = record.RecordedAt,
        };
    }
}
