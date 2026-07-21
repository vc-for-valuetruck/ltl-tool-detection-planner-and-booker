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
            await SweepLoadExceptionsAsync(ct);
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
    /// T8 — one sweep of the internal <see cref="LtlLoadService"/> exception worklist
    /// (Alvys-read-only) fires two exception notification kinds:
    /// <list type="bullet">
    /// <item>Predicted-late: an in-transit load whose ETA is past its delivery window, fired BEFORE
    /// it actually goes late. Dedupe is load id + predicted ETA (the trigger <c>OccurredAt</c>), so
    /// re-polling an unchanged prediction never re-fires while a materially shifted ETA surfaces a
    /// fresh heads-up.</item>
    /// <item>Actual-late delivery: the delivery-stop window/appointment has passed with no arrival
    /// recorded on Alvys. Dedupe is load + stop + window end, so a still-late delivery seen on every
    /// poll fires exactly once (no re-fire storm).</item>
    /// </list>
    /// Writes nothing back to Alvys.
    /// </summary>
    private async Task SweepLoadExceptionsAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var loads = scope.ServiceProvider.GetRequiredService<LtlLoadService>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<NotificationDispatcher>();

        var exceptionLoads = await loads.ExceptionsAsync(ct);
        foreach (var load in exceptionLoads)
        {
            ct.ThrowIfCancellationRequested();
            if (load.PredictedLate && load.PredictedDeliveryAt is not null)
                await dispatcher.DispatchAsync(ToPredictedLateTrigger(load), ct);
            if (load.LateDelivery is not null)
                await dispatcher.DispatchAsync(ToLateDeliveryTrigger(load), ct);
            if (load.StuckStop is not null)
                await dispatcher.DispatchAsync(ToStuckStopTrigger(load), ct);
        }
    }

    /// <summary>
    /// Maps an actual-late delivery to a T8 exception trigger. <c>SourceKey</c> is
    /// <c>{loadId}:{stopId}</c> and <c>OccurredAt</c> is the passed window end, so the dispatcher
    /// idempotency key is one-per-(load, stop, window end) — a still-late delivery seen on every
    /// poll fires exactly once. Exposed for unit testing the mapping in isolation.
    /// </summary>
    public static NotificationTrigger ToLateDeliveryTrigger(LtlLoadSummary load)
    {
        var loadLabel = load.LoadNumber ?? load.Id;
        var late = load.LateDelivery!;
        var where = FormatPlace(late.DestinationCity, late.DestinationState);
        var destination = where is null ? string.Empty : $" to {where}";

        return new NotificationTrigger
        {
            Stage = NotificationStage.ExceptionRaised,
            SourceKey = $"{load.Id}:{late.StopId}",
            Title = $"Late delivery · {loadLabel}",
            Summary = $"Load {loadLabel}{destination} is {late.HoursOverdue:0.#}h overdue. {late.Message}.",
            LoadId = load.Id,
            LoadNumber = load.LoadNumber,
            LinkPath = $"/ltl/loads/{Uri.EscapeDataString(loadLabel)}",
            OccurredAt = late.WindowEnd,
        };
    }

    /// <summary>
    /// Maps a stuck-at-stop signal to a T8 exception trigger. <c>SourceKey</c> is
    /// <c>{loadId}:{stopId}:stuck</c> (distinct from the late-delivery key on the same stop) and
    /// <c>OccurredAt</c> is the recorded arrival, so the dispatcher idempotency key is
    /// one-per-(load, stop, condition) — a still-stuck stop seen on every poll fires exactly once.
    /// Exposed for unit testing the mapping in isolation.
    /// </summary>
    public static NotificationTrigger ToStuckStopTrigger(LtlLoadSummary load)
    {
        var loadLabel = load.LoadNumber ?? load.Id;
        var stuck = load.StuckStop!;
        var where = FormatPlace(stuck.City, stuck.State);
        var at = where is null ? string.Empty : $" at {where}";

        return new NotificationTrigger
        {
            Stage = NotificationStage.ExceptionRaised,
            SourceKey = $"{load.Id}:{stuck.StopId}:stuck",
            Title = $"Stuck at stop · {loadLabel}",
            Summary =
                $"Load {loadLabel} has been at its stop{at} for {stuck.HoursSinceArrival:0.#}h "
                + $"with no departure recorded. {stuck.Message}.",
            LoadId = load.Id,
            LoadNumber = load.LoadNumber,
            LinkPath = $"/ltl/loads/{Uri.EscapeDataString(loadLabel)}",
            OccurredAt = stuck.ArrivedAt,
        };
    }

    private static string? FormatPlace(string? city, string? state)
    {
        if (!string.IsNullOrWhiteSpace(city) && !string.IsNullOrWhiteSpace(state)) return $"{city}, {state}";
        if (!string.IsNullOrWhiteSpace(city)) return city;
        if (!string.IsNullOrWhiteSpace(state)) return state;
        return null;
    }

    /// <summary>
    /// Maps a predicted-late load to a T8 exception trigger. <c>OccurredAt</c> is the predicted ETA
    /// (not "now"), so the dispatcher idempotency key stays stable across polls of the same
    /// prediction. Exposed for unit testing the mapping in isolation.
    /// </summary>
    public static NotificationTrigger ToPredictedLateTrigger(LtlLoadSummary load)
    {
        var loadLabel = load.LoadNumber ?? load.Id;
        var eta = load.PredictedDeliveryAt!.Value;
        var window = load.ScheduledDeliveryAt;
        var summary = window is null
            ? $"Load {loadLabel} is predicted late — ETA {eta:g}. {load.EtaBasis}"
            : $"Load {loadLabel} is predicted late — ETA {eta:g} vs {window:g} delivery window. {load.EtaBasis}";

        return new NotificationTrigger
        {
            Stage = NotificationStage.ExceptionRaised,
            SourceKey = load.Id,
            Title = $"Predicted late · {loadLabel}",
            Summary = summary,
            LoadId = load.Id,
            LoadNumber = load.LoadNumber,
            LinkPath = $"/ltl/loads/{Uri.EscapeDataString(loadLabel)}",
            OccurredAt = eta,
        };
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
