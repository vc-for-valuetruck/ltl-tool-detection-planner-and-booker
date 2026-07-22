using LtlTool.Api.Features.Ltl.YardIngestion;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.YardIngestion;

/// <summary>
/// Unit tests for the deterministic event-type classifier: canonical vocabulary, tolerated wire
/// variants (dotted / kebab / snake / suffix), fail-closed behavior for unknown types, and the
/// administrative/unknown exclusion from scheduler input.
/// </summary>
public sealed class YardEventClassifierTests
{
    [Theory]
    [InlineData("arrival", YardEventCategory.Arrival)]
    [InlineData("Truck_Arrived", YardEventCategory.Arrival)]
    [InlineData("truck-arrived", YardEventCategory.Arrival)]
    [InlineData("TRUCK.ARRIVED", YardEventCategory.Arrival)]
    [InlineData("loading.completed", YardEventCategory.LoadComplete)]
    [InlineData("unload.complete", YardEventCategory.UnloadComplete)]
    [InlineData("trailer.assigned", YardEventCategory.TrailerAssignment)]
    [InlineData("dock.assigned", YardEventCategory.DockAssignment)]
    [InlineData("freight.weight.captured", YardEventCategory.FreightWeight)]
    [InlineData("freight.dimensions", YardEventCategory.FreightDimensions)]
    [InlineData("appointment.updated", YardEventCategory.Appointment)]
    [InlineData("exception.raised", YardEventCategory.Exception)]
    [InlineData("security.hold", YardEventCategory.Hold)]
    [InlineData("load.released", YardEventCategory.Release)]
    [InlineData("cancelled", YardEventCategory.Cancellation)]
    [InlineData("load.split", YardEventCategory.Split)]
    [InlineData("load.consolidated", YardEventCategory.Consolidation)]
    public void Classifies_canonical_and_tolerated_variants(string eventType, YardEventCategory expected) =>
        Assert.Equal(expected, YardEventClassifier.Classify(eventType));

    [Theory]
    [InlineData("gate.log")]
    [InlineData("note.added")]
    [InlineData("visitor.scheduled")]
    [InlineData("report.generated")]
    [InlineData("user.login")]
    public void Known_administrative_types_are_administrative(string eventType)
    {
        var category = YardEventClassifier.Classify(eventType);
        Assert.Equal(YardEventCategory.Administrative, category);
        Assert.False(YardEventClassifier.AffectsSchedulerInput(category));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("some.type.we.do.not.model")]
    public void Unrecognized_types_fail_closed_to_unknown(string? eventType)
    {
        var category = YardEventClassifier.Classify(eventType);
        Assert.Equal(YardEventCategory.Unknown, category);
        Assert.False(YardEventClassifier.AffectsSchedulerInput(category));
    }

    [Fact]
    public void Freight_affecting_categories_are_scheduler_input()
    {
        Assert.True(YardEventClassifier.AffectsSchedulerInput(YardEventCategory.Arrival));
        Assert.True(YardEventClassifier.AffectsSchedulerInput(YardEventCategory.LoadComplete));
        Assert.True(YardEventClassifier.AffectsSchedulerInput(YardEventCategory.Hold));
        Assert.True(YardEventClassifier.AffectsSchedulerInput(YardEventCategory.Consolidation));
    }

    [Theory]
    [InlineData("  truck__arrived  ", "truck.arrived")]
    [InlineData("Load/Complete", "load.complete")]
    [InlineData("security:hold", "security.hold")]
    [InlineData("--release--", "release")]
    public void Normalize_collapses_separators_and_trims(string raw, string expected) =>
        Assert.Equal(expected, YardEventClassifier.Normalize(raw));
}
