using System.Collections.Concurrent;

namespace LtlTool.Api.Features.Ltl.Optimization;

/// <summary>
/// An Alvys-derived location. Coordinates are optional because current Alvys reads expose only
/// city/state on a stop, never lat/long — so <see cref="Latitude"/>/<see cref="Longitude"/> are
/// usually null and the distance provider degrades to a clearly-labeled estimate. Nothing here is
/// fetched from a non-Alvys source.
/// </summary>
public sealed record GeoPoint(string? City, string? State, double? Latitude = null, double? Longitude = null);

/// <summary>How a single leg's mileage was derived — surfaced so the UI never presents an estimate as truth.</summary>
public enum DistanceSource
{
    /// <summary>Mileage came straight from an Alvys field (LoadedMiles / CustomerMileage).</summary>
    Alvys,

    /// <summary>Mileage is a straight-line-derated estimate (coordinates present) or a coarse fallback (no geo).</summary>
    Estimated,
}

/// <summary>A directed leg with a known Alvys mileage, seeded into the matrix as first-line truth.</summary>
public sealed record KnownLeg(string FromRef, string ToRef, decimal Miles);

/// <summary>
/// Result of building a distance matrix: the node order, an integer miles matrix (rounded, for
/// OR-Tools), the per-cell provenance, and whether any cell was estimated.
/// </summary>
public sealed record DistanceMatrixResult(
    IReadOnlyList<string> OrderedRefs,
    long[,] Miles,
    DistanceSource[,] Sources,
    bool AnyEstimated);

/// <summary>Tuning for the distance provider. Bound from <c>Ltl:Optimization:Distance</c>.</summary>
public sealed class DistanceMatrixOptions
{
    public const string SectionName = "Ltl:Optimization:Distance";

    /// <summary>
    /// Multiplier applied to a straight-line (haversine) distance to approximate road miles.
    /// 1.2 is the widely-used circuity default; the result is always labeled <see cref="DistanceSource.Estimated"/>.
    /// </summary>
    public double RoadCircuityFactor { get; set; } = 1.2;

    /// <summary>
    /// Coarse per-leg estimate used only when a leg has neither an Alvys mileage nor coordinates on
    /// both ends. Labeled <see cref="DistanceSource.Estimated"/> so it is never mistaken for a real
    /// distance; it exists purely so the solver has a finite arc cost to reason about.
    /// </summary>
    public double FallbackMilesWhenUnknown { get; set; } = 500;
}

/// <summary>
/// Builds the node-to-node mileage matrix an OR-Tools routing model needs, without introducing any
/// data source beyond Alvys. First-line values are Alvys-derived leg miles
/// (<c>LoadedMiles</c>/<c>CustomerMileage</c>); everything else is a straight-line-derated estimate
/// (when both ends carry coordinates) or a coarse labeled fallback. Results are cached in-memory.
/// </summary>
public interface IDistanceMatrixProvider
{
    /// <summary>
    /// Build a symmetric-by-default matrix over <paramref name="points"/> (indexed by their position),
    /// seeding directed <paramref name="knownLegs"/> as Alvys truth where available.
    /// </summary>
    DistanceMatrixResult Build(
        IReadOnlyList<GeoPoint> points,
        IReadOnlyList<string> refs,
        IReadOnlyList<KnownLeg> knownLegs);
}

/// <summary>
/// Default <see cref="IDistanceMatrixProvider"/>: Alvys-first, estimate-labeled fallback, in-memory
/// cache keyed by the input shape. Registered as a singleton so the cache is shared across requests.
/// </summary>
public sealed class AlvysDistanceMatrixProvider(Microsoft.Extensions.Options.IOptions<DistanceMatrixOptions> options)
    : IDistanceMatrixProvider
{
    private readonly DistanceMatrixOptions _opts = options.Value;
    private readonly ConcurrentDictionary<string, DistanceMatrixResult> _cache = new();

    public DistanceMatrixResult Build(
        IReadOnlyList<GeoPoint> points,
        IReadOnlyList<string> refs,
        IReadOnlyList<KnownLeg> knownLegs)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(refs);
        ArgumentNullException.ThrowIfNull(knownLegs);
        if (points.Count != refs.Count)
            throw new ArgumentException("points and refs must be the same length.");

        var cacheKey = BuildCacheKey(points, refs, knownLegs);
        return _cache.GetOrAdd(cacheKey, _ => Compute(points, refs, knownLegs));
    }

    private DistanceMatrixResult Compute(
        IReadOnlyList<GeoPoint> points,
        IReadOnlyList<string> refs,
        IReadOnlyList<KnownLeg> knownLegs)
    {
        var n = points.Count;
        var miles = new long[n, n];
        var sources = new DistanceSource[n, n];
        var anyEstimated = false;

        var refIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < n; i++) refIndex[refs[i]] = i;

        var known = new Dictionary<(int, int), decimal>();
        foreach (var leg in knownLegs)
        {
            if (refIndex.TryGetValue(leg.FromRef, out var f)
                && refIndex.TryGetValue(leg.ToRef, out var t)
                && f != t
                && leg.Miles >= 0)
            {
                known[(f, t)] = leg.Miles;
            }
        }

        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                if (i == j)
                {
                    miles[i, j] = 0;
                    sources[i, j] = DistanceSource.Alvys; // a self-leg is definitionally zero, not an estimate
                    continue;
                }

                if (known.TryGetValue((i, j), out var alvysMiles))
                {
                    miles[i, j] = (long)Math.Round(alvysMiles, MidpointRounding.AwayFromZero);
                    sources[i, j] = DistanceSource.Alvys;
                    continue;
                }

                sources[i, j] = DistanceSource.Estimated;
                anyEstimated = true;
                var haversine = Haversine(points[i], points[j]);
                miles[i, j] = haversine is not null
                    ? (long)Math.Round(haversine.Value * _opts.RoadCircuityFactor, MidpointRounding.AwayFromZero)
                    : (long)Math.Round(_opts.FallbackMilesWhenUnknown, MidpointRounding.AwayFromZero);
            }
        }

        return new DistanceMatrixResult(refs, miles, sources, anyEstimated);
    }

    /// <summary>Straight-line miles between two points, or null when either lacks coordinates.</summary>
    private static double? Haversine(GeoPoint a, GeoPoint b)
    {
        if (a.Latitude is null || a.Longitude is null || b.Latitude is null || b.Longitude is null)
            return null;

        const double earthRadiusMiles = 3958.7613;
        var lat1 = DegreesToRadians(a.Latitude.Value);
        var lat2 = DegreesToRadians(b.Latitude.Value);
        var dLat = DegreesToRadians(b.Latitude.Value - a.Latitude.Value);
        var dLon = DegreesToRadians(b.Longitude.Value - a.Longitude.Value);

        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthRadiusMiles * (2 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1 - h)));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static string BuildCacheKey(
        IReadOnlyList<GeoPoint> points,
        IReadOnlyList<string> refs,
        IReadOnlyList<KnownLeg> knownLegs)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            sb.Append(refs[i]).Append('|')
              .Append(p.City).Append('|').Append(p.State).Append('|')
              .Append(p.Latitude?.ToString("R") ?? "-").Append('|')
              .Append(p.Longitude?.ToString("R") ?? "-").Append(';');
        }
        sb.Append("##");
        foreach (var leg in knownLegs.OrderBy(l => l.FromRef).ThenBy(l => l.ToRef))
            sb.Append(leg.FromRef).Append('>').Append(leg.ToRef).Append('=').Append(leg.Miles).Append(';');
        return sb.ToString();
    }
}
