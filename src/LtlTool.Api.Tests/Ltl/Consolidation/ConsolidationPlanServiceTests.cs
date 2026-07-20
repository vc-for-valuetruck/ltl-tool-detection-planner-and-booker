using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.Consolidation;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Consolidation;

/// <summary>
/// Behavior tests for the plan-preview service. Grounded in the yard-visit examples:
/// Verdef parent + Verdef sibling + Masonite sibling routing through Laredo → Dallas.
/// Every scenario asserts the plan preview is honest about missing data and never invents
/// values.
/// </summary>
public sealed class ConsolidationPlanServiceTests
{
    private static readonly DateTimeOffset ParentPickup =
        new(2026, 7, 17, 8, 0, 0, TimeSpan.Zero);

    private static ConsolidationOptions DefaultOptions() => new();

    private static ConsolidationPlanService BuildService(
        FakeAlvysClient client,
        ConsolidationOptions? overrides = null)
    {
        var loads = new LtlLoadService(
            client,
            LtlTestFactory.Normalizer(),
            LtlTestFactory.Visibility(),
            LtlTestFactory.AccessorialAnalyzer(),
            new NullAccessorialSignalExtractor(),
            LtlTestFactory.Options());

        return new ConsolidationPlanService(
            loads,
            Microsoft.Extensions.Options.Options.Create(overrides ?? DefaultOptions()),
            LtlTestFactory.Options(),
            LtlTestFactory.Clock(),
            LtlTestFactory.StaticPolicyReader(overrides ?? DefaultOptions()));
    }

    private static AlvysLoad Load(
        string id,
        string customer,
        string originCity,
        string originState,
        string destCity,
        string destState,
        DateTimeOffset pickup,
        decimal? rate = null,
        decimal? mileage = null,
        decimal? weight = null)
        => new()
        {
            Id = id,
            LoadNumber = id,
            Status = "Available",
            CustomerName = customer,
            CustomerRate = rate,
            CustomerMileage = mileage,
            Weight = weight,
            Stops =
            [
                new AlvysLoadStop
                {
                    StopType = "Pickup",
                    Address = new AlvysAddress { City = originCity, State = originState },
                    ScheduledStart = pickup,
                    Sequence = 1,
                },
                new AlvysLoadStop
                {
                    StopType = "Delivery",
                    Address = new AlvysAddress { City = destCity, State = destState },
                    ScheduledStart = pickup.AddDays(1),
                    Sequence = 2,
                },
            ],
        };

    [Fact]
    public async Task Missing_parent_id_throws_bad_request()
    {
        var client = new FakeAlvysClient();
        var service = BuildService(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.BuildAsync(
                new ConsolidationPlanRequest { ParentLoadId = "", SiblingLoadIds = ["L-2"] },
                default));
    }

    [Fact]
    public async Task Missing_siblings_throws_bad_request()
    {
        var client = new FakeAlvysClient();
        var service = BuildService(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.BuildAsync(
                new ConsolidationPlanRequest { ParentLoadId = "L-1", SiblingLoadIds = [] },
                default));
    }

    [Fact]
    public async Task Unknown_corridor_throws_bad_request()
    {
        var client = new FakeAlvysClient();
        var service = BuildService(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.BuildAsync(
                new ConsolidationPlanRequest
                {
                    ParentLoadId = "L-1",
                    SiblingLoadIds = ["L-2"],
                    CorridorCode = "NOT_A_CORRIDOR",
                },
                default));
    }

    [Fact]
    public async Task Unresolvable_parent_throws_bad_request()
    {
        var client = new FakeAlvysClient(); // LoadDetail is null → not found
        var service = BuildService(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.BuildAsync(
                new ConsolidationPlanRequest { ParentLoadId = "L-MISSING", SiblingLoadIds = ["L-2"] },
                default));
    }

    [Fact]
    public async Task Sibling_that_is_actually_the_parent_id_is_rejected()
    {
        var parent = Load(
            "L-100234", "Verdef",
            "Laredo", "TX", "Dallas", "TX", ParentPickup,
            rate: 4100m, mileage: 500m);
        var client = new FakeAlvysClient { LoadDetail = parent };
        var service = BuildService(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.BuildAsync(
                new ConsolidationPlanRequest
                {
                    ParentLoadId = "L-100234",
                    SiblingLoadIds = ["L-100234"],
                },
                default));
    }

