using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Consolidation;

/// <summary>
/// Computes the live corridor-health snapshot: for every configured corridor, how many open loads
/// sit on the origin → destination lane right now, plus a default seed hint. This is the expensive
/// part — a bounded two-sided nearby-cities cross-product of tiny PageSize=1 Alvys reads — so it is
/// run off the request path by <see cref="CorridorHealthCache"/> and never inline in the controller.
/// Read-only against Alvys; nothing here writes upstream.
/// </summary>
public interface ICorridorHealthProbe
{
    Task<IReadOnlyList<CorridorHealth>> ComputeAsync(CancellationToken ct);
}

/// <inheritdoc cref="ICorridorHealthProbe"/>
public sealed class CorridorHealthProbe(
    LtlLoadService loads,
    IOptions<ConsolidationOptions> options) : ICorridorHealthProbe
{
    private readonly ConsolidationOptions _options = options.Value;
    private readonly LtlLoadService _loads = loads;

    /// <summary>
    /// Upper bound on origin × destination lane probes the corridor-health walk issues per
    /// corridor. Keeps the two-sided nearby-cities cross-product bounded; the default pilot config
    /// (≈8 origin × ≈9 destination cities) sits comfortably under it.
    /// </summary>
    private const int MaxLaneProbes = 100;

    public async Task<IReadOnlyList<CorridorHealth>> ComputeAsync(CancellationToken ct)
    {
        var warehouseByCode = _options.Warehouses.ToDictionary(
            w => w.Code, w => w, StringComparer.OrdinalIgnoreCase);

        var healths = new List<CorridorHealth>();
        foreach (var corridor in _options.Corridors)
        {
            if (!warehouseByCode.TryGetValue(corridor.OriginWarehouseCode, out var origin) ||
                !warehouseByCode.TryGetValue(corridor.DestinationWarehouseCode, out var destination))
            {
                // Silently drop misconfigured corridors here — the /corridors listing already
                // filtered them out. This branch is defensive.
                continue;
            }

            // Canonical city = first entry in NearbyCities, by convention the yard's own name.
            // Warehouses ship at least one nearby city so First() is safe on real config, but
            // guard against test/edge configs anyway.
            var originCity = origin.NearbyCities.FirstOrDefault();
            var destinationCity = destination.NearbyCities.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(originCity) || string.IsNullOrWhiteSpace(destinationCity))
            {
                healths.Add(new CorridorHealth
                {
                    Code = corridor.Code,
                    OpenLoadCount = 0,
                    Truncated = false,
                    OriginCity = originCity ?? origin.Code,
                    DestinationCity = destinationCity ?? destination.Code,
                });
                continue;
            }

            // Walk origin nearby-cities × destination nearby-cities (each a tiny PageSize=1 read)
            // rather than only the canonical yard-name pair. This is what makes the pilot queue
            // auto-populate reliably: the seed hint is found whenever ANY legal origin/destination
            // pair (e.g. Santa Catarina → Irving, not just "Laredo" → "Dallas") has a live parent
            // — the common case in the live tenant. The walk is bounded by MaxLaneProbes so the
            // cross-product can never balloon Alvys reads; a tripped cap is reported honestly as
            // truncated below.
            var originCities = origin.NearbyCities.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
            var destinationCities = destination.NearbyCities.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();

            var totalCount = 0;
            var anyTruncated = false;
            var successfulReads = 0;
            var probes = 0;
            LtlLoadSummary? seed = null;
            foreach (var oCity in originCities)
            {
                foreach (var dCity in destinationCities)
                {
                    if (probes >= MaxLaneProbes)
                    {
                        // Bounded sweep: stop probing beyond the cap and flag the count truncated
                        // so the picker never implies it scanned the full cross-product.
                        anyTruncated = true;
                        break;
                    }
                    probes++;

                    LtlSearchResponse response;
                    try
                    {
                        response = await _loads.SearchAsync(new LtlSearchQuery
                        {
                            // Status pinned to Open: Alvys loads/search returns nothing for a
                            // filterless request, and only open freight is plannable.
                            Status = ["Open"],
                            OriginCity = oCity,
                            DestinationCity = dCity,
                            Page = 0,
                            // PageSize=1 keeps Alvys payloads tiny — we only need Total + a seed hint.
                            PageSize = 1,
                        }, ct);
                    }
                    catch (Exception)
                    {
                        // One lane's read failing must not take the picker down; skip it and let the
                        // rest of the walk contribute. Only if EVERY read fails do we report the
                        // corridor as "unknown" below.
                        continue;
                    }

                    successfulReads++;
                    totalCount += response.Total;
                    anyTruncated |= response.Truncated;
                    // First open load found becomes the default seed hint so the UI can populate the
                    // pilot queue on tab-load without app-settings. Honest: stays null when every lane
                    // is empty. Never fabricated.
                    seed ??= response.Items.FirstOrDefault();
                }

                if (probes >= MaxLaneProbes) break;
            }

            if (successfulReads == 0)
            {
                // Every origin-city read degraded — return a null signal so the caller shows
                // "unknown" honestly rather than a false zero.
                healths.Add(new CorridorHealth
                {
                    Code = corridor.Code,
                    OpenLoadCount = null,
                    Truncated = false,
                    OriginCity = originCity,
                    DestinationCity = destinationCity,
                });
                continue;
            }

            healths.Add(new CorridorHealth
            {
                Code = corridor.Code,
                OpenLoadCount = totalCount,
                Truncated = anyTruncated,
                OriginCity = originCity,
                DestinationCity = destinationCity,
                SeedLoadId = seed?.Id,
                SeedLoadNumber = seed?.LoadNumber,
            });
        }

        return healths;
    }
}
