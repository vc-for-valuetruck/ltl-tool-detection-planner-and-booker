using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the match orchestration layer: the bounded active-trip window-feasibility sweep is
/// grounded only in Alvys trip/stop data, the per-load equipment-event batch is memoized, and the
/// prediction provider result is honestly labeled with a deterministic fallback ranking.
/// </summary>
public sealed class MatchServiceTests
{
    private static readonly DateTimeOffset Pickup = new(2026, 7, 1, 8, 0, 0, TimeSpan.Zero);

    private static MatchService Build(
        FakeAlvysClient alvys, IAlvysDriverPredictionProvider? prediction = null, LtlOptions? options = null) =>
        LtlTestFactory.Matcher(alvys, prediction, options);

    private static LtlLoadSummary Load() => new()
    {
        Id = "L1",
        Status = "Open",
        Equipment = ["Dry Van"],
        WeightLbs = 8000m,
        Origin = new LtlPlace { City = "Dallas", State = "TX" },
        ScheduledPickupAt = Pickup,
        ScheduledDeliveryAt = Pickup.AddDays(1),
    };

    /// <summary>A load with no pickup instant — window feasibility must stay not-evaluated.</summary>
    private static LtlLoadSummary LoadWithoutPickup() => new()
    {
        Id = "L1",
        Status = "Open",
        Equipment = ["Dry Van"],
        WeightLbs = 8000m,
        Origin = new LtlPlace { City = "Dallas", State = "TX" },
        ScheduledPickupAt = null,
        ScheduledDeliveryAt = Pickup.AddDays(1),
    };

    private static MatchCandidate TruckCandidate(string truckId) =>
        new() { Truck = new AlvysTruck { Id = truckId, TruckNum = truckId } };

    [Fact]
    public async Task Window_feasibility_flags_a_truck_whose_committed_trip_clears_after_pickup()
    {
        var alvys = new FakeAlvysClient
        {
            Trips =
            [
                new AlvysTrip { Id = "TRIP-1", Status = "In Transit", Truck = new AlvysEquipmentRef { Id = "T1" } },
            ],
            TripStops = new()
            {
                ["TRIP-1"] =
                [
                    new AlvysTripStopDetail { StopWindow = new AlvysStopWindow { End = Pickup.AddHours(6) } },
                ],
            },
        };
        var service = Build(alvys);
        var candidates = new[] { TruckCandidate("T1") };

        var ctx = await service.FetchWindowFeasibilityAsync(Load(), candidates, default);
        var assessment = service.AssessWindow(candidates[0], ctx);

        Assert.True(assessment.Evaluated);
        Assert.True(assessment.Infeasible);
        Assert.Equal("TRIP-1", assessment.CommittedTripId);
    }

    [Fact]
    public async Task Window_feasibility_is_free_when_a_committed_trip_clears_before_pickup()
    {
        var alvys = new FakeAlvysClient
        {
            Trips = [new AlvysTrip { Id = "TRIP-1", Truck = new AlvysEquipmentRef { Id = "T1" } }],
            TripStops = new()
            {
                ["TRIP-1"] = [new AlvysTripStopDetail { StopWindow = new AlvysStopWindow { End = Pickup.AddHours(-6) } }],
            },
        };
        var service = Build(alvys);
        var candidates = new[] { TruckCandidate("T1") };

        var ctx = await service.FetchWindowFeasibilityAsync(Load(), candidates, default);
        var assessment = service.AssessWindow(candidates[0], ctx);

        Assert.True(assessment.Evaluated);
        Assert.False(assessment.Infeasible);
    }

    [Fact]
    public async Task Window_feasibility_not_evaluated_when_the_load_has_no_pickup_instant()
    {
        var service = Build(new FakeAlvysClient());

        var ctx = await service.FetchWindowFeasibilityAsync(
            LoadWithoutPickup(), [TruckCandidate("T1")], default);

        Assert.False(ctx.Evaluated);
        Assert.Same(TripCommitmentContext.NotEvaluated, ctx);
    }

    [Fact]
    public async Task Uncommitted_candidate_reads_as_free_on_a_complete_trip_search()
    {
        // Trips exist for other equipment but none carry T2; a complete (non-truncated) search lets
        // us treat the absence of a commitment as genuinely free rather than guessing.
        var alvys = new FakeAlvysClient
        {
            Trips =
            [
                new AlvysTrip { Id = "TRIP-1", Truck = new AlvysEquipmentRef { Id = "T1" } },
                new AlvysTrip { Id = "TRIP-2", Truck = new AlvysEquipmentRef { Id = "T9" } },
            ],
        };
        var service = Build(alvys);
        var candidates = new[] { TruckCandidate("T2") };

        var ctx = await service.FetchWindowFeasibilityAsync(Load(), candidates, default);
        var assessment = service.AssessWindow(candidates[0], ctx);

        Assert.True(assessment.Evaluated);
        Assert.Null(assessment.CommittedTripId);
        Assert.False(assessment.Infeasible);
        // No candidate-referenced trips → no stop-detail fetches were needed.
        Assert.Equal(0, alvys.ListTripStopsCallCount);
    }

    [Fact]
    public async Task Equipment_event_batch_is_memoized_across_repeated_fetches()
    {
        var alvys = new FakeAlvysClient();
        var service = Build(alvys);
        var candidates = new[]
        {
            new MatchCandidate
            {
                Truck = new AlvysTruck { Id = "T1" },
                Trailer = new AlvysTrailerEquipment { Id = "R1" },
            },
        };

        await service.FetchEquipmentEventsAsync(Load(), candidates, default);
        await service.FetchEquipmentEventsAsync(Load(), candidates, default);

        // Same window + same equipment set → the two Alvys event searches run only once.
        Assert.Equal(1, alvys.SearchTruckEventsCallCount);
        Assert.Equal(1, alvys.SearchTrailerEventsCallCount);
    }

    [Fact]
    public async Task Recommend_labels_every_result_with_the_prediction_fallback_basis()
    {
        var alvys = new FakeAlvysClient
        {
            Drivers = [new AlvysDriver { Id = "DR1", Name = "Sam", IsActive = true }],
        };
        var service = Build(alvys);

        var results = await service.RecommendAsync(Load(), top: 5, default);

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal("AlvysPredictionUnavailable", r.PredictionBasis));
    }

    [Fact]
    public async Task Recommend_orders_by_prediction_ranking_when_a_provider_is_available()
    {
        var alvys = new FakeAlvysClient
        {
            Drivers =
            [
                new AlvysDriver { Id = "DR1", Name = "First-by-factors", IsActive = true },
                new AlvysDriver { Id = "DR2", Name = "Prediction-favored", IsActive = true },
            ],
        };
        var prediction = new StubPrediction(["DR2", "DR1"], "AlvysBetaPrediction");
        var service = Build(alvys, prediction);

        var results = await service.RecommendAsync(Load(), top: 5, default);

        Assert.Equal("DR2", results[0].DriverId);
        Assert.All(results, r => Assert.Equal("AlvysBetaPrediction", r.PredictionBasis));
    }

    private sealed class StubPrediction(IReadOnlyList<string> ranked, string basis) : IAlvysDriverPredictionProvider
    {
        public Task<AlvysDriverPrediction> PredictAsync(
            LtlLoadSummary load, IReadOnlyList<MatchCandidate> candidates, CancellationToken ct)
            => Task.FromResult(new AlvysDriverPrediction
            {
                Available = true,
                RankedDriverIds = ranked,
                Basis = basis,
            });
    }
}