    [Fact]
    public async Task Happy_path_computes_driver_rpm_from_trip_value_and_loaded_miles()
    {
        // Corrected 2026-07-18 per Reuben transcript + empirical MCP verification (see
        // docs/ALVYS_API_DECISIONS.md "Empirical findings, Finding 3"). RPM is the driver-
        // facing number: Trip.TripValue.Amount / Trip.LoadedMileage.Distance.Value. The
        // customer-side (revenue, customer mileage) is retained for operator context but is
        // NOT the RPM math.
        var parent = Load(
            "L-100234", "Verdef",
            "Laredo", "TX", "Dallas", "TX", ParentPickup,
            rate: 4100m, mileage: 1072m, weight: 14200m);
        var sibling = Load(
            "L-100241", "Verdef",
            "Laredo", "TX", "Dallas", "TX", ParentPickup.AddHours(3),
            rate: 4100m, mileage: 500m, weight: 4100m);

        var client = new StatefulAlvysClient(parent, sibling);

        // Seed trips with the driver-facing shape that lives on Trip.TripValue and
        // Trip.LoadedMileage. LtlLoadService.FetchTripEconomicsForLoadAsync pulls these via
        // SearchTripsAsync -> the FakeAlvysClient default returns the seeded Trips list
        // filtered by LoadNumber caller-side.
        client.Trips.Add(new AlvysTrip
        {
            Id = "T-100234",
            LoadNumber = "L-100234",
            TripValue = new AlvysMoney { Amount = 4200m, Currency = "USD" },
            LoadedMileage = new AlvysDistanceMeasurement
            {
                Distance = new AlvysDistance { Value = 1050m, UnitOfMeasure = "Miles" },
            },
        });
        client.Trips.Add(new AlvysTrip
        {
            Id = "T-100241",
            LoadNumber = "L-100241",
            TripValue = new AlvysMoney { Amount = 3900m, Currency = "USD" },
            LoadedMileage = new AlvysDistanceMeasurement
            {
                Distance = new AlvysDistance { Value = 490m, UnitOfMeasure = "Miles" },
            },
        });

        var loads = new LtlLoadService(
            client, LtlTestFactory.Normalizer(), LtlTestFactory.Visibility(),
            LtlTestFactory.AccessorialAnalyzer(), new NullAccessorialSignalExtractor(),
            LtlTestFactory.Options());
        var options = DefaultOptions();
        options.CustomerPolicies.Add(new()
        {
            Customer = "Verdef",
            Tier = CustomerConsolidationTier.Allowed,
        });
        var service = new ConsolidationPlanService(
            loads,
            Microsoft.Extensions.Options.Options.Create(options),
            LtlTestFactory.Options(),
            LtlTestFactory.Clock(),
            LtlTestFactory.StaticPolicyReader(options));

        var response = await service.BuildAsync(
            new ConsolidationPlanRequest
            {
                ParentLoadId = "L-100234",
                SiblingLoadIds = ["L-100241"],
            },
            default);

        Assert.Empty(response.Blockers);
        Assert.Equal("LAREDO_TO_DALLAS", response.CorridorCode);
        Assert.Equal("L-100234", response.Parent.LoadNumber);
        var included = Assert.Single(response.Siblings);
        Assert.Equal("L-100241", included.LoadNumber);

        // Customer-side, kept for operator context.
        Assert.Equal(8200m, response.CombinedRevenue);
        Assert.Equal(1072m, response.LinehaulMiles);

        // Driver-side — the numbers RPM is actually computed against.
        Assert.Equal(1050m, response.DriverLoadedMiles);
        Assert.Equal(8100m, response.CombinedDriverTripValue);
        // 8100 / 1050 = 7.7142… rounded to 7.71. Reuben's yard-visit "~$5/mi combined" example
        // was different numbers but the formula matches.
        Assert.Equal(7.71m, response.CombinedRevenuePerMile);
    }

