using LtlTool.Api.Features.Integrations.Alvys;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// Builds the "Capacity today" snapshot (Phase 7.4) entirely from live, read-only Alvys reads:
/// how many trucks Alvys reports active, how the trailer pool breaks down by equipment type, and
/// how many trips are in transit right now. Every number is a real Alvys count — nothing is
/// fabricated. Each sweep is bounded by <see cref="LtlOptions.MaxLoadsScanned"/>; if any sweep
/// hits its cap the snapshot is marked <see cref="CapacitySnapshot.Truncated"/> so the UI reports
/// the counts as a floor ("at least N") rather than an exact total.
///
/// <para>
/// Read-only: this service never writes back to Alvys. The active/inactive truck split uses the
/// Alvys <c>IsActive</c> conditional filter (a proven-valid call shape) so the denominator is an
/// honest active + inactive total, not a guess.
/// </para>
/// </summary>
public sealed class CapacitySnapshotService(
    IAlvysClient alvys, IOptions<LtlOptions> options, TimeProvider clock)
{
    private readonly LtlOptions _options = options.Value;

    /// <summary>Trip statuses (case-insensitive) that count as "in transit right now".</summary>
    private static readonly List<string> InTransitStatuses = ["In Transit", "En-Route"];

    /// <summary>Truck status tokens (case-insensitive) that read as active/available.</summary>
    private static readonly string[] ActiveTruckTokens = ["Active", "Available"];

    public async Task<CapacitySnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        var (trucks, trucksTruncated) = await SweepTrucksAsync(ct);
        var (trailers, trailersTruncated) = await SweepTrailersAsync(ct);
        var (inTransitTrips, tripsTruncated) = await SweepInTransitTripsAsync(ct);

        var trailersByType = trailers
            .GroupBy(t => NormalizeEquipmentType(t.EquipmentType))
            .Select(g => new TrailerTypeCount { EquipmentType = g.Key, Count = g.Count() })
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.EquipmentType, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CapacitySnapshot
        {
            GeneratedAt = clock.GetUtcNow(),
            ActiveTrucks = trucks.Count(IsActive),
            TotalTrucks = trucks.Count,
            TotalTrailers = trailers.Count,
            TrailersByType = trailersByType,
            InTransitTrips = inTransitTrips,
            Truncated = trucksTruncated || trailersTruncated || tripsTruncated,
        };
    }

    private static bool IsActive(AlvysTruck truck) =>
        truck.Status is { } status
        && ActiveTruckTokens.Any(t => status.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeEquipmentType(string? equipmentType) =>
        string.IsNullOrWhiteSpace(equipmentType) ? "Unspecified" : equipmentType.Trim();

    /// <summary>
    /// Pages the trucks endpoint until exhausted or the scan bound is reached (no status filter, so
    /// the active count and the honest total denominator both come from one sweep — mirrors how the
    /// match engine already sweeps the trailer pool). Returns the trucks and whether the bound
    /// truncated the sweep.
    /// </summary>
    private async Task<(List<AlvysTruck> Items, bool Truncated)> SweepTrucksAsync(CancellationToken ct)
    {
        var pageSize = _options.AlvysPageSize;
        var items = new List<AlvysTruck>();
        var page = 0;

        while (true)
        {
            var response = await alvys.SearchTrucksAsync(
                new TruckSearchRequest { Page = page, PageSize = pageSize }, ct);
            if (response.Items.Count == 0) break;

            items.AddRange(response.Items);

            if (items.Count >= _options.MaxLoadsScanned)
                return (items, response.Total > items.Count);
            if (items.Count >= response.Total || response.Items.Count < pageSize) break;
            page++;
        }

        return (items, false);
    }

    private async Task<(List<AlvysTrailerEquipment> Items, bool Truncated)> SweepTrailersAsync(
        CancellationToken ct)
    {
        var pageSize = _options.AlvysPageSize;
        var items = new List<AlvysTrailerEquipment>();
        var page = 0;

        while (true)
        {
            var response = await alvys.SearchTrailersAsync(
                new TrailerSearchRequest { Page = page, PageSize = pageSize }, ct);
            if (response.Items.Count == 0) break;

            items.AddRange(response.Items);

            if (items.Count >= _options.MaxLoadsScanned)
                return (items, response.Total > items.Count);
            if (items.Count >= response.Total || response.Items.Count < pageSize) break;
            page++;
        }

        return (items, false);
    }

    private async Task<(int Count, bool Truncated)> SweepInTransitTripsAsync(CancellationToken ct)
    {
        var pageSize = _options.AlvysPageSize;
        var count = 0;
        var page = 0;

        while (true)
        {
            var response = await alvys.SearchTripsAsync(
                new TripSearchRequest { Page = page, PageSize = pageSize, Status = InTransitStatuses }, ct);
            if (response.Items.Count == 0) break;

            count += response.Items.Count;

            if (count >= _options.MaxLoadsScanned)
                return (count, response.Total > count);
            if (count >= response.Total || response.Items.Count < pageSize) break;
            page++;
        }

        return (count, false);
    }
}
