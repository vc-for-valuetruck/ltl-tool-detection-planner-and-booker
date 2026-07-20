using LtlTool.Api.Features.Ltl.Optimization;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Optimization;

/// <summary>
/// Tests the distance matrix provider's provenance contract: Alvys-supplied leg miles are used
/// verbatim and labeled <see cref="DistanceSource.Alvys"/>; every other cell is an estimate
/// (haversine-derated when coordinates exist, coarse fallback otherwise) and labeled
/// <see cref="DistanceSource.Estimated"/> so the UI never presents a guess as truth.
/// </summary>
[Trait("Category", "Optimization")]
public sealed class AlvysDistanceMatrixProviderTests
{
    private static AlvysDistanceMatrixProvider Build(DistanceMatrixOptions? opts = null)
        => new(Microsoft.Extensions.Options.Options.Create(opts ?? new DistanceMatrixOptions()));

    [Fact]
    public void Known_leg_is_used_verbatim_and_labeled_alvys()
    {
        var provider = Build();
        var points = new List<GeoPoint>
        {
            new("Laredo", "TX"),
            new("Dallas", "TX"),
        };
        var refs = new[] { "A", "B" };
        var known = new[] { new KnownLeg("A", "B", 431m) };

        var result = provider.Build(points, refs, known);

        Assert.Equal(431, result.Miles[0, 1]);
        Assert.Equal(DistanceSource.Alvys, result.Sources[0, 1]);
    }

    [Fact]
    public void Leg_without_alvys_or_coords_uses_labeled_fallback()
    {
        var provider = Build(new DistanceMatrixOptions { FallbackMilesWhenUnknown = 500 });
        var points = new List<GeoPoint> { new("Laredo", "TX"), new("Dallas", "TX") };
        var refs = new[] { "A", "B" };

        var result = provider.Build(points, refs, []);

        Assert.Equal(500, result.Miles[0, 1]);
        Assert.Equal(DistanceSource.Estimated, result.Sources[0, 1]);
        Assert.True(result.AnyEstimated);
    }

    [Fact]
    public void Leg_with_coordinates_uses_derated_haversine_estimate()
    {
        var provider = Build(new DistanceMatrixOptions { RoadCircuityFactor = 1.2 });
        var points = new List<GeoPoint>
        {
            new("Laredo", "TX", 27.5306, -99.4803),
            new("Dallas", "TX", 32.7767, -96.7970),
        };
        var refs = new[] { "A", "B" };

        var result = provider.Build(points, refs, []);

        Assert.Equal(DistanceSource.Estimated, result.Sources[0, 1]);
        // Straight-line Laredo→Dallas ≈ 385 mi; ×1.2 ≈ 462. Assert a sane band, not an exact value.
        Assert.InRange(result.Miles[0, 1], 400, 520);
    }

    [Fact]
    public void Self_leg_is_zero_and_not_estimated()
    {
        var provider = Build();
        var points = new List<GeoPoint> { new("Laredo", "TX"), new("Dallas", "TX") };
        var refs = new[] { "A", "B" };

        var result = provider.Build(points, refs, []);

        Assert.Equal(0, result.Miles[0, 0]);
        Assert.Equal(DistanceSource.Alvys, result.Sources[0, 0]);
    }

    [Fact]
    public void Mismatched_points_and_refs_throws()
    {
        var provider = Build();
        var points = new List<GeoPoint> { new("Laredo", "TX") };
        var refs = new[] { "A", "B" };

        Assert.Throws<ArgumentException>(() => provider.Build(points, refs, []));
    }
}
