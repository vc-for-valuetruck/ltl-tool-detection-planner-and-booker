using LtlTool.Api.Features.Integrations.Alvys;
using Microsoft.Extensions.Caching.Memory;

namespace LtlTool.Api.Features.Ltl.DispatchPlanner;

/// <summary>
/// Read-only feature layer over the Alvys Public API dispatch-planner reads
/// (<see cref="IAlvysClient.SearchDispatchPreferencesAsync"/> and
/// <see cref="IAlvysClient.SearchLocationsAsync"/>). It exists to <em>utilise</em> that planner data
/// on the LTL surfaces — preferred driver/truck/trailer pairings and richer yard/location metadata —
/// without duplicating the typed client that #134 already built.
///
/// <para>
/// Two safety postures: (1) <b>polite caching</b> — both reads are memoised for a short TTL keyed by
/// their id set, so repeatedly opening the Dock review / Assignments surfaces does not hammer Alvys
/// (which rate-limits with 429). (2) <b>honest degradation</b> — the underlying client already turns
/// any non-success (429 included) into an empty result; this service passes that through as an
/// <see cref="DispatchPreferenceView.Unresolved"/> view or a missing map entry, never a fabricated
/// pairing or address.
/// </para>
/// </summary>
public sealed class DispatchPlannerService(
    IAlvysClient alvys,
    IMemoryCache cache,
    ILogger<DispatchPlannerService> logger)
{
    private readonly IAlvysClient _alvys = alvys;
    private readonly IMemoryCache _cache = cache;
    private readonly ILogger<DispatchPlannerService> _logger = logger;

    /// <summary>Dispatch preferences change rarely; a short TTL keeps the surfaces snappy and polite.</summary>
    private static readonly TimeSpan PreferenceTtl = TimeSpan.FromSeconds(60);

    /// <summary>Locations are effectively static master data; cache them longer.</summary>
    private static readonly TimeSpan LocationTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Resolves the preferred pairing for any combination of driver/truck/trailer id. Returns the
    /// most-recently-updated matching preference (Alvys can carry several); an empty read or a call
    /// with no ids yields <see cref="DispatchPreferenceView.Unresolved"/>.
    /// </summary>
    public async Task<DispatchPreferenceView> GetPreferredPairingAsync(
        string? driverId, string? truckId, string? trailerId, CancellationToken ct)
    {
        var driver = Clean(driverId);
        var truck = Clean(truckId);
        var trailer = Clean(trailerId);
        if (driver is null && truck is null && trailer is null)
            return DispatchPreferenceView.Unresolved;

        var key = $"dp:{driver}|{truck}|{trailer}";
        var preferences = await _cache.GetOrCreateAsync(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = PreferenceTtl;
            var request = new DispatchPreferenceSearchRequest
            {
                DriverIds = driver is null ? null : [driver],
                TruckIds = truck is null ? null : [truck],
                TrailerIds = trailer is null ? null : [trailer],
            };
            return _alvys.SearchDispatchPreferencesAsync(request, ct);
        }) ?? [];

        var winner = preferences
            .OrderByDescending(p => p.UpdatedAt)
            .FirstOrDefault();

        if (winner is null)
            return DispatchPreferenceView.Unresolved;

        return new DispatchPreferenceView
        {
            Resolved = true,
            DispatcherId = Clean(winner.DispatcherId),
            Driver1Id = Clean(winner.Driver1Id),
            Driver2Id = Clean(winner.Driver2Id),
            TruckId = Clean(winner.TruckId),
            TrailerId = Clean(winner.TrailerId),
            UpdatedAt = winner.UpdatedAt,
        };
    }

    /// <summary>
    /// Resolves the given Alvys location ids to a projection map (id → <see cref="LocationView"/>).
    /// Ids that Alvys does not return are simply absent from the map, so callers degrade to their
    /// static config rather than showing a blank. Empty input short-circuits with no Alvys call.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, LocationView>> GetLocationsAsync(
        IReadOnlyCollection<string> locationIds, CancellationToken ct)
    {
        var ids = locationIds
            .Select(Clean)
            .Where(id => id is not null)
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
            return EmptyMap;

        var key = "loc:" + string.Join('|', ids.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        var response = await _cache.GetOrCreateAsync(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = LocationTtl;
            var request = new LocationSearchRequest { Page = 0, PageSize = 100, LocationIds = ids };
            return _alvys.SearchLocationsAsync(request, ct);
        });

        var items = response?.Items ?? [];
        var map = new Dictionary<string, LocationView>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in items)
        {
            if (string.IsNullOrWhiteSpace(location.Id) || map.ContainsKey(location.Id))
                continue;
            map[location.Id] = new LocationView
            {
                Id = location.Id,
                Name = Clean(location.Name),
                Type = Clean(location.Type),
                AddressLabel = FormatAddress(location.PhysicalAddress),
            };
        }

        if (map.Count < ids.Count)
            _logger.LogDebug(
                "Dispatch-planner location enrichment resolved {Resolved}/{Requested} ids; unresolved yards degrade to config.",
                map.Count, ids.Count);

        return map;
    }

    private static readonly IReadOnlyDictionary<string, LocationView> EmptyMap =
        new Dictionary<string, LocationView>(StringComparer.OrdinalIgnoreCase);

    private static string? FormatAddress(AlvysContextAddress? address)
    {
        if (address is null)
            return null;
        var cityState = string.Join(", ",
            new[] { Clean(address.City), Clean(address.State) }.Where(p => p is not null));
        var tail = string.Join(' ',
            new[] { cityState.Length == 0 ? null : cityState, Clean(address.ZipCode) }
                .Where(p => p is not null));
        var line = string.Join(", ",
            new[] { Clean(address.Street), tail.Length == 0 ? null : tail }.Where(p => p is not null));
        return line.Length == 0 ? null : line;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
