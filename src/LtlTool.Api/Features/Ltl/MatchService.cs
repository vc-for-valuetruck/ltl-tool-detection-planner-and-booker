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
    IAlvysClient alvys, MatchScoringService scorer, IOptions<LtlOptions> options)
{
    private readonly LtlOptions _options = options.Value;

    /// <summary>
    /// Ranks fleet candidates for <paramref name="load"/>, best first. <paramref name="top"/>
    /// defaults to <see cref="LtlOptions.DefaultMatchResults"/> when not positive.
    /// </summary>
    public async Task<IReadOnlyList<MatchResult>> RecommendAsync(
        LtlLoadSummary load, int top, CancellationToken ct)
    {
        var take = top > 0 ? top : _options.DefaultMatchResults;
        var candidates = await AssembleCandidatesAsync(ct);

        return candidates
            .Select(c => scorer.Score(load, c))
            // No hard disqualifier first, then by score; both descending so the best is on top.
            .OrderByDescending(r => r.Disqualifiers.Count == 0)
            .ThenByDescending(r => r.Score)
            .Take(take)
            .ToList();
    }

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