    [Fact]
    public async Task Rpm_stays_null_when_no_driver_trip_data_available()
    {
        // Anti-failure map 3o: silent misses. When Alvys returns no trips for these load
        // numbers, the plan must NOT invent a driver RPM by falling back to customer-side
        // math. It reports null and shows dashes in the click card.
        var parent = Load(
            "L-100234", "Verdef",
            "Laredo", "TX", "Dallas", "TX", ParentPickup,
            rate: 4100m, mileage: 1072m, weight: 14200m);
        var sibling = Load(
            "L-100241", "Verdef",
            "Laredo", "TX", "Dallas", "TX", ParentPickup.AddHours(3),
            rate: 4100m, mileage: 500m, weight: 4100m);

        var client = new StatefulAlvysClient(parent, sibling); // no trips seeded
        var loads = new LtlLoadService(
            client, LtlTestFactory.Normalizer(), LtlTestFactory.Visibility(),
            LtlTestFactory.AccessorialAnalyzer(), new NullAccessorialSignalExtractor(),
            LtlTestFactory.Options());
        var options = DefaultOptions();
        options.CustomerPolicies.Add(new()
        {
            Customer = "Verdef",
            Tier = CustomerConsolidationTier.Allowed,
        });
        var service = new ConsolidationPlanService(
            loads,
            Microsoft.Extensions.Options.Options.Create(options),
            LtlTestFactory.Options(),
            LtlTestFactory.Clock(),
            LtlTestFactory.StaticPolicyReader(options));

        var response = await service.BuildAsync(
            new ConsolidationPlanRequest
            {
                ParentLoadId = "L-100234",
                SiblingLoadIds = ["L-100241"],
            },
            default);

        Assert.Empty(response.Blockers);
        Assert.Equal(8200m, response.CombinedRevenue);
        Assert.Equal(1072m, response.LinehaulMiles);
        Assert.Null(response.DriverLoadedMiles);
        Assert.Null(response.CombinedDriverTripValue);
        Assert.Null(response.CombinedRevenuePerMile);

        // Click card should visibly say the driver RPM is unknown, not fake a number.
        Assert.Contains("Combined driver RPM:", response.ClickCard.PlainText);
        Assert.Contains("needs both combined driver trip value and parent loaded miles",
            response.ClickCard.PlainText);
    }

    [Fact]
    public async Task Sibling_missing_weight_surfaces_visual_verify_caution()
    {
        var parent = Load(
            "L-100234", "Verdef",
            "Laredo", "TX", "Dallas", "TX", ParentPickup,
            rate: 4100m, mileage: 1072m, weight: 14200m);
        var sibling = Load(
            "L-100237", "Verdef",
            "Laredo", "TX", "Dallas", "TX", ParentPickup.AddHours(3),
            rate: 4100m, mileage: 500m,
            weight: null); // missing weight

        var client = new StatefulAlvysClient(parent, sibling);
        var loads = new LtlLoadService(
            client, LtlTestFactory.Normalizer(), LtlTestFactory.Visibility(),
            LtlTestFactory.AccessorialAnalyzer(), new NullAccessorialSignalExtractor(),
            LtlTestFactory.Options());
        var service = new ConsolidationPlanService(
            loads,
            Microsoft.Extensions.Options.Options.Create(DefaultOptions()),
            LtlTestFactory.Options(),
            LtlTestFactory.Clock(),
            LtlTestFactory.StaticPolicyReader(DefaultOptions()));

        var response = await service.BuildAsync(
            new ConsolidationPlanRequest
            {
                ParentLoadId = "L-100234",
                SiblingLoadIds = ["L-100237"],
            },
            default);

        var included = Assert.Single(response.Siblings);
        Assert.Null(included.WeightLbs);
        Assert.Contains(included.Cautions, c => c.Contains("visual verify"));
    }

    [Fact]
    public async Task Never_customer_sibling_becomes_a_blocker_and_is_not_included()
    {
        var parent = Load(
            "L-100234", "Verdef",
            "Laredo", "TX", "Dallas", "TX", ParentPickup,
            rate: 4100m, mileage: 1072m);
        var kroger = Load(
            "L-KROGER", "Kroger",
            "Laredo", "TX", "Fort Worth", "TX", ParentPickup,
            rate: 2000m);

        var client = new StatefulAlvysClient(parent, kroger);
        var loads = new LtlLoadService(
            client, LtlTestFactory.Normalizer(), LtlTestFactory.Visibility(),
            LtlTestFactory.AccessorialAnalyzer(), new NullAccessorialSignalExtractor(),
            LtlTestFactory.Options());
        var service = new ConsolidationPlanService(
            loads,
            Microsoft.Extensions.Options.Options.Create(DefaultOptions()),
            LtlTestFactory.Options(),
            LtlTestFactory.Clock(),
            LtlTestFactory.StaticPolicyReader(DefaultOptions()));

        var response = await service.BuildAsync(
            new ConsolidationPlanRequest
            {
                ParentLoadId = "L-100234",
                SiblingLoadIds = ["L-KROGER"],
            },
            default);

        Assert.Empty(response.Siblings);
        Assert.NotEmpty(response.Blockers);
        Assert.Contains(response.Blockers, b => b.Contains("Kroger"));
    }

