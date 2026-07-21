using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LtlTool.Api.Features.Ltl.Consolidation;

/// <summary>
/// Server-side, in-memory snapshot cache for the corridor-health sweep. The sweep issues up to a
/// hundred sequential Alvys reads per corridor, so running it inline on every UI page-load hung the
/// <c>/corridors/health</c> request for 10s+. This cache serves the last computed snapshot to the
/// request path <b>instantly</b> and refreshes it in the background (stale-while-revalidate), with a
/// hard timeout on the compute so a slow Alvys can never wedge a refresh forever. The controller
/// never awaits the sweep — a cold cache returns an empty snapshot and triggers the first refresh,
/// and health is a progressive enhancement on top of the already-rendered corridor chips.
///
/// <para>Singleton by design (shared across requests). Read-only against Alvys via the probe.</para>
/// </summary>
public sealed class CorridorHealthCache(
    IServiceScopeFactory scopeFactory,
    ILogger<CorridorHealthCache> logger)
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<CorridorHealthCache> _logger = logger;

    /// <summary>How long a snapshot is served before a background refresh is triggered on access.</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    /// <summary>Hard cap on a single background sweep so a slow Alvys never wedges the refresh.</summary>
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(60);

    private volatile Snapshot? _snapshot;
    private int _refreshing; // 0 = idle, 1 = a refresh is in-flight (single-flight guard).

    /// <summary>An immutable computed snapshot plus the instant it was produced.</summary>
    public sealed record Snapshot(IReadOnlyList<CorridorHealth> Healths, DateTimeOffset AsOf);

    /// <summary>
    /// Returns the current snapshot (may be <c>null</c> on a cold cache) and kicks off a background
    /// refresh when the snapshot is missing or older than <see cref="Ttl"/>. Never blocks on the sweep.
    /// </summary>
    public Snapshot? GetSnapshot()
    {
        var snap = _snapshot;
        if (snap is null || DateTimeOffset.UtcNow - snap.AsOf > Ttl)
        {
            TriggerBackgroundRefresh();
        }
        return snap;
    }

    /// <summary>Fire-and-forget a refresh unless one is already running (single-flight).</summary>
    public void TriggerBackgroundRefresh()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Keep the prior snapshot on failure — a degraded sweep must not blank the picker.
                _logger.LogWarning(ex, "Corridor-health background refresh failed; keeping prior snapshot.");
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    /// <summary>
    /// Runs the sweep in a fresh DI scope (the probe depends on scoped services) under a hard
    /// timeout and replaces the snapshot on success. Public so a startup warmup can await it.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RefreshTimeout);

        using var scope = _scopeFactory.CreateScope();
        var probe = scope.ServiceProvider.GetRequiredService<ICorridorHealthProbe>();
        var healths = await probe.ComputeAsync(timeoutCts.Token);
        _snapshot = new Snapshot(healths, DateTimeOffset.UtcNow);
    }
}
