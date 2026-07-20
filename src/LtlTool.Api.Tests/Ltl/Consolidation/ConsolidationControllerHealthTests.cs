using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.Consolidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Consolidation;

/// <summary>
/// Live-count endpoint. Uses FakeAlvysClient so it exercises the real
/// LtlLoadService.SearchAsync path (which is what the endpoint actually calls) rather than
/// mocking at the service level.
/// </summary>
public class ConsolidationControllerHealthTests
{
    [Fact]
    public async Task GetCorridorHealth_returns_open_load_count_from_canonical_city_pair()
    {
        var client = new FakeAlvysClient();
        // Two Laredo->Dallas loads (should count), one Houston->Dallas (should not).
        client.Loads.Add(BuildLoad("L-1", "Laredo", "TX", "Dallas", "TX"));
        client.Loads.Add(BuildLoad("L-2", "Laredo", "TX", "Dallas", "TX"));
        client.Loads.Add(BuildLoad("L-3", "Houston", "TX", "Dallas", "TX"));

        var controller = BuildController(client, PilotOptions());

        var result = await controller.GetCorridorHealth(default);
        var healths = Assert.IsAssignableFrom<IReadOnlyList<CorridorHealth>>(
            ((OkObjectResult)result.Result!).Value);

        var laredoDallas = Assert.Single(healths);
        Assert.Equal("LAREDO_TO_DALLAS", laredoDallas.Code);
        Assert.Equal(2, laredoDallas.OpenLoadCount);
        Assert.Equal("Laredo", laredoDallas.OriginCity);
        Assert.Equal("Dallas", laredoDallas.DestinationCity);
        Assert.False(laredoDallas.Truncated);
        // The first open load on the lane is exposed as a default seed hint so the UI can
        // auto-populate the pilot queue on tab-load. Present because loads exist; never fabricated.
        Assert.NotNull(laredoDallas.SeedLoadId);
        Assert.NotNull(laredoDallas.SeedLoadNumber);
    }

    [Fact]
    public async Task GetCorridorHealth_seed_hint_walks_non_canonical_origin_cities()
    {
        // The pilot queue must auto-populate whenever ANY legal origin city has a live parent —
        // not only the yard's own name. Here the only open load originates in Encinal (a Laredo
        // nearby-city), not "Laredo" proper. The seed hint must still surface it so the queue
        // auto-seeds instead of showing a blank screen in the live tenant.
        var client = new FakeAlvysClient();
        client.Loads.Add(BuildLoad("L-9", "Encinal", "TX", "Dallas", "TX"));

        var opts = new ConsolidationOptions
        {
            Warehouses =
            [
                new() { Code = "LAREDO", Name = "Laredo yard", State = "TX", NearbyCities = ["Laredo", "Encinal"] },
                new() { Code = "DALLAS", Name = "Dallas 154-door yard", State = "TX", NearbyCities = ["Dallas"] },
            ],
            Corridors =
            [
                new() { Code = "LAREDO_TO_DALLAS", OriginWarehouseCode = "LAREDO", DestinationWarehouseCode = "DALLAS", PickupWindowDays = 2, DeliveryWindowDays = 3 },
            ],
        };

        var controller = BuildController(client, opts);
        var result = await controller.GetCorridorHealth(default);
        var healths = Assert.IsAssignableFrom<IReadOnlyList<CorridorHealth>>(
            ((OkObjectResult)result.Result!).Value);

        var only = Assert.Single(healths);
        Assert.Equal(1, only.OpenLoadCount);
        // Seed hint found via the Encinal → Dallas lane even though "Laredo" proper is empty.
        Assert.Equal("L-9", only.SeedLoadId);
        Assert.Equal("L-9", only.SeedLoadNumber);
    }

    [Fact]
    public async Task GetCorridorHealth_returns_zero_when_no_matching_loads()
    {
        var client = new FakeAlvysClient();
        // Only irrelevant loads seeded \u2014 no Laredo->Dallas.
        client.Loads.Add(BuildLoad("L-1", "Houston", "TX", "Austin", "TX"));

        var controller = BuildController(client, PilotOptions());

        var result = await controller.GetCorridorHealth(default);
        var healths = Assert.IsAssignableFrom<IReadOnlyList<CorridorHealth>>(
            ((OkObjectResult)result.Result!).Value);

        var only = Assert.Single(healths);
        Assert.Equal(0, only.OpenLoadCount);
        // No open loads on the lane → no seed hint (never fabricated).
        Assert.Null(only.SeedLoadId);
        Assert.Null(only.SeedLoadNumber);
    }

