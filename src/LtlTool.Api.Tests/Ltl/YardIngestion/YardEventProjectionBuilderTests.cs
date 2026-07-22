using LtlTool.Api.Features.Ltl.YardIngestion;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.YardIngestion;

/// <summary>
/// Unit tests for the pure event→projection fold. Covers the boundary contract's hard requirements:
/// administrative-only records never project, out-of-order delivery is order-independent (fold sorts
/// by occurrence then sequence), last-writer-wins overlays never clear known values, hold/release/
/// cancel derive from the latest hold-family event, split/consolidation relationships are captured,
/// and the provisional→ready transition needs dock completion + security clearance with no active
/// hold/cancel.
/// </summary>
public sealed class YardEventProjectionBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 8, 0, 0, TimeSpan.Zero);

    private static YardEventRecord Event(
        YardEventCategory category,
        string? payloadJson = null,
        int minutesAfterT0 = 0,
        long sequence = 0,
        string sourceRecordId = "R1",
        string yard = "YARD-A") => new()
    {
        DedupeKey = $"{category}:{sequence}:{Guid.NewGuid():n}",
        EventId = Guid.NewGuid().ToString(),
        SchemaVersion = 1,
        EventType = category.ToString(),
        Category = category.ToString(),
        AffectsSchedulerInput = YardEventClassifier.AffectsSchedulerInput(category),
        OccurredAt = T0.AddMinutes(minutesAfterT0),
        ReceivedAt = T0.AddMinutes(minutesAfterT0),
        SourceSystem = "yard-control",
        SourceRecordType = "appointment",
        SourceRecordId = sourceRecordId,
        YardLocationId = yard,
        PayloadJson = payloadJson ?? "{}",
        Sequence = sequence,
    };

    [Fact]
    public void Administrative_only_record_never_projects()
    {
        var built = YardEventProjectionBuilder.Build(
        [
            Event(YardEventCategory.Administrative, sequence: 1),
            Event(YardEventCategory.Unknown, sequence: 2),
        ]);

        Assert.Null(built);
    }

    [Fact]
    public void First_freight_event_makes_record_eligible_and_provisional()
    {
        var built = YardEventProjectionBuilder.Build([Event(YardEventCategory.Arrival, sequence: 1)]);

        Assert.NotNull(built);
        Assert.True(built!.SchedulerEligible);
        Assert.Equal(ScheduleReadiness.Provisional.ToString(), built.Readiness);
        Assert.Equal(ScheduleHoldState.None.ToString(), built.HoldState);
        Assert.Equal(0.0, built.Completeness);
        Assert.Equal(1, built.EventCount);
    }

    [Fact]
    public void Missing_freight_fields_stay_null_never_coerced_to_zero()
    {
        var built = YardEventProjectionBuilder.Build([Event(YardEventCategory.Arrival, sequence: 1)]);

        Assert.NotNull(built);
        Assert.Null(built!.WeightLbs);
        Assert.Null(built.PieceCount);
        Assert.Null(built.AppointmentAt);
        Assert.Null(built.TruckId);
    }

    [Fact]
    public void Out_of_order_delivery_produces_the_same_projection_as_in_order()
    {
        // Weight captured at T0+10 = 100, then a later occurrence at T0+20 = 200.
        var early = Event(YardEventCategory.FreightWeight, "{\"weightLbs\":100}", minutesAfterT0: 10, sequence: 1);
        var late = Event(YardEventCategory.FreightWeight, "{\"weightLbs\":200}", minutesAfterT0: 20, sequence: 2);

        var inOrder = YardEventProjectionBuilder.Build([early, late]);
        var reversed = YardEventProjectionBuilder.Build([late, early]);

        // The later occurrence wins regardless of the order the events were handed to the fold.
        Assert.Equal(200, inOrder!.WeightLbs);
        Assert.Equal(200, reversed!.WeightLbs);
        Assert.Equal(inOrder.WeightLbs, reversed.WeightLbs);
        Assert.Equal(late.EventId, inOrder.LatestEventId);
        Assert.Equal(late.EventId, reversed.LatestEventId);
    }

    [Fact]
    public void Overlay_does_not_clear_a_previously_known_value_when_a_later_event_omits_it()
    {
        var withTruck = Event(YardEventCategory.Arrival, "{\"truckId\":\"T-9\"}", minutesAfterT0: 0, sequence: 1);
        var later = Event(YardEventCategory.CheckIn, "{}", minutesAfterT0: 30, sequence: 2);

        var built = YardEventProjectionBuilder.Build([withTruck, later]);

        Assert.Equal("T-9", built!.TruckId);
    }

    [Fact]
    public void Hold_then_release_clears_hold_and_sets_security_cleared()
    {
        var built = YardEventProjectionBuilder.Build(
        [
            Event(YardEventCategory.Hold, minutesAfterT0: 10, sequence: 1),
            Event(YardEventCategory.Release, minutesAfterT0: 20, sequence: 2),
        ]);

        Assert.Equal(ScheduleHoldState.Released.ToString(), built!.HoldState);
        Assert.True(built.SecurityCleared);
    }

    [Fact]
    public void Release_then_hold_leaves_an_active_hold()
    {
        var built = YardEventProjectionBuilder.Build(
        [
            Event(YardEventCategory.Release, minutesAfterT0: 10, sequence: 1),
            Event(YardEventCategory.Hold, minutesAfterT0: 20, sequence: 2),
        ]);

        Assert.Equal(ScheduleHoldState.Held.ToString(), built!.HoldState);
        Assert.False(built.SecurityCleared);
    }

    [Fact]
    public void Cancellation_is_terminal_and_blocks_readiness()
    {
        var built = YardEventProjectionBuilder.Build(
        [
            Event(YardEventCategory.LoadComplete, minutesAfterT0: 10, sequence: 1),
            Event(YardEventCategory.Release, minutesAfterT0: 20, sequence: 2),
            Event(YardEventCategory.Cancellation, minutesAfterT0: 30, sequence: 3),
        ]);

        Assert.Equal(ScheduleHoldState.Cancelled.ToString(), built!.HoldState);
        Assert.Equal(ScheduleReadiness.Provisional.ToString(), built.Readiness);
    }

    [Fact]
    public void Split_relationship_is_captured_from_payload()
    {
        var built = YardEventProjectionBuilder.Build(
        [
            Event(
                YardEventCategory.Split,
                "{\"parentSourceRecordId\":\"P-1\",\"relatedRecordIds\":[\"C-1\",\"C-2\"]}",
                sequence: 1),
        ]);

        Assert.Equal(YardEventCategory.Split.ToString(), built!.RelationshipType);
        Assert.Equal("P-1", built.ParentSourceRecordId);
        Assert.Contains("C-1", built.RelatedRecordIdsJson);
        Assert.Contains("C-2", built.RelatedRecordIdsJson);
    }

    [Fact]
    public void Provisional_advances_to_ready_when_dock_complete_and_security_cleared()
    {
        var built = YardEventProjectionBuilder.Build(
        [
            Event(YardEventCategory.Arrival, minutesAfterT0: 0, sequence: 1),
            Event(YardEventCategory.LoadComplete, minutesAfterT0: 30, sequence: 2),
            Event(YardEventCategory.Release, minutesAfterT0: 40, sequence: 3),
        ]);

        Assert.True(built!.DockCompleted);
        Assert.True(built.SecurityCleared);
        Assert.Equal(1.0, built.Completeness);
        Assert.Equal(ScheduleReadiness.Ready.ToString(), built.Readiness);
    }

    [Fact]
    public void Dock_complete_without_security_clearance_stays_provisional_at_half_completeness()
    {
        var built = YardEventProjectionBuilder.Build(
        [
            Event(YardEventCategory.LoadComplete, minutesAfterT0: 10, sequence: 1),
        ]);

        Assert.True(built!.DockCompleted);
        Assert.False(built.SecurityCleared);
        Assert.Equal(0.5, built.Completeness);
        Assert.Equal(ScheduleReadiness.Provisional.ToString(), built.Readiness);
    }

    [Fact]
    public void Exception_event_flags_open_exception()
    {
        var built = YardEventProjectionBuilder.Build(
        [
            Event(YardEventCategory.Exception, sequence: 1),
        ]);

        Assert.True(built!.HasOpenException);
    }
}
