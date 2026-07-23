using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl.Agent;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.DispatchAssist;

/// <summary>
/// "Inform and assemble the right driver and truck" — the read-only Dispatch Assist engine. It
/// sweeps the Alvys fleet master data (drivers, trucks, trailers) and the dispatcher-curated
/// dispatch preferences, then ranks assembled driver+truck+trailer candidates against a load's
/// pickup with an explainable, deterministic score. Every candidate carries the reasons that
/// produced its score.
///
/// <para><b>Honest data posture.</b> The read-only Alvys Public API exposes no real-time ELD
/// hours-of-service duty status and no driver GPS/event stream, so this service never fabricates
/// them: <em>availability</em> is taken from the driver's Alvys <c>Status</c>/<c>IsActive</c>
/// (reported verbatim, and never a hard disqualifier — an off/available driver is still rankable),
/// and <em>proximity</em> is a state-centroid <i>reference</i> distance from the driver's Alvys
/// home-base address to the load origin, always labelled as such. Factors whose inputs are missing
/// are excluded from the score denominator rather than penalised. Nothing is written back to Alvys.
/// </para>
/// </summary>
public sealed class DispatchAssistService(
    IAlvysClient alvys, LtlLoadService loads, IOptions<LtlOptions> options,
    ILogger<DispatchAssistService> logger)
{
    private readonly LtlOptions _options = options.Value;

    // Factor weights. A candidate's score is earned / (sum of available factors' max) * 100, so a
    // factor with no data is excluded from the denominator instead of dragging the score down.
    private const int ProximityMax = 40;
    private const int AvailabilityMax = 25;
    private const int PreferenceMax = 20;
    private const int EquipmentMax = 15;

    /// <summary>Distance beyond which the proximity factor contributes nothing (reference miles).</summary>
    private const double ProximityCeilingMiles = 1500;

    /// <summary>
    /// Resolves the ranking target — from a live Alvys load when <paramref name="loadId"/> is given,
    /// otherwise from the caller-supplied origin/destination params — and ranks fleet candidates
    /// against it. Returns null only when a <paramref name="loadId"/> was supplied but could not be
    /// resolved upstream (so the controller can 404).
    /// </summary>
    public async Task<DispatchRecommendationsResponse?> RecommendAsync(
        string? loadId, string? originCity, string? originState,
        string? destinationCity, string? destinationState, int top, CancellationToken ct)
    {
        DispatchTarget target;
        if (!string.IsNullOrWhiteSpace(loadId))
        {
            var load = await loads.GetDetailAsync(loadId, ct);
            if (load is null) return null;
            target = new DispatchTarget
            {
                LoadId = load.Id,
                LoadNumber = load.LoadNumber,
                OriginCity = load.Origin?.City,
                OriginState = load.Origin?.State,
                DestinationCity = load.Destination?.City,
                DestinationState = load.Destination?.State,
                RequiredEquipment = load.Equipment,
                Source = "Alvys load (read-only).",
            };
        }
        else
        {
            target = new DispatchTarget
            {
                OriginCity = Clean(originCity),
                OriginState = Clean(originState),
                DestinationCity = Clean(destinationCity),
                DestinationState = Clean(destinationState),
                Source = "Caller-supplied origin/destination (no load id).",
            };
        }

        var take = top > 0 ? top : _options.DefaultMatchResults;
        var (candidates, truncated) = await AssembleAsync(ct);

        var ranked = candidates
            .Select(c => Rank(c, target))
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.IsPreferredPairing)
            .Take(take)
            .ToList();

        return new DispatchRecommendationsResponse
        {
            Target = target,
            Candidates = ranked,
            Truncated = truncated,
        };
    }

    /// <summary>
    /// Resolves the notify-step recipients (assigned driver + dispatcher) from read-only Alvys
    /// contacts. The driver comes from the assigned <c>DriverId</c>; the dispatcher from the matching
    /// dispatch preference's <c>DispatcherId</c> resolved to a user email. Recipients that Alvys does
    /// not carry an email for are still returned (address null) so the UI can show who was intended;
    /// they are simply skipped when actually addressing mail. Never fabricates an address.
    /// </summary>
    public async Task<IReadOnlyList<DispatchNotifyRecipient>> ResolveNotifyRecipientsAsync(
        DispatchAssembleRequest request, CancellationToken ct)
    {
        var recipients = new List<DispatchNotifyRecipient>();
        var pageSize = _options.AlvysPageSize;

        AlvysDriver? driver = null;
        if (!string.IsNullOrWhiteSpace(request.DriverId))
        {
            var drivers = await alvys.SearchDriversAsync(new DriverSearchRequest { PageSize = pageSize }, ct);
            driver = drivers.Items.FirstOrDefault(d =>
                string.Equals(d.Id, request.DriverId, StringComparison.OrdinalIgnoreCase));
            if (driver is not null)
                recipients.Add(new DispatchNotifyRecipient
                {
                    Role = "driver",
                    Name = Clean(driver.Name),
                    Address = Clean(driver.Email),
                });
        }

        // Dispatcher: preference (driver/truck/trailer) → DispatcherId → user email.
        var prefs = await alvys.SearchDispatchPreferencesAsync(new DispatchPreferenceSearchRequest
        {
            DriverIds = request.DriverId is { Length: > 0 } d ? [d] : null,
            TruckIds = request.TruckId is { Length: > 0 } t ? [t] : null,
            TrailerIds = request.TrailerId is { Length: > 0 } r ? [r] : null,
        }, ct);
        var dispatcherId = prefs
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => p.DispatcherId)
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));

        if (!string.IsNullOrWhiteSpace(dispatcherId))
        {
            var users = await alvys.SearchUsersAsync(new UserSearchRequest { PageSize = pageSize }, ct);
            var dispatcher = users.Items.FirstOrDefault(u =>
                string.Equals(u.Id, dispatcherId, StringComparison.OrdinalIgnoreCase));
            if (dispatcher is not null)
                recipients.Add(new DispatchNotifyRecipient
                {
                    Role = "dispatcher",
                    Name = Clean(dispatcher.Name) ?? Clean(dispatcher.UserName),
                    Address = Clean(dispatcher.Email),
                });
        }

        return recipients;
    }

    /// <summary>
    /// Scores one raw candidate against the target and produces its explainable row. Public so the
    /// ranking logic can be unit-tested directly without an Alvys sweep.
    /// </summary>
    public DispatchCandidate Rank(RawCandidate candidate, DispatchTarget target)
    {
        var driver = candidate.Driver;
        var truck = candidate.Truck;
        var trailer = candidate.Trailer;

        var reasons = new List<string>();
        double earned = 0;
        double availableMax = 0;

        // --- Proximity (home-base reference distance) ---
        var homeState = NormalizeState(driver?.Address?.State);
        var originState = NormalizeState(target.OriginState);
        int? referenceMiles = null;
        if (homeState is not null && originState is not null)
        {
            availableMax += ProximityMax;
            referenceMiles = (int)Math.Round(ReferenceMiles(homeState, originState));
            var fraction = Math.Clamp(1 - referenceMiles.Value / ProximityCeilingMiles, 0, 1);
            earned += ProximityMax * fraction;
            reasons.Add(string.Equals(homeState, originState, StringComparison.OrdinalIgnoreCase)
                ? $"home base {homeState} matches origin state"
                : $"≈{referenceMiles} mi from origin ({homeState}→{originState}, home-base reference)");
        }
        else
        {
            reasons.Add("proximity unavailable (no home-base or origin state)");
        }

        // --- Availability (Alvys driver status; never a hard disqualifier) ---
        var duty = DutyStatus(driver);
        if (driver is not null)
        {
            availableMax += AvailabilityMax;
            var active = driver.IsActive ?? IsActiveStatus(driver.Status);
            earned += active ? AvailabilityMax : AvailabilityMax * 0.4; // off/available still dispatchable
            reasons.Add(active
                ? $"driver active in Alvys (status: {duty})"
                : $"driver {duty} — may still be dispatchable, verify duty");
        }

        // --- Preferred pairing (dispatcher-curated) ---
        availableMax += PreferenceMax;
        if (candidate.Preference is not null)
        {
            earned += PreferenceMax;
            var pairLabel = truck?.TruckNum is { Length: > 0 } tn ? $" with TRK {tn}"
                : trailer?.TrailerNum is { Length: > 0 } rn ? $" with trailer {rn}" : "";
            reasons.Add($"preferred pairing{pairLabel}");
        }
        else
        {
            reasons.Add("no dispatcher preference on file");
        }

        // --- Equipment fit ---
        var equipType = Clean(trailer?.EquipmentType);
        if (target.RequiredEquipment.Count > 0 && equipType is not null)
        {
            availableMax += EquipmentMax;
            var fit = target.RequiredEquipment.Any(e =>
                e.Contains(equipType, StringComparison.OrdinalIgnoreCase) ||
                equipType.Contains(e, StringComparison.OrdinalIgnoreCase));
            if (fit)
            {
                earned += EquipmentMax;
                reasons.Add($"equipment {equipType} fits load requirement");
            }
            else
            {
                reasons.Add($"equipment {equipType} may not fit ({string.Join("/", target.RequiredEquipment)})");
            }
        }

        var score = availableMax > 0 ? (int)Math.Round(earned / availableMax * 100) : 0;

        return new DispatchCandidate
        {
            DriverId = driver?.Id,
            DriverName = Clean(driver?.Name),
            DriverEmail = Clean(driver?.Email),
            DriverPhone = Clean(driver?.PhoneNumber),
            DriverHomeState = homeState,
            DutyStatus = duty,
            TruckId = truck?.Id,
            TruckNumber = Clean(truck?.TruckNum),
            TrailerId = trailer?.Id,
            TrailerNumber = Clean(trailer?.TrailerNum),
            TrailerEquipmentType = equipType,
            PreferredDispatcherId = Clean(candidate.Preference?.DispatcherId),
            IsPreferredPairing = candidate.Preference is not null,
            ReferenceMilesFromOrigin = referenceMiles,
            Score = score,
            Reasons = reasons,
        };
    }

    /// <summary>
    /// Sweeps the read-only fleet master data and dispatch preferences and builds the raw candidate
    /// set: dispatcher-curated pairings first, then active drivers not already paired. Bounded by
    /// <see cref="LtlOptions.MaxMatchCandidates"/>; the truncation flag is honest.
    /// </summary>
    private async Task<(IReadOnlyList<RawCandidate> Candidates, bool Truncated)> AssembleAsync(
        CancellationToken ct)
    {
        var pageSize = _options.AlvysPageSize;
        var driversTask = alvys.SearchDriversAsync(
            new DriverSearchRequest { IsActive = true, PageSize = pageSize }, ct);
        var trucksTask = alvys.SearchTrucksAsync(
            new TruckSearchRequest { IsActive = true, PageSize = pageSize }, ct);
        var trailersTask = alvys.SearchTrailersAsync(new TrailerSearchRequest { PageSize = pageSize }, ct);
        var prefsTask = alvys.SearchDispatchPreferencesAsync(new DispatchPreferenceSearchRequest(), ct);

        await Task.WhenAll(driversTask, trucksTask, trailersTask, prefsTask);

        var drivers = Index((await driversTask).Items, d => d.Id);
        var trucks = Index((await trucksTask).Items, t => t.Id);
        var trailers = Index((await trailersTask).Items, t => t.Id);
        var preferences = await prefsTask;

        var max = _options.MaxMatchCandidates;
        var candidates = new List<RawCandidate>();
        var seenDrivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;

        foreach (var pref in preferences)
        {
            if (candidates.Count >= max) { truncated = true; break; }
            var driver = Lookup(drivers, pref.Driver1Id);
            var truck = Lookup(trucks, pref.TruckId);
            var trailer = Lookup(trailers, pref.TrailerId);
            if (driver is null && truck is null && trailer is null) continue;

            candidates.Add(new RawCandidate(driver, truck, trailer, pref));
            if (driver is not null) seenDrivers.Add(driver.Id);
        }

        foreach (var driver in drivers.Values)
        {
            if (candidates.Count >= max) { truncated = true; break; }
            if (!seenDrivers.Add(driver.Id)) continue;
            candidates.Add(new RawCandidate(driver, null, null, null));
        }

        logger.LogDebug(
            "Dispatch Assist assembled {Count} candidates from {Drivers} drivers / {Prefs} preferences.",
            candidates.Count, drivers.Count, preferences.Count);

        return (candidates, truncated);
    }

    private static string DutyStatus(AlvysDriver? driver)
    {
        if (driver is null) return "Unknown";
        if (!string.IsNullOrWhiteSpace(driver.Status)) return driver.Status.Trim();
        return driver.IsActive switch { true => "Active", false => "Inactive", null => "Unknown" };
    }

    private static bool IsActiveStatus(string? status) =>
        !string.IsNullOrWhiteSpace(status) &&
        status.Contains("active", StringComparison.OrdinalIgnoreCase) &&
        !status.Contains("inactive", StringComparison.OrdinalIgnoreCase);

    /// <summary>Great-circle distance (statute miles) between two state centroids; 0 within a state.</summary>
    private static double ReferenceMiles(string fromState, string toState)
    {
        if (string.Equals(fromState, toState, StringComparison.OrdinalIgnoreCase)) return 60; // intra-state nominal
        if (!StateCentroids.Table.TryGetValue(fromState, out var a) ||
            !StateCentroids.Table.TryGetValue(toState, out var b))
            return ProximityCeilingMiles; // unknown centroid → treated as far, never fabricated near

        const double earthRadiusMiles = 3958.8;
        var dLat = ToRad(b.Lat - a.Lat);
        var dLon = ToRad(b.Lon - a.Lon);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRad(a.Lat)) * Math.Cos(ToRad(b.Lat)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthRadiusMiles * 2 * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private static double ToRad(double deg) => deg * Math.PI / 180;

    /// <summary>Normalizes a state input (full name or code) to a two-letter uppercase code, or null.</summary>
    private static string? NormalizeState(string? state)
    {
        var s = Clean(state);
        if (s is null) return null;
        if (s.Length == 2 && StateCentroids.Table.ContainsKey(s)) return s.ToUpperInvariant();
        return StateCentroids.NameToCode.TryGetValue(s, out var code) ? code : null;
    }

    private static Dictionary<string, T> Index<T>(IEnumerable<T> items, Func<T, string> key) =>
        items.Where(i => !string.IsNullOrWhiteSpace(key(i)))
            .GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private static T? Lookup<T>(IReadOnlyDictionary<string, T> map, string? id) where T : class =>
        !string.IsNullOrWhiteSpace(id) && map.TryGetValue(id, out var v) ? v : null;

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// A raw driver/truck/trailer candidate before scoring. Any member may be null — the scorer treats
/// absent data as an unavailable factor rather than inventing capability.
/// </summary>
public sealed record RawCandidate(
    AlvysDriver? Driver, AlvysTruck? Truck, AlvysTrailerEquipment? Trailer,
    AlvysDispatchPreference? Preference);
