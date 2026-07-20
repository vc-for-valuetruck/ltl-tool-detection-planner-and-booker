using LtlTool.Api.Features.Ltl.Optimization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Optimization;

/// <summary>
/// Behavior + property tests for the OR-Tools capacity/cost solver. Fixtures are deterministic —
/// every leg carries an Alvys mileage so no estimate labeling leaks in — and assert the two
/// invariants leadership relies on: (1) the solver never packs past the trailer's weight/pallet
/// envelope, and (2) it respects its wall-clock budget. Nothing here fetches or invents data.
/// </summary>
[Trait("Category", "Optimization")]
public sealed class OrToolsCapacityCostSolverTests
{
    private static OrToolsCapacityCostSolver BuildSolver(CapacityCostSolverOptions? opts = null)
    {
        var distanceOptions = Microsoft.Extensions.Options.Options.Create(new DistanceMatrixOptions());
        var distances = new AlvysDistanceMatrixProvider(distanceOptions);
        return new OrToolsCapacityCostSolver(
            distances,
            Microsoft.Extensions.Options.Options.Create(opts ?? new CapacityCostSolverOptions()),
            TimeProvider.System,
            NullLogger<OrToolsCapacityCostSolver>.Instance);
    }

    private static CapacityCostCandidate Candidate(
        string loadRef,
        decimal? weight,
        int? pallets,
        decimal? revenue,
        decimal miles,
        bool mandatory = false)
        => new(
            LoadRef: loadRef,
            WeightLbs: weight,
            Pallets: pallets,
            Revenue: revenue,
            Miles: miles,
            // Coordinates so same-city legs compute to ~0 rather than hitting the coarse fallback
            // (Alvys exposes no lat/long today; these deterministic fixtures supply them explicitly).
            Origin: new GeoPoint("Laredo", "TX", 27.5306, -99.4803),
            Destination: new GeoPoint("Dallas", "TX", 32.7767, -96.7970),
            Mandatory: mandatory);

    [Fact]
    public async Task Empty_candidate_list_is_unsolved()
    {
        var solver = BuildSolver();

        var result = await solver.SolveAsync(
            new CapacityCostRequest(new TrailerCapacitySpec(45_000m, 26, null), []));

        Assert.False(result.Solved);
        Assert.Null(result.Plan);
    }

    [Fact]
    public async Task Golden_all_fit_selects_every_candidate()
    {
        var solver = BuildSolver();
        var request = new CapacityCostRequest(
            new TrailerCapacitySpec(45_000m, 26, null),
            [
                Candidate("PARENT", 10_000m, 6, 2_000m, 450m, mandatory: true),
                Candidate("SIB-A", 8_000m, 5, 1_500m, 450m),
                Candidate("SIB-B", 7_000m, 4, 1_200m, 450m),
            ]);

        var result = await solver.SolveAsync(request);

        Assert.True(result.Solved);
        Assert.NotNull(result.Plan);
        Assert.Equal(
            new[] { "PARENT", "SIB-A", "SIB-B" }.OrderBy(x => x),
            result.Plan!.SelectedLoadRefs.OrderBy(x => x));
    }

    [Fact]
    public async Task Golden_weight_over_capacity_drops_optional_but_keeps_mandatory()
    {
        var solver = BuildSolver();
        // Parent alone is 40k; each sibling is 40k. Trailer holds 45k — only one load can ride,
        // and the parent is mandatory, so both siblings must be dropped.
        var request = new CapacityCostRequest(
            new TrailerCapacitySpec(45_000m, 26, null),
            [
                Candidate("PARENT", 40_000m, 10, 3_000m, 450m, mandatory: true),
                Candidate("SIB-A", 40_000m, 10, 2_000m, 450m),
                Candidate("SIB-B", 40_000m, 10, 2_000m, 450m),
            ]);

        var result = await solver.SolveAsync(request);

        Assert.True(result.Solved);
        Assert.NotNull(result.Plan);
        Assert.Contains("PARENT", result.Plan!.SelectedLoadRefs);
        Assert.DoesNotContain("SIB-A", result.Plan.SelectedLoadRefs);
        Assert.DoesNotContain("SIB-B", result.Plan.SelectedLoadRefs);
    }

