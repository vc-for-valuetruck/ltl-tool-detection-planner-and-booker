using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the Phase 7.4 "Capacity today" snapshot: active-truck classification, the honest total
/// denominator, trailer pool breakdown by equipment type, and the in-transit trip count — all from
/// read-only Alvys sweeps, nothing fabricated.
/// </summary>
public sealed class CapacitySnapshotServiceTests
{
    private static CapacitySnapshotService Build(FakeAlvysClient client, LtlOptions? options = null) =>
        new(client, LtlTestFactory.Options(options), LtlTestFactory.Clock());

    [Fact]
    public async Task Snapshot_counts_active_trucks_and_keeps_an_honest_total()
    {
        var client = new FakeAlvysClient
        {
            Trucks =
            [
                new AlvysTruck { Id = "T1", Status = "Active" },
                new AlvysTruck { Id = "T2", Status = "Available" },
                new AlvysTruck { Id = "T3", Status = "Out of Service" },
                new AlvysTruck { Id = "T4", Status = null }, // unknown status → not counted active
            ],
        };

        var snapshot = await Build(client).GetSnapshotAsync(default);

        Assert.Equal(2, snapshot.ActiveTrucks);
        Assert.Equal(4, snapshot.TotalTrucks);
        Assert.False(snapshot.Truncated);
    }

    [Fact]
    public async Task Snapshot_breaks_trailer_pool_down_by_equipment_type_most_common_first()
    {
        var client = new FakeAlvysClient
        {
            Trailers =
            [
                new AlvysTrailerEquipment { Id = "R1", EquipmentType = "Dry Van" },
                new AlvysTrailerEquipment { Id = "R2", EquipmentType = "Dry Van" },
                new AlvysTrailerEquipment { Id = "R3", EquipmentType = "Reefer" },
                new AlvysTrailerEquipment { Id = "R4", EquipmentType = null }, // → "Unspecified"
            ],
        };

        var snapshot = await Build(client).GetSnapshotAsync(default);

        Assert.Equal(4, snapshot.TotalTrailers);
        Assert.Equal(3, snapshot.TrailersByType.Count);
        Assert.Equal("Dry Van", snapshot.TrailersByType[0].EquipmentType);
        Assert.Equal(2, snapshot.TrailersByType[0].Count);
        Assert.Contains(snapshot.TrailersByType, t => t.EquipmentType == "Unspecified" && t.Count == 1);
    }

    [Fact]
    public async Task Snapshot_counts_in_transit_trips()
    {
        var client = new FakeAlvysClient
        {
            Trips =
            [
                new AlvysTrip { Id = "P1", Status = "In Transit" },
                new AlvysTrip { Id = "P2", Status = "En-Route" },
            ],
        };

        var snapshot = await Build(client).GetSnapshotAsync(default);

        Assert.Equal(2, snapshot.InTransitTrips);
    }

    [Fact]
    public async Task Snapshot_is_all_zero_and_untruncated_for_an_empty_fleet()
    {
        var snapshot = await Build(new FakeAlvysClient()).GetSnapshotAsync(default);

        Assert.Equal(0, snapshot.ActiveTrucks);
        Assert.Equal(0, snapshot.TotalTrucks);
        Assert.Equal(0, snapshot.TotalTrailers);
        Assert.Empty(snapshot.TrailersByType);
        Assert.Equal(0, snapshot.InTransitTrips);
        Assert.False(snapshot.Truncated);
    }
}
