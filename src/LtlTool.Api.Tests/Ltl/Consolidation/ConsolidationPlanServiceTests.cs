using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.Consolidation;
using LtlTool.Api.Features.Ltl.Optimization;
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
            LtlTestFactory.Options(), LtlTestFactory.Clock());

        return new ConsolidationPlanService(
            loads,
            Microsoft.Extensions.Options.Options.Create(overrides ?? DefaultOptions()),
            LtlTestFactory.Options(),
            LtlTestFactory.Clock(),
            LtlTestFactory.StaticPolicyReader(overrides ?? DefaultOptions()),
            new NullTrailerFitService(TimeProvider.System),
            new LtlTool.Api.Features.Ltl.Optimization.NullStopSequencer(LtlTestFactory.Clock()));
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
            LtlTestFactory.Options(), LtlTestFactory.Clock());
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
            LtlTestFactory.StaticPolicyReader(options),
            new NullTrailerFitService(TimeProvider.System),
            new LtlTool.Api.Features.Ltl.Optimization.NullStopSequencer(LtlTestFactory.Clock()));

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
            LtlTestFactory.Options(), LtlTestFactory.Clock());
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
            LtlTestFactory.StaticPolicyReader(options),
            new NullTrailerFitService(TimeProvider.System),
            new LtlTool.Api.Features.Ltl.Optimization.NullStopSequencer(LtlTestFactory.Clock()));

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
            LtlTestFactory.Options(), LtlTestFactory.Clock());
        var service = new ConsolidationPlanService(
            loads,
            Microsoft.Extensions.Options.Options.Create(DefaultOptions()),
            LtlTestFactory.Options(),
            LtlTestFactory.Clock(),
            LtlTestFactory.StaticPolicyReader(DefaultOptions()),
            new NullTrailerFitService(TimeProvider.System),
            new LtlTool.Api.Features.Ltl.Optimization.NullStopSequencer(LtlTestFactory.Clock()));

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
            LtlTestFactory.Options(), LtlTestFactory.Clock());
        var service = new ConsolidationPlanService(
            loads,
            Microsoft.Extensions.Options.Options.Create(DefaultOptions()),
            LtlTestFactory.Options(),
            LtlTestFactory.Clock(),
            LtlTestFactory.StaticPolicyReader(DefaultOptions()),
            new NullTrailerFitService(TimeProvider.System),
            new LtlTool.Api.Features.Ltl.Optimization.NullStopSequencer(LtlTestFactory.Clock()));

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

    /// <summary>
    /// Regression for the Dock UI incident where loads 1002054/1002196 (Vertiv Mexico freight
    /// picking up in the Santa Catarina, NL / Monterrey cluster and delivering to the Dallas
    /// metro) surfaced correctly as Auto-suggest candidates but then failed Review/Combine with
    /// "not on the LAREDO_TO_DALLAS corridor" for both parent and sibling. Root cause: this
    /// service had its own stale copy of the corridor-nearness check that required
    /// State-equality-first, which structurally excludes NL (Mexico) pickups even though the
    /// LAREDO warehouse's NearbyCities whitelist explicitly includes the Monterrey cluster.
    /// ConsolidationCandidateService got the city-first fix in #100; this service did not. Both
    /// now delegate to the shared CorridorGeography.IsNear so they can never drift again.
    /// </summary>
    [Fact]
    public async Task Cross_border_Monterrey_cluster_parent_and_sibling_combine_without_corridor_blockers()
    {
        var parent = Load(
            "1002054", "Vertiv Mexico VERUSD CO Data2Logistics",
            "Santa Catarina", "NL", "Fort Worth", "TX", ParentPickup,
            rate: 12502.5m, mileage: 582m, weight: 28047m);
        var sibling = Load(
            "1002196", "Vertiv Mexico VERUSD CO Data2Logistics",
            "Santa Catarina", "NL", "Irving", "TX", ParentPickup.AddHours(6),
            rate: 12514.6m, mileage: 604m, weight: 28047m);

        var client = new StatefulAlvysClient(parent, sibling);
        var loads = new LtlLoadService(
            client, LtlTestFactory.Normalizer(), LtlTestFactory.Visibility(),
            LtlTestFactory.AccessorialAnalyzer(), new NullAccessorialSignalExtractor(),
            LtlTestFactory.Options(), LtlTestFactory.Clock());
        var service = new ConsolidationPlanService(
            loads,
            Microsoft.Extensions.Options.Options.Create(DefaultOptions()),
            LtlTestFactory.Options(),
            LtlTestFactory.Clock(),
            LtlTestFactory.StaticPolicyReader(DefaultOptions()),
            new NullTrailerFitService(TimeProvider.System),
            new LtlTool.Api.Features.Ltl.Optimization.NullStopSequencer(LtlTestFactory.Clock()));

        var response = await service.BuildAsync(
            new ConsolidationPlanRequest
            {
                ParentLoadId = "1002054",
                SiblingLoadIds = ["1002196"],
            },
            default);

        Assert.Empty(response.Blockers);
        Assert.Single(response.Siblings);
        Assert.Equal("1002196", response.Siblings[0].LoadNumber);
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
            LtlTestFactory.Options(), LtlTestFactory.Clock());
        var service = new ConsolidationPlanService(
            loads,
            Microsoft.Extensions.Options.Options.Create(options),
            LtlTestFactory.Options(),
            LtlTestFactory.Clock(),
            LtlTestFactory.StaticPolicyReader(options),
            new NullTrailerFitService(TimeProvider.System),
            new LtlTool.Api.Features.Ltl.Optimization.NullStopSequencer(LtlTestFactory.Clock()));

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

    private static ConsolidationPlanService BuildServiceWithTrips(
        StatefulAlvysClient client,
        ConsolidationOptions options)
    {
        var loads = new LtlLoadService(
            client, LtlTestFactory.Normalizer(), LtlTestFactory.Visibility(),
            LtlTestFactory.AccessorialAnalyzer(), new NullAccessorialSignalExtractor(),
            LtlTestFactory.Options(), LtlTestFactory.Clock());
        return new ConsolidationPlanService(
            loads,
            Microsoft.Extensions.Options.Options.Create(options),
            LtlTestFactory.Options(),
            LtlTestFactory.Clock(),
            LtlTestFactory.StaticPolicyReader(options),
            new NullTrailerFitService(TimeProvider.System),
            new LtlTool.Api.Features.Ltl.Optimization.NullStopSequencer(LtlTestFactory.Clock()));
    }

    private static StatefulAlvysClient VerdefPairWithTrips(
        decimal parentTripValue, decimal parentLoadedMiles,
        decimal siblingTripValue, decimal siblingLoadedMiles)
    {
        var parent = Load(
            "L-100234", "Verdef",
            "Laredo", "TX", "Dallas", "TX", ParentPickup,
            rate: 4100m, mileage: 1072m, weight: 14200m);
        var sibling = Load(
            "L-100241", "Verdef",
            "Laredo", "TX", "Dallas", "TX", ParentPickup.AddHours(3),
            rate: 4100m, mileage: 500m, weight: 4100m);

        var client = new StatefulAlvysClient(parent, sibling);
        client.Trips.Add(new AlvysTrip
        {
            Id = "T-100234",
            LoadNumber = "L-100234",
            TripValue = new AlvysMoney { Amount = parentTripValue, Currency = "USD" },
            LoadedMileage = new AlvysDistanceMeasurement
            {
                Distance = new AlvysDistance { Value = parentLoadedMiles, UnitOfMeasure = "Miles" },
            },
        });
        client.Trips.Add(new AlvysTrip
        {
            Id = "T-100241",
            LoadNumber = "L-100241",
            TripValue = new AlvysMoney { Amount = siblingTripValue, Currency = "USD" },
            LoadedMileage = new AlvysDistanceMeasurement
            {
                Distance = new AlvysDistance { Value = siblingLoadedMiles, UnitOfMeasure = "Miles" },
            },
        });
        return client;
    }

    private static ConsolidationOptions VerdefAllowedOptions(decimal? redRpmFloor = null)
    {
        var options = DefaultOptions();
        if (redRpmFloor is not null) options.RedRpmThresholdPerMile = redRpmFloor.Value;
        options.CustomerPolicies.Add(new()
        {
            Customer = "Verdef",
            Tier = CustomerConsolidationTier.Allowed,
        });
        return options;
    }

    [Fact]
    public async Task Rpm_warning_is_ok_when_combined_driver_rpm_clears_the_floor()
    {
        // Combined driver RPM = 8100 / 1050 = 7.71, well above the default 1.50 floor.
        var client = VerdefPairWithTrips(4200m, 1050m, 3900m, 490m);
        var service = BuildServiceWithTrips(client, VerdefAllowedOptions());

        var response = await service.BuildAsync(
            new ConsolidationPlanRequest { ParentLoadId = "L-100234", SiblingLoadIds = ["L-100241"] },
            default);

        Assert.NotNull(response.RpmWarning);
        Assert.Equal(ConsolidationRpmWarningStatus.Ok, response.RpmWarning!.Status);
        Assert.Equal(7.71m, response.RpmWarning.RpmPerMile);
        Assert.Equal(1.50m, response.RpmWarning.ThresholdPerMile);
    }

    [Fact]
    public async Task Rpm_warning_is_below_when_combined_driver_rpm_is_under_the_configured_floor()
    {
        // Same 7.71 combined driver RPM, but a floor set above it flips the chip to Below.
        var client = VerdefPairWithTrips(4200m, 1050m, 3900m, 490m);
        var service = BuildServiceWithTrips(client, VerdefAllowedOptions(redRpmFloor: 10.00m));

        var response = await service.BuildAsync(
            new ConsolidationPlanRequest { ParentLoadId = "L-100234", SiblingLoadIds = ["L-100241"] },
            default);

        Assert.NotNull(response.RpmWarning);
        Assert.Equal(ConsolidationRpmWarningStatus.Below, response.RpmWarning!.Status);
        Assert.Equal(7.71m, response.RpmWarning.RpmPerMile);
        Assert.Equal(10.00m, response.RpmWarning.ThresholdPerMile);
    }

    [Fact]
    public async Task Rpm_warning_is_unavailable_and_never_zero_when_driver_data_is_missing()
    {
        // No trips seeded → combined driver RPM is null. The chip must be gray/Unavailable with a
        // null RPM (never coerced to 0), so the SPA is honest rather than implying a below-floor.
        var parent = Load(
            "L-100234", "Verdef", "Laredo", "TX", "Dallas", "TX", ParentPickup,
            rate: 4100m, mileage: 1072m, weight: 14200m);
        var sibling = Load(
            "L-100241", "Verdef", "Laredo", "TX", "Dallas", "TX", ParentPickup.AddHours(3),
            rate: 4100m, mileage: 500m, weight: 4100m);
        var client = new StatefulAlvysClient(parent, sibling); // no trips
        var service = BuildServiceWithTrips(client, VerdefAllowedOptions());

        var response = await service.BuildAsync(
            new ConsolidationPlanRequest { ParentLoadId = "L-100234", SiblingLoadIds = ["L-100241"] },
            default);

        Assert.NotNull(response.RpmWarning);
        Assert.Equal(ConsolidationRpmWarningStatus.Unavailable, response.RpmWarning!.Status);
        Assert.Null(response.RpmWarning.RpmPerMile);
    }

    [Fact]
    public async Task Accessorial_pre_checks_cover_the_parent_and_each_included_sibling()
    {
        var client = VerdefPairWithTrips(4200m, 1050m, 3900m, 490m);
        var service = BuildServiceWithTrips(client, VerdefAllowedOptions());

        var response = await service.BuildAsync(
            new ConsolidationPlanRequest { ParentLoadId = "L-100234", SiblingLoadIds = ["L-100241"] },
            default);

        Assert.Equal(2, response.AccessorialPreChecks.Count);
        var parentCheck = Assert.Single(response.AccessorialPreChecks, p => p.IsParent);
        Assert.Equal("L-100234", parentCheck.LoadNumber);
        var siblingCheck = Assert.Single(response.AccessorialPreChecks, p => !p.IsParent);
        Assert.Equal("L-100241", siblingCheck.LoadNumber);
        // No dollar amounts ever ride an accessorial candidate.
        foreach (var check in response.AccessorialPreChecks)
        {
            foreach (var candidate in check.Candidates)
            {
                Assert.DoesNotContain("$", candidate.Reason);
            }
        }
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
            LtlTestFactory.Options(), LtlTestFactory.Clock());
        var service = new ConsolidationPlanService(
            loads,
            Microsoft.Extensions.Options.Options.Create(options),
            LtlTestFactory.Options(),
            LtlTestFactory.Clock(),
            LtlTestFactory.StaticPolicyReader(options),
            new NullTrailerFitService(TimeProvider.System),
            new LtlTool.Api.Features.Ltl.Optimization.NullStopSequencer(LtlTestFactory.Clock()));

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