    [Theory]
    [InlineData(45_000, 26)]
    [InlineData(30_000, 12)]
    [InlineData(20_000, 8)]
    public async Task Property_selection_never_violates_capacity(int maxWeight, int maxPallets)
    {
        var solver = BuildSolver();
        var candidates = new List<CapacityCostCandidate>
        {
            Candidate("PARENT", 9_000m, 5, 2_000m, 400m, mandatory: true),
            Candidate("SIB-A", 12_000m, 7, 1_800m, 400m),
            Candidate("SIB-B", 15_000m, 9, 2_400m, 400m),
            Candidate("SIB-C", 11_000m, 6, 1_500m, 400m),
            Candidate("SIB-D", 8_000m, 4, 1_100m, 400m),
        };
        var request = new CapacityCostRequest(
            new TrailerCapacitySpec(maxWeight, maxPallets, null), candidates);

        var result = await solver.SolveAsync(request);

        Assert.True(result.Solved);
        var byRef = candidates.ToDictionary(c => c.LoadRef);
        var usedWeight = result.Plan!.SelectedLoadRefs.Sum(r => byRef[r].WeightLbs ?? 0m);
        var usedPallets = result.Plan.SelectedLoadRefs.Sum(r => byRef[r].Pallets ?? 0);

        Assert.True(usedWeight <= maxWeight, $"weight {usedWeight} exceeded cap {maxWeight}");
        Assert.True(usedPallets <= maxPallets, $"pallets {usedPallets} exceeded cap {maxPallets}");
    }

    [Fact]
    public async Task Time_limit_is_respected()
    {
        var solver = BuildSolver(new CapacityCostSolverOptions { TimeLimitSeconds = 2 });
        // An instance large enough that guided local search would keep improving if unbounded, yet
        // still feasible within the budget so we can assert the wall-clock limit actually bounds it.
        var candidates = new List<CapacityCostCandidate>
        {
            Candidate("PARENT", 5_000m, 3, 2_000m, 400m, mandatory: true),
        };
        for (var i = 0; i < 6; i++)
            candidates.Add(Candidate($"SIB-{i}", 4_000m, 2, 1_000m + i * 50m, 300m + i * 20m));

        var request = new CapacityCostRequest(new TrailerCapacitySpec(45_000m, 26, null), candidates);

        var start = DateTimeOffset.UtcNow;
        var result = await solver.SolveAsync(request);
        var elapsed = DateTimeOffset.UtcNow - start;

        Assert.True(result.Solved);
        // 2s budget + generous slack for model build + native interop; asserts the limit is wired
        // (an unbounded guided-local-search on this instance would not return in single-digit seconds).
        Assert.True(elapsed < TimeSpan.FromSeconds(8), $"solve took {elapsed.TotalSeconds:N1}s");
    }

    [Fact]
    public async Task Missing_weight_is_not_treated_as_zero_capacity_draw_but_pallets_still_bound()
    {
        var solver = BuildSolver();
        // Weight unknown on siblings — solver cannot charge weight capacity for them, but the pallet
        // dimension still binds. Cap of 6 pallets, parent takes 4, so at most one 3-pallet sibling fits.
        var request = new CapacityCostRequest(
            new TrailerCapacitySpec(45_000m, 6, null),
            [
                Candidate("PARENT", 10_000m, 4, 2_000m, 400m, mandatory: true),
                Candidate("SIB-A", null, 3, 1_500m, 400m),
                Candidate("SIB-B", null, 3, 1_400m, 400m),
            ]);

        var result = await solver.SolveAsync(request);

        Assert.True(result.Solved);
        var selectedSiblings = result.Plan!.SelectedLoadRefs.Where(r => r.StartsWith("SIB")).ToList();
        Assert.True(selectedSiblings.Count <= 1, "pallet cap should allow at most one sibling");
        Assert.Contains("PARENT", result.Plan.SelectedLoadRefs);
    }
}
