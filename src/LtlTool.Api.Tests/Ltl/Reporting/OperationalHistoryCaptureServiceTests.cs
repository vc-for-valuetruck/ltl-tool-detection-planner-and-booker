using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl.Reporting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Reporting;

/// <summary>
/// Verifies <see cref="OperationalHistoryCaptureService"/> extracts accessorial/assignment data
/// correctly from an already-fetched <see cref="AlvysLoad"/>/<see cref="AlvysTrip"/> list, that an
/// empty trip list only skips the trip-derived captures (customer accessorials still capture), that
/// every matching trip is captured (not just one), that a trip with nothing assigned records no
/// assignment row, and that a capture-store failure never propagates to the caller (best-effort
/// side channel).
/// </summary>
public sealed class OperationalHistoryCaptureServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Capture_extracts_customer_accessorials_from_the_load()
    {
        var (service, accessorials, _) = Build();
        var load = LoadWithCustomerAccessorial();

        service.Capture(load, []);

        var line = Assert.Single(accessorials.Captured);
        Assert.Equal(AccessorialEntityType.Customer, line.Line.EntityType);
        Assert.Equal("Detention", line.Line.Type);
        Assert.Equal(150m, line.Line.Amount);
        Assert.Null(line.TripId);
    }

    [Fact]
    public void Capture_extracts_carrier_and_driver_accessorials_from_the_trip()
    {
        var (service, accessorials, _) = Build();
        var load = new AlvysLoad { Id = "load-1", LoadNumber = "L-1001" };
        var trip = new AlvysTrip
        {
            Id = "trip-1",
            Carrier = new AlvysPartyPay
            {
                Id = "C1",
                AccessorialsDetails = [new AlvysAccessorialDetail { Type = "Lumper", Amount = 75m }],
            },
            Driver1 = new AlvysPartyPay
            {
                Id = "D1",
                AccessorialsDetails = [new AlvysAccessorialDetail { Type = "Layover", Amount = 100m }],
            },
        };

        service.Capture(load, [trip]);

        Assert.Contains(accessorials.Captured, c =>
            c.Line.EntityType == AccessorialEntityType.Carrier && c.Line.Type == "Lumper" && c.TripId == "trip-1");
        Assert.Contains(accessorials.Captured, c =>
            c.Line.EntityType == AccessorialEntityType.Driver1 && c.Line.Type == "Layover" && c.TripId == "trip-1");
    }

    [Fact]
    public void Capture_with_no_trips_only_captures_customer_accessorials()
    {
        var (service, accessorials, assignments) = Build();
        var load = LoadWithCustomerAccessorial();

        service.Capture(load, []);

        Assert.Single(accessorials.Captured);
        Assert.Empty(assignments.Captured);
    }

    [Fact]
    public void Capture_records_an_assignment_snapshot_from_the_trip()
    {
        var (service, _, assignments) = Build();
        var load = new AlvysLoad { Id = "load-1", LoadNumber = "L-1001" };
        var trip = new AlvysTrip
        {
            Id = "trip-1",
            Status = "Dispatched",
            Carrier = new AlvysPartyPay { Id = "C1", Name = "Acme Carrier" },
            Driver1 = new AlvysPartyPay { Id = "D1", Name = "Driver One" },
            Truck = new AlvysEquipmentRef { Id = "TRK1" },
            Trailer = new AlvysTrailer { Id = "TRL1" },
            DispatcherId = "US1",
        };

        service.Capture(load, [trip]);

        var snapshot = Assert.Single(assignments.Captured);
        Assert.Equal("load-1", snapshot.LoadId);
        Assert.Equal("C1", snapshot.CarrierId);
        Assert.Equal("D1", snapshot.Driver1Id);
        Assert.Equal("TRK1", snapshot.TruckId);
        Assert.Equal("TRL1", snapshot.TrailerId);
    }

    [Fact]
    public void Capture_records_every_matching_trip_not_just_one()
    {
        // A load with two trips (e.g. a re-dispatch) — neither is "the" economics-bearing trip from
        // the caller's perspective; both must still contribute their own assignment/accessorial data.
        var (service, accessorials, assignments) = Build();
        var load = new AlvysLoad { Id = "load-1", LoadNumber = "L-1001" };
        var tripA = new AlvysTrip
        {
            Id = "trip-A",
            Carrier = new AlvysPartyPay
            {
                Id = "C1",
                AccessorialsDetails = [new AlvysAccessorialDetail { Type = "Lumper", Amount = 50m }],
            },
        };
        var tripB = new AlvysTrip
        {
            Id = "trip-B",
            Carrier = new AlvysPartyPay { Id = "C2" },
            Truck = new AlvysEquipmentRef { Id = "TRK2" },
        };

        service.Capture(load, [tripA, tripB]);

        Assert.Contains(accessorials.Captured, c => c.TripId == "trip-A" && c.Line.Type == "Lumper");
        Assert.Equal(2, assignments.Captured.Count);
        Assert.Contains(assignments.Captured, a => a.TripId == "trip-A" && a.CarrierId == "C1");
        Assert.Contains(assignments.Captured, a => a.TripId == "trip-B" && a.CarrierId == "C2");
    }

    [Fact]
    public void Capture_skips_the_assignment_row_when_the_trip_has_nothing_assigned()
    {
        var (service, _, assignments) = Build();
        var load = new AlvysLoad { Id = "load-1", LoadNumber = "L-1001" };
        var trip = new AlvysTrip { Id = "trip-1", Status = "Pending" };

        service.Capture(load, [trip]);

        Assert.Empty(assignments.Captured);
    }

    [Fact]
    public void Capture_swallows_a_store_failure_and_never_throws()
    {
        var accessorials = new ThrowingAccessorialStore();
        var assignments = new FakeLoadAssignmentStore();
        var service = new OperationalHistoryCaptureService(
            accessorials, assignments, new FixedClock(Now), NullLogger<OperationalHistoryCaptureService>.Instance);
        var load = LoadWithCustomerAccessorial();

        var ex = Record.Exception(() => service.Capture(load, []));

        Assert.Null(ex);
    }

    private static AlvysLoad LoadWithCustomerAccessorial() => new()
    {
        Id = "load-1",
        LoadNumber = "L-1001",
        CustomerAccessorialsDetails = [new AlvysAccessorialDetail { Type = "Detention", Amount = 150m }],
    };

    private static (OperationalHistoryCaptureService Service, FakeAccessorialStore Accessorials, FakeLoadAssignmentStore Assignments)
        Build()
    {
        var accessorials = new FakeAccessorialStore();
        var assignments = new FakeLoadAssignmentStore();
        var service = new OperationalHistoryCaptureService(
            accessorials, assignments, new FixedClock(Now), NullLogger<OperationalHistoryCaptureService>.Instance);
        return (service, accessorials, assignments);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeAccessorialStore : IAccessorialStore
    {
        public List<(string LoadId, string? LoadNumber, string? TripId, ObservedAccessorialLine Line)> Captured { get; } = [];

        public void Capture(
            string loadId, string? loadNumber, string? tripId, ObservedAccessorialLine line, DateTimeOffset now) =>
            Captured.Add((loadId, loadNumber, tripId, line));

        public IReadOnlyList<AccessorialRecord> List(string? loadId, AccessorialEntityType? entityType, int max) => [];
    }

    private sealed class ThrowingAccessorialStore : IAccessorialStore
    {
        public void Capture(
            string loadId, string? loadNumber, string? tripId, ObservedAccessorialLine line, DateTimeOffset now) =>
            throw new InvalidOperationException("simulated store failure");

        public IReadOnlyList<AccessorialRecord> List(string? loadId, AccessorialEntityType? entityType, int max) => [];
    }

    private sealed class FakeLoadAssignmentStore : ILoadAssignmentStore
    {
        public List<ObservedAssignment> Captured { get; } = [];

        public void CaptureIfChanged(ObservedAssignment snapshot, DateTimeOffset now) => Captured.Add(snapshot);

        public IReadOnlyList<LoadAssignmentRecord> ListForLoad(string loadId, int max) => [];
        public IReadOnlyList<LoadAssignmentRecord> ListRecent(int max) => [];
    }
}
