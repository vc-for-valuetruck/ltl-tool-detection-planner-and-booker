using Microsoft.Extensions.Caching.Memory;

namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// Short-lived memoization of the per-load-window truck/trailer <see cref="EquipmentEventBatch"/> so
/// re-opening a load's match drawer (or validating an assignment for the same window seconds later)
/// does not re-issue the two Alvys event searches. The cache key is derived from the pickup/delivery
/// window plus the exact set of equipment ids queried, so a different window or candidate set misses
/// and fetches fresh. Read-only data with a small TTL — never a source of truth, only a speed-up.
/// </summary>
public sealed class EquipmentEventCache(IMemoryCache cache)
{
    /// <summary>How long a fetched batch is served before a fresh Alvys fetch is issued.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Return the cached batch for <paramref name="key"/>, or invoke <paramref name="factory"/> once
    /// and cache the result. A not-evaluated batch (no window / no equipment) is not cached, since it
    /// carries no fetched data worth reusing.
    /// </summary>
    public async Task<EquipmentEventBatch> GetOrFetchAsync(
        string key, Func<Task<EquipmentEventBatch>> factory)
    {
        if (cache.TryGetValue<EquipmentEventBatch>(key, out var cached) && cached is not null)
            return cached;

        var batch = await factory();
        if (batch.Evaluated)
            cache.Set(key, batch, Ttl);
        return batch;
    }
}
