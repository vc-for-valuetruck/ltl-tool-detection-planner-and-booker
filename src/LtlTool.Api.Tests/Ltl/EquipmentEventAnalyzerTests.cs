using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the equipment-event interpreter: repair/maintenance events overlapping the load window
/// become conflicts, non-overlapping or non-conflict-type events are ignored, and absent/unfetched
/// data is reported not-evaluated (never asserted "available").
/// </summary>
public sealed class EquipmentEventAnalyzerTests
{
    private static readonly DateTimeOffset PickupAt = new(2026, 6, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DeliveryAt = new(2026, 6, 22, 0, 0, 0, TimeSpan.Zero);

    private static EquipmentEventAnalyzer Analyzer() => LtlTestFactory.EquipmentEvents();

    [Fact]
    public void Not_evaluated_when_caller_did_not_fetch()
    {
        var result = Analyzer().Assess(PickupAt, DeliveryAt, [], [], evaluated: false);

        Assert.Same(EquipmentEventAssessment.NotEvaluated, result);
        Assert.False(result.Evaluated);
        Assert.False(result.HasConflict);
    }

    [Fact]
    public void Not_evaluated_when_there_is_no_window()
    {
        var result = Analyzer().Assess(null, null, [], [], evaluated: true);

        Assert.False(result.Evaluated);
    }

    [Fact]
    public void Repair_overlapping_the_window_is_a_conflict()
    {
        var truckEvents = new List<AlvysTruckEvent>
        {
            new() { TruckId = "T1", EventType = "Repair", Title = "Brake job",
                StartDate = PickupAt.AddDays(-1), EndDate = PickupAt.AddDays(1) },
        };

        var result = Analyzer().Assess(PickupAt, DeliveryAt, truckEvents, [], evaluated: true);

        Assert.True(result.Evaluated);
        Assert.True(result.HasConflict);
        Assert.Contains(result.Conflicts, c => c.Contains("Brake job"));
    }

    [Fact]
    public void Event_outside_the_window_is_not_a_conflict()
    {
        var trailerEvents = new List<AlvysTrailerEvent>
        {
            new() { TrailerId = "TR1", EventType = "Maintenance",
                StartDate = DeliveryAt.AddDays(5), EndDate = DeliveryAt.AddDays(6) },
        };

        var result = Analyzer().Assess(PickupAt, DeliveryAt, [], trailerEvents, evaluated: true);

        Assert.True(result.Evaluated);
        Assert.False(result.HasConflict);
    }

    [Fact]
    public void Non_conflict_event_type_is_ignored()
    {
        var truckEvents = new List<AlvysTruckEvent>
        {
            new() { TruckId = "T1", EventType = "Inspection",
                StartDate = PickupAt, EndDate = DeliveryAt },
        };

        var result = Analyzer().Assess(PickupAt, DeliveryAt, truckEvents, [], evaluated: true);

        Assert.True(result.Evaluated);
        Assert.False(result.HasConflict);
    }
}
