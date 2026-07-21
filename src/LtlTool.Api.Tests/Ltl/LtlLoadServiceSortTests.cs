using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Phase 0 stability guardrail: locks the "nulls sort last in both directions" behavior at
/// <see cref="LtlLoadService"/> so missing revenue / mileage / weight never floats to the top of
/// the search grid. Regression from PR #24; the roadmap explicitly names this as a stability
/// success criterion.
/// </summary>
public sealed class LtlLoadServiceSortTests
{
    private static LtlLoadService Build(FakeAlvysClient client) =>
        new(client, LtlTestFactory.Normalizer(), LtlTestFactory.Visibility(),
            LtlTestFactory.AccessorialAnalyzer(), new NullAccessorialSignalExtractor(),
            LtlTestFactory.Options(), LtlTestFactory.Clock());

    private static AlvysLoad Load(string number, decimal? rate, decimal? weight, decimal? mileage) => new()
    {
        Id = number,
        LoadNumber = number,
        Status = "Available",
        CustomerRate = rate,
        Weight = weight,
        CustomerMileage = mileage,
    };

    private static FakeAlvysClient WithLoads() => new()
    {
        // Deliberate interleaving of populated + null values so a naive sort would surface nulls
        // (which compare as "less than" any value) at the top of ascending order.
        Loads =
        [
            Load("A", rate: 300m, weight: 1000m, mileage: 500m),
            Load("B", rate: null,  weight: 2000m, mileage: 300m),
            Load("C", rate: 100m, weight: null,  mileage: 400m),
            Load("D", rate: 200m, weight: 3000m, mileage: null),
        ],
    };

    [Fact]
    public async Task Revenue_sort_ascending_keeps_nulls_last()
    {
        var response = await Build(WithLoads()).SearchAsync(
            new LtlSearchQuery { Sort = LtlSortField.Revenue, SortDescending = false }, default);

        var order = response.Items.Select(i => i.LoadNumber).ToArray();
        // Populated values ascend (100, 200, 300), then the null (B) trails.
        Assert.Equal(new[] { "C", "D", "A", "B" }, order);
    }

    [Fact]
    public async Task Revenue_sort_descending_keeps_nulls_last()
    {
        var response = await Build(WithLoads()).SearchAsync(
            new LtlSearchQuery { Sort = LtlSortField.Revenue, SortDescending = true }, default);

        var order = response.Items.Select(i => i.LoadNumber).ToArray();
        // Populated values descend (300, 200, 100), then the null (B) trails — NOT at the top.
        Assert.Equal(new[] { "A", "D", "C", "B" }, order);
    }

    [Fact]
    public async Task Weight_sort_descending_keeps_null_last_not_first()
    {
        var response = await Build(WithLoads()).SearchAsync(
            new LtlSearchQuery { Sort = LtlSortField.Weight, SortDescending = true }, default);

        var order = response.Items.Select(i => i.LoadNumber).ToArray();
        // D has the highest weight, then B, then A; C's weight is null and trails.
        Assert.Equal(new[] { "D", "B", "A", "C" }, order);
    }

    [Fact]
    public async Task Distance_sort_ascending_null_mileage_does_not_float_to_top()
    {
        var response = await Build(WithLoads()).SearchAsync(
            new LtlSearchQuery { Sort = LtlSortField.Distance, SortDescending = false }, default);

        var order = response.Items.Select(i => i.LoadNumber).ToArray();
        // Populated mileage ascends (300, 400, 500); D's null trails at the bottom.
        Assert.Equal(new[] { "B", "C", "A", "D" }, order);
    }

    [Fact]
    public async Task UrgencyScore_sort_descending_surfaces_the_missing_rate_load_first()
    {
        // "B" has no rate -> a blocking MISSING_RATE exception -> the highest urgency score of the set.
        // The rest carry a rate and no other risk signal, so they all score 0 and tie.
        var response = await Build(WithLoads()).SearchAsync(
            new LtlSearchQuery { Sort = LtlSortField.UrgencyScore, SortDescending = true }, default);

        Assert.Equal("B", response.Items[0].LoadNumber);
    }
}
