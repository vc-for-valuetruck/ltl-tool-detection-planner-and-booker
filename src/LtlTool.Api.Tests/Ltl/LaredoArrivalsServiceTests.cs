using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.Arrivals;
using LtlTool.Api.Features.Ltl.Consolidation;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the Phase 8.1 Laredo Arrivals Board: trips with a Laredo-area stop on the target day
/// surface as arrivals, Dallas-bound freight sorts first, ownership is honest (Fleet vs Unknown,
/// never guessed), status derives from the Laredo stop, and a missing corridor yields an honest
/// empty board — all from read-only Alvys reads.
/// </summary>
public sealed class LaredoArrivalsServiceTests
{
    // The fixed clock (2026-06-30) is the default board day when the request carries no date.
    private static readonly DateOnly Day = DateOnly.FromDateTime(LtlTestFactory.Now.UtcDateTime);

    private static LaredoArrivalsService Build(
        FakeAlvysClient client, ConsolidationOptions? consolidation = null, LtlOptions? ltl = null) =>
        new(
            client,
            LtlTestFactory.Options(ltl),
            Microsoft.Extensions.Options.Options.Create(consolidation ?? new ConsolidationOptions()),
            LtlTestFactory.Clock());

    private static AlvysTripStop Stop(
        int sequence,
        string stopType,
        string city,
        string state,
        DateTimeOffset? windowStart = null,
        DateTimeOffset? arrived = null,
        DateTimeOffset? departed = null,
        string? status = null) => new()
    {
        Sequence = sequence,
        StopType = stopType,
        Status = status,
        Address = new AlvysAddress { City = city, State = state },
        StopWindowStart = windowStart,
        ArrivedDate = arrived,
        DepartedDate = departed,
    };

    private static DateTimeOffset OnDay(int hour) =>
        new(Day.Year, Day.Month, Day.Day, hour, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Board_surfaces_laredo_arrivals_with_dallas_bound_first()
    {
        var client = new FakeAlvysClient
        {
            Trips =
            [
                // Non-Dallas-bound: Laredo → Houston.
                new AlvysTrip
                {
                    Id = "P-HOU",
                    TripNumber = "T-HOU",
                    Stops =
                    [
                        Stop(1, "Pickup", "Laredo", "TX", windowStart: OnDay(8)),
                        Stop(2, "Delivery", "Houston", "TX", windowStart: OnDay(20)),
                    ],
                },
                // Dallas-bound: Laredo → Dallas.
                new AlvysTrip
                {
                    Id = "P-DAL",
                    TripNumber = "T-DAL",
                    Stops =
                    [
                        Stop(1, "Pickup", "Laredo", "TX", windowStart: OnDay(9)),
                        Stop(2, "Delivery", "Dallas", "TX", windowStart: OnDay(21)),
                    ],
                },
            ],
        };

        var board = await Build(client).GetBoardAsync(null, default);

        Assert.Equal("LAREDO", board.Yard);
        Assert.Equal(2, board.Arrivals.Count);
        Assert.True(board.Arrivals[0].DallasBound);
        Assert.Equal("P-DAL", board.Arrivals[0].TripId);
        Assert.False(board.Arrivals[1].DallasBound);
        Assert.Contains(board.Arrivals[0].OnwardStops, p => p.City == "Dallas");
        Assert.False(board.Truncated);
    }

    [Fact]
    public async Task Ownership_is_fleet_when_master_data_resolves_a_fleet_otherwise_unknown()
    {
        var client = new FakeAlvysClient
        {
            Trips =
            [
                new AlvysTrip
                {
                    Id = "P1",
                    TripNumber = "T1",
                    Truck = new AlvysEquipmentRef { Id = "TR-1" },
                    Trailer = new AlvysTrailer { Id = "RL-1", EquipmentType = "Dry Van" },
                    Stops =
                    [
                        Stop(1, "Pickup", "Laredo", "TX", windowStart: OnDay(8)),
                        Stop(2, "Delivery", "Dallas", "TX", windowStart: OnDay(20)),
                    ],
                },
            ],
            Trucks =
            [
                new AlvysTruck
                {
                    Id = "TR-1",
                    TruckNum = "101",
                    Fleet = new AlvysFleet { Id = "F1", Name = "Value Truck Fleet" },
                },
            ],
            // No trailer master data → trailer ownership stays honestly Unknown.
        };

        var board = await Build(client).GetBoardAsync(null, default);

        var arrival = Assert.Single(board.Arrivals);
        Assert.Equal(ArrivalOwnership.Fleet, arrival.Truck!.Ownership);
        Assert.Equal("101", arrival.Truck.Unit);
        Assert.Equal("Value Truck Fleet", arrival.Truck.FleetName);

        Assert.Equal(ArrivalOwnership.Unknown, arrival.Trailer!.Ownership);
        Assert.Null(arrival.Trailer.Unit);
        // The inline trip trailer type is preserved even when master data is missing.
        Assert.Equal("Dry Van", arrival.Trailer.EquipmentType);
    }

    [Fact]
    public async Task Status_derives_from_the_laredo_stop_movement_timestamps()
    {
        var client = new FakeAlvysClient
        {
            Trips =
            [
                new AlvysTrip
                {
                    Id = "P-DEP",
                    Stops = [Stop(1, "Pickup", "Laredo", "TX", windowStart: OnDay(6), arrived: OnDay(7), departed: OnDay(8))],
                },
                new AlvysTrip
                {
                    Id = "P-ARR",
                    Stops = [Stop(1, "Pickup", "Laredo", "TX", windowStart: OnDay(6), arrived: OnDay(7))],
                },
                new AlvysTrip
                {
                    Id = "P-SCH",
                    Stops = [Stop(1, "Pickup", "Laredo", "TX", windowStart: OnDay(6))],
                },
            ],
        };

        var board = await Build(client).GetBoardAsync(null, default);
        var byId = board.Arrivals.ToDictionary(a => a.TripId);

        Assert.Equal(ArrivalStatus.Departed, byId["P-DEP"].Status);
        Assert.Equal(ArrivalStatus.Arrived, byId["P-ARR"].Status);
        Assert.Equal(ArrivalStatus.Scheduled, byId["P-SCH"].Status);
    }

    [Fact]
    public async Task Board_is_empty_when_the_pilot_corridor_is_not_configured()
    {
        var client = new FakeAlvysClient
        {
            Trips =
            [
                new AlvysTrip
                {
                    Id = "P1",
                    Stops = [Stop(1, "Pickup", "Laredo", "TX", windowStart: OnDay(8))],
                },
            ],
        };

        var board = await Build(client, new ConsolidationOptions { Corridors = [] })
            .GetBoardAsync(null, default);

        Assert.Empty(board.Arrivals);
    }

    [Fact]
    public async Task Trips_without_a_laredo_stop_do_not_appear()
    {
        var client = new FakeAlvysClient
        {
            Trips =
            [
                new AlvysTrip
                {
                    Id = "P-NONE",
                    Stops =
                    [
                        Stop(1, "Pickup", "El Paso", "TX", windowStart: OnDay(8)),
                        Stop(2, "Delivery", "Dallas", "TX", windowStart: OnDay(20)),
                    ],
                },
            ],
        };

        var board = await Build(client).GetBoardAsync(null, default);

        Assert.Empty(board.Arrivals);
    }
}
