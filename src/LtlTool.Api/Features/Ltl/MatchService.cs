using LtlTool.Api.Features.Integrations.Alvys;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// Assembles driver/truck/trailer candidates from the read-only Alvys fleet master data and
/// ranks them against a load with the deterministic <see cref="MatchScoringService"/>.
///
/// Candidates are sourced from Alvys dispatch preferences first — those are the real
/// dispatcher-curated driver↔truck↔trailer pairings — and fall back to active drivers alone
/// when no preferences exist (the scorer treats absent equipment as unavailable rather than
/// inventing it). The candidate set is bounded by <see cref="LtlOptions.MaxMatchCandidates"/>
/// so a large fleet never turns one recommendation into an unbounded scoring sweep. This is a
/// pure read/score path: nothing is written back to Alvys.
/// </summary>
public sealed class MatchService(
    IAlvysClient alvys, MatchScoringService scorer, EquipmentEventAnalyzer equipmentEvents,
    IOptions<LtlOptions> options)
{
    private readonly LtlOptions _options = options.Value;

    /// <summary>
    /// Ranks fleet candidates for <paramref name="load"/>, best first. <paramref name="top"/>
    /// defaults to <see cref="LtlOptions.DefaultMatchResults"/> when not positive. When the load
    /// carries a usable pickup/delivery window, truck/trailer events are batch-fetched once and
    /// folded into each candidate's equipment-availability factor.
    /// </summary>
    public async Task<IReadOnlyList<MatchResult>> RecommendAsync(
        LtlLoadSummary load, int top, CancellationToken ct)
    {
        var take = top > 0 ? top : _options.DefaultMatchResults;
        var candidates = await AssembleCandidatesAsync(ct);
        var events = await FetchEquipmentEventsAsync(load, candidates, ct);

        return candidates
            .Select(c => scorer.Score(load, c, AssessCandidate(load, c, events)))
            // No hard disqualifier first, then by score; both descending so the best is on top.
            .OrderByDescending(r => r.Disqualifiers.Count == 0)
            .ThenByDescending(r => r.Score)
            .Take(take)
            .ToList();
    }

    /// <summary>
    /// Builds the equipment-availability assessment for a single candidate against the load window
    /// from a pre-fetched event batch. Returns <see cref="EquipmentEventAssessment.NotEvaluated"/>
    /// when no window/fetch was performed (so absent data never reads as "available").
    /// </summary>
    public EquipmentEventAssessment AssessCandidate(
        LtlLoadSummary load, MatchCandidate candidate, EquipmentEventBatch events)
    {
        if (!events.Evaluated) return EquipmentEventAssessment.NotEvaluated;

        var truckId = candidate.Truck?.Id;
        var trailerId = candidate.Trailer?.Id;
        var truckEvents = truckId is { Length: > 0 }
            ? events.TruckEvents.Where(e => IdEquals(e.TruckId, truckId)) : [];
        var trailerEvents = trailerId is { Length: > 0 }
            ? events.TrailerEvents.Where(e => IdEquals(e.TrailerId, trailerId)) : [];

        return equipmentEvents.Assess(
            load.ScheduledPickupAt, load.ScheduledDeliveryAt, truckEvents, trailerEvents, evaluated: true);
    }

    /// <summary>
    /// Batch-fetches truck + trailer events across all candidate equipment over the load's
    /// pickup/delivery window in two calls. Returns a not-evaluated batch when the load has no usable
    /// window or there is no equipment to query.
    /// </summary>
    public async Task<EquipmentEventBatch> FetchEquipmentEventsAsync(
        LtlLoadSummary load, IReadOnlyList<MatchCandidate> candidates, CancellationToken ct)
    {
        var start = load.ScheduledPickupAt ?? load.ScheduledDeliveryAt;
        var end = load.ScheduledDeliveryAt ?? load.ScheduledPickupAt;
        if (start is null) return EquipmentEventBatch.NotEvaluated;

        var truckIds = candidates
            .Select(c => c.Truck?.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var trailerIds = candidates
            .Select(c => c.Trailer?.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (truckIds.Count == 0 && trailerIds.Count == 0) return EquipmentEventBatch.NotEvaluated;

        var truckTask = truckIds.Count > 0
            ? alvys.SearchTruckEventsAsync(
                new TruckEventSearchRequest { StartDate = start, EndDate = end, TruckIds = truckIds }, ct)
            : Task.FromResult<IReadOnlyList<AlvysTruckEvent>>([]);
        var trailerTask = trailerIds.Count > 0
            ? alvys.SearchTrailerEventsAsync(
                new TrailerEventSearchRequest { StartDate = start, EndDate = end, TrailerIds = trailerIds }, ct)
            : Task.FromResult<IReadOnlyList<AlvysTrailerEvent>>([]);

        await Task.WhenAll(truckTask, trailerTask);
        return new EquipmentEventBatch
        {
            Evaluated = true,
            TruckEvents = await truckTask,
            TrailerEvents = await trailerTask,
        };
    }

    /// <summary>
    /// Resolves a single <see cref="MatchCandidate"/> from the read-only fleet master data by the
    /// ids on a proposed assignment. Members that are not supplied or do not resolve are left null
    /// (the validator treats absent equipment/driver as unconfirmed rather than inventing it).
    /// </summary>
    public async Task<MatchCandidate> ResolveCandidateAsync(
        string? driverId, string? truckId, string? trailerId, CancellationToken ct)
    {
        var pageSize = _options.AlvysPageSize;

        var driversTask = string.IsNullOrWhiteSpace(driverId)
            ? Task.FromResult(new AlvysDriversResponse())
            : alvys.SearchDriversAsync(new DriverSearchRequest { PageSize = pageSize }, ct);
        var trucksTask = string.IsNullOrWhiteSpace(truckId)
            ? Task.FromResult(new AlvysTrucksResponse())
            : alvys.SearchTrucksAsync(new TruckSearchRequest { PageSize = pageSize }, ct);
        var trailersTask = string.IsNullOrWhiteSpace(trailerId)
            ? Task.FromResult(new AlvysTrailersResponse())
            : alvys.SearchTrailersAsync(new TrailerSearchRequest { PageSize = pageSize }, ct);

        await Task.WhenAll(driversTask, trucksTask, trailersTask);

        return new MatchCandidate
        {
            Driver = (await driversTask).Items.FirstOrDefault(d => IdEquals(d.Id, driverId)),
            Truck = (await trucksTask).Items.FirstOrDefault(t => IdEquals(t.Id, truckId)),
            Trailer = (await trailersTask).Items.FirstOrDefault(t => IdEquals(t.Id, trailerId)),
        };
    }

    private static bool IdEquals(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<MatchCandidate>> AssembleCandidatesAsync(CancellationToken ct)
    {
        var pageSize = _options.AlvysPageSize;

        var driversTask = alvys.SearchDriversAsync(
            new DriverSearchRequest { IsActive = true, PageSize = pageSize }, ct);
        var trucksTask = alvys.SearchTrucksAsync(
            new TruckSearchRequest { IsActive = true, PageSize = pageSize }, ct);
        var trailersTask = alvys.SearchTrailersAsync(
            new TrailerSearchRequest { PageSize = pageSize }, ct);
        var preferencesTask = alvys.SearchDispatchPreferencesAsync(
            new DispatchPreferenceSearchRequest(), ct);

        await Task.WhenAll(driversTask, trucksTask, trailersTask, preferencesTask);

        var drivers = (await driversTask).Items
            .Where(d => !string.IsNullOrWhiteSpace(d.Id))
            .GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
        var trucks = (await trucksTask).Items
            .Where(t => !string.IsNullOrWhiteSpace(t.Id))
            .GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        var trailers = (await trailersTask).Items
            .Where(t => !string.IsNullOrWhiteSpace(t.Id))
            .GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        var preferences = await preferencesTask;

        var candidates = new List<MatchCandidate>();
        var seenDrivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Primary source: dispatcher-curated pairings.
        foreach (var pref in preferences)
        {
            if (candidates.Count >= _options.MaxMatchCandidates) break;

            var driver = Lookup(drivers, pref.Driver1Id);
            var truck = Lookup(trucks, pref.TruckId);
            var trailer = Lookup(trailers, pref.TrailerId);
            if (driver is null && truck is null && trailer is null) continue;

            candidates.Add(new MatchCandidate { Driver = driver, Truck = truck, Trailer = trailer });
            if (driver is not null) seenDrivers.Add(driver.Id);
        }

        // Fallback / fill: active drivers not already paired, scored on driver factors alone.
        foreach (var driver in drivers.Values)
        {
            if (candidates.Count >= _options.MaxMatchCandidates) break;
            if (!seenDrivers.Add(driver.Id)) continue;
            candidates.Add(new MatchCandidate { Driver = driver });
        }

        return candidates;
    }

    private static T? Lookup<T>(IReadOnlyDictionary<string, T> map, string? id) where T : class =>
        !string.IsNullOrWhiteSpace(id) && map.TryGetValue(id, out var value) ? value : null;
}
