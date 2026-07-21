using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl.DispatchPlanner;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.DispatchPlanner;

/// <summary>
/// Behavior tests for the read-only dispatch-planner utilisation layer: it must pick the most-recent
/// preference, degrade honestly to an unresolved view when Alvys returns nothing, cache politely so
/// repeated surface loads do not hammer Alvys, and enrich locations without ever fabricating one.
/// </summary>
public sealed class DispatchPlannerServiceTests
{
    private static DispatchPlannerService Build(FakeAlvysClient client) =>
        new(client, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DispatchPlannerService>.Instance);

    [Fact]
    public async Task GetPreferredPairing_returns_unresolved_when_no_ids_supplied()
    {
        var view = await Build(new FakeAlvysClient()).GetPreferredPairingAsync(null, "  ", null, default);

        Assert.False(view.Resolved);
        Assert.Null(view.Driver1Id);
    }

    [Fact]
    public async Task GetPreferredPairing_returns_unresolved_when_alvys_returns_nothing()
    {
        // Empty list models both "no preference on file" and a degraded/429 read (the client turns
        // any non-success into []). Either way the view is honestly unresolved, never fabricated.
        var view = await Build(new FakeAlvysClient()).GetPreferredPairingAsync(null, "TRK-1", null, default);

        Assert.False(view.Resolved);
        Assert.Null(view.TruckId);
    }

    [Fact]
    public async Task GetPreferredPairing_picks_the_most_recently_updated_preference()
    {
        var client = new FakeAlvysClient
        {
            DispatchPreferences =
            [
                new AlvysDispatchPreference
                {
                    UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    Driver1Id = "OLD", TruckId = "TRK-1", TrailerId = "TRL-9",
                },
                new AlvysDispatchPreference
                {
                    UpdatedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                    Driver1Id = "NEW", TruckId = "TRK-1", TrailerId = "TRL-7",
                },
            ],
        };

        var view = await Build(client).GetPreferredPairingAsync(null, "TRK-1", null, default);

        Assert.True(view.Resolved);
        Assert.Equal("NEW", view.Driver1Id);
        Assert.Equal("TRL-7", view.TrailerId);
    }

    [Fact]
    public async Task GetLocations_maps_resolved_ids_and_omits_unresolved()
    {
        var client = new FakeAlvysClient
        {
            Locations =
            [
                new AlvysLocation
                {
                    Id = "LOC-1", Name = "Laredo Cross-Dock", Type = "Warehouse",
                    PhysicalAddress = new AlvysContextAddress { City = "Laredo", State = "TX", ZipCode = "78045" },
                },
            ],
        };

        var map = await Build(client).GetLocationsAsync(["LOC-1", "LOC-MISSING"], default);

        Assert.True(map.ContainsKey("LOC-1"));
        Assert.False(map.ContainsKey("LOC-MISSING"));
        Assert.Equal("Laredo, TX 78045", map["LOC-1"].AddressLabel);
    }

    [Fact]
    public async Task GetLocations_short_circuits_on_empty_input()
    {
        var map = await Build(new FakeAlvysClient()).GetLocationsAsync([], default);
        Assert.Empty(map);
    }
}