    [Fact]
    public async Task Click_card_text_includes_sanctioned_alvys_instructions()
    {
        var parent = Load(
            "L-100234", "Verdef",
            "Laredo", "TX", "Dallas", "TX", ParentPickup,
            rate: 4100m, mileage: 1072m);
        var sibling = Load(
            "L-100241", "Verdef",
            "Laredo", "TX", "Dallas", "TX", ParentPickup.AddHours(3),
            rate: 4100m);

        var options = DefaultOptions();
        options.CustomerPolicies.Add(new()
        {
            Customer = "Verdef",
            Tier = CustomerConsolidationTier.Allowed,
        });

        var client = new StatefulAlvysClient(parent, sibling);
        var loads = new LtlLoadService(
            client, LtlTestFactory.Normalizer(), LtlTestFactory.Visibility(),
            LtlTestFactory.AccessorialAnalyzer(), new NullAccessorialSignalExtractor(),
            LtlTestFactory.Options());
        var service = new ConsolidationPlanService(
            loads,
            Microsoft.Extensions.Options.Options.Create(options),
            LtlTestFactory.Options(),
            LtlTestFactory.Clock(),
            LtlTestFactory.StaticPolicyReader(options));

        var response = await service.BuildAsync(
            new ConsolidationPlanRequest
            {
                ParentLoadId = "L-100234",
                SiblingLoadIds = ["L-100241"],
            },
            default);

        var text = response.ClickCard.PlainText;
        Assert.Contains("LTL CONSOLIDATION PLAN", text);
        Assert.Contains("Add stop → Waypoint", text);
        Assert.Contains("Loaded miles → set to 0", text);
        Assert.Contains("Trip References", text);
        Assert.Contains("Main Load Id = L-100234", text);
        // Trips report filter uses AND per Poornima's guidance.
        Assert.Contains("AND", text);
        // Include the sibling label as an "Open sibling load" instruction.
        Assert.Contains("Open sibling load L-100241", text);
    }

    [Fact]
    public async Task Preview_id_is_deterministic_shape()
    {
        var parent = Load(
            "L-100234", "Verdef",
            "Laredo", "TX", "Dallas", "TX", ParentPickup,
            rate: 4100m, mileage: 1072m);
        var sibling = Load(
            "L-100241", "Verdef",
            "Laredo", "TX", "Dallas", "TX", ParentPickup.AddHours(3),
            rate: 4100m);
        var options = DefaultOptions();
        options.CustomerPolicies.Add(new()
        {
            Customer = "Verdef",
            Tier = CustomerConsolidationTier.Allowed,
        });

        var client = new StatefulAlvysClient(parent, sibling);
        var loads = new LtlLoadService(
            client, LtlTestFactory.Normalizer(), LtlTestFactory.Visibility(),
            LtlTestFactory.AccessorialAnalyzer(), new NullAccessorialSignalExtractor(),
            LtlTestFactory.Options());
        var service = new ConsolidationPlanService(
            loads,
            Microsoft.Extensions.Options.Options.Create(options),
            LtlTestFactory.Options(),
            LtlTestFactory.Clock(),
            LtlTestFactory.StaticPolicyReader(options));

        var response = await service.BuildAsync(
            new ConsolidationPlanRequest
            {
                ParentLoadId = "L-100234",
                SiblingLoadIds = ["L-100241"],
            },
            default);

        Assert.StartsWith("plan-", response.PreviewId);
    }
}

/// <summary>
/// Stateful Alvys client that resolves detail lookups by matching load number/id. Extends
/// <see cref="FakeAlvysClient"/> so we only need to override the two lookup methods; every
/// other interface member falls back to the shared empty-response defaults.
/// </summary>
internal sealed class StatefulAlvysClient : FakeAlvysClient
{
    private readonly Dictionary<string, AlvysLoad> _byKey;

    public StatefulAlvysClient(params AlvysLoad[] items)
    {
        _byKey = items.ToDictionary(
            l => l.LoadNumber ?? l.Id ?? "",
            l => l,
            StringComparer.OrdinalIgnoreCase);

        // Seed the base Loads list so any code path that goes through SearchLoadsAsync also
        // sees the corpus (matches the FakeAlvysClient sweep behavior).
        Loads = _byKey.Values.ToList();
    }

    public override Task<AlvysLoad?> GetLoadByNumberAsync(string loadNumber, CancellationToken ct = default)
    {
        _byKey.TryGetValue(loadNumber, out var load);
        return Task.FromResult(load);
    }

    public override Task<AlvysLoad?> GetLoadAsync(LoadLookup lookup, CancellationToken ct = default)
    {
        var key = lookup.LoadNumber ?? lookup.Id ?? "";
        _byKey.TryGetValue(key, out var load);
        return Task.FromResult(load);
    }
}