    [Fact]
    public async Task GetCorridorHealth_skips_corridors_with_missing_warehouses()
    {
        var client = new FakeAlvysClient();
        var opts = new ConsolidationOptions
        {
            Warehouses = [new() { Code = "LAREDO", Name = "Laredo yard", State = "TX", NearbyCities = ["Laredo"] }],
            Corridors =
            [
                // References GHOST which isn't defined.
                new() { Code = "LAREDO_TO_GHOST", OriginWarehouseCode = "LAREDO", DestinationWarehouseCode = "GHOST" },
            ],
        };

        var controller = BuildController(client, opts);
        var result = await controller.GetCorridorHealth(default);
        var healths = Assert.IsAssignableFrom<IReadOnlyList<CorridorHealth>>(
            ((OkObjectResult)result.Result!).Value);

        Assert.Empty(healths);
    }

    [Fact]
    public async Task GetCorridorHealth_returns_null_count_when_Alvys_read_degrades()
    {
        // FakeAlvysClient with a load-throwing override simulates an Alvys blip.
        var client = new ThrowingSearchAlvysClient();
        var controller = BuildController(client, PilotOptions());

        var result = await controller.GetCorridorHealth(default);
        var healths = Assert.IsAssignableFrom<IReadOnlyList<CorridorHealth>>(
            ((OkObjectResult)result.Result!).Value);

        var only = Assert.Single(healths);
        Assert.Null(only.OpenLoadCount); // caller shows "unknown" honestly, not a false zero
    }

    // ---------- fixtures ----------

    private static ConsolidationOptions PilotOptions() => new()
    {
        Warehouses =
        [
            new() { Code = "LAREDO", Name = "Laredo yard", State = "TX", NearbyCities = ["Laredo"] },
            new() { Code = "DALLAS", Name = "Dallas 154-door yard", State = "TX", NearbyCities = ["Dallas"] },
        ],
        Corridors =
        [
            new() { Code = "LAREDO_TO_DALLAS", OriginWarehouseCode = "LAREDO", DestinationWarehouseCode = "DALLAS", PickupWindowDays = 2, DeliveryWindowDays = 3 },
        ],
    };

    private static AlvysLoad BuildLoad(string loadNumber, string oCity, string oState, string dCity, string dState) => new()
    {
        Id = loadNumber,
        LoadNumber = loadNumber,
        Status = "Available",
        // Minimum required for the LTL normalization to yield an origin/destination that
        // matches the search filter. AlvysLoadStop carries city/state on the Address sub-object.
        Stops =
        [
            new AlvysLoadStop
            {
                StopType = "Pickup",
                Sequence = 1,
                Address = new AlvysAddress { City = oCity, State = oState },
                ScheduledStart = DateTimeOffset.UtcNow.AddDays(1),
            },
            new AlvysLoadStop
            {
                StopType = "Delivery",
                Sequence = 2,
                Address = new AlvysAddress { City = dCity, State = dState },
                ScheduledStart = DateTimeOffset.UtcNow.AddDays(2),
            },
        ],
    };

    private static ConsolidationController BuildController(IAlvysClient client, ConsolidationOptions opts)
    {
        var ltlOptions = LtlTestFactory.Options();
        var normalizer = LtlTestFactory.Normalizer();
        var loads = new LtlLoadService(client, normalizer, LtlTestFactory.Visibility(), LtlTestFactory.AccessorialAnalyzer(), new NullAccessorialSignalExtractor(), ltlOptions);
        return new ConsolidationController(
            candidates: null!,
            plans: null!,
            audits: null!,
            options: Microsoft.Extensions.Options.Options.Create(opts),
            loads: loads);
    }

    /// <summary>Fake that throws on the paged search overload; used to exercise the degrade branch.</summary>
    private sealed class ThrowingSearchAlvysClient : FakeAlvysClient
    {
        public override Task<AlvysLoadsResponse> SearchLoadsAsync(LoadSearchRequest request, CancellationToken ct = default)
            => throw new HttpRequestException("simulated Alvys blip");
    }
}
