using Microsoft.Extensions.Hosting;

namespace LtlTool.Api.Features.Ltl.Consolidation;

/// <summary>
/// Warms <see cref="CorridorHealthCache"/> once at startup so the first <c>/corridors/health</c>
/// request already has live counts instead of an empty cold-cache snapshot. Fire-and-forget: the
/// warmup runs in the background and never blocks app startup or health checks, and any failure is
/// swallowed by the cache's own refresh guard (a degraded sweep just leaves the cache cold until
/// the next access-triggered refresh).
/// </summary>
public sealed class CorridorHealthWarmup(CorridorHealthCache cache) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cache.TriggerBackgroundRefresh();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
