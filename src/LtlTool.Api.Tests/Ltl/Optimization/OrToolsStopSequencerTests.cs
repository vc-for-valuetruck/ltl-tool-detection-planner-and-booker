using LtlTool.Api.Features.Ltl.Optimization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Optimization;

/// <summary>
/// Tests for the OR-Tools stop sequencer. The central honesty rule: without stop coordinates every
/// leg is an identical estimate, so the sequencer must preserve input order and report
/// <see cref="StopSequenceResult.Optimized"/> = false rather than fabricate a route. When
/// coordinates are present it produces a shortest-visiting path.
/// </summary>
[Trait("Category", "Optimization")]
public sealed class OrToolsStopSequencerTests
{
    private static OrToolsStopSequencer BuildSequencer()
    {
        var distances = new AlvysDistanceMatrixProvider(Microsoft.Extensions.Options.Options.Create(new DistanceMatrixOptions()));
        return new OrToolsStopSequencer(
            distances,
            Microsoft.Extensions.Options.Options.Create(new StopSequencerOptions()),
            TimeProvider.System,
            NullLogger<OrToolsStopSequencer>.Instance);
    }

    [Fact]
    public async Task Fewer_than_three_stops_preserves_input_order_unoptimized()
    {
        var sequencer = BuildSequencer();
        var request = new StopSequenceRequest(
        [
            new StopToSequence("A", "Laredo", "TX", 27.5, -99.5),
            new StopToSequence("B", "Dallas", "TX", 32.7, -96.8),
        ]);

        var result = await sequencer.SequenceAsync(request);

        Assert.False(result.Optimized);
        Assert.Equal(["A", "B"], result.OrderedStopRefs);
    }

    [Fact]
    public async Task No_coordinates_preserves_input_order_unoptimized()
    {
        var sequencer = BuildSequencer();
        var request = new StopSequenceRequest(
        [
            new StopToSequence("A", "Laredo", "TX", null, null),
            new StopToSequence("B", "San Antonio", "TX", null, null),
            new StopToSequence("C", "Dallas", "TX", null, null),
        ]);

        var result = await sequencer.SequenceAsync(request);

        Assert.False(result.Optimized);
        Assert.Equal(["A", "B", "C"], result.OrderedStopRefs);
    }

    [Fact]
    public async Task Coordinates_present_produces_shortest_path_from_first_stop()
    {
        var sequencer = BuildSequencer();
        // Anchored at Laredo. A naive input order visits Dallas before San Antonio (a backtrack);
        // the optimizer should visit San Antonio (closer) before Dallas.
        var request = new StopSequenceRequest(
        [
            new StopToSequence("LAREDO", "Laredo", "TX", 27.5306, -99.4803),
            new StopToSequence("DALLAS", "Dallas", "TX", 32.7767, -96.7970),
            new StopToSequence("SAN_ANTONIO", "San Antonio", "TX", 29.4241, -98.4936),
        ]);

        var result = await sequencer.SequenceAsync(request);

        Assert.True(result.Optimized);
        Assert.Equal("LAREDO", result.OrderedStopRefs[0]);
        Assert.Equal(["LAREDO", "SAN_ANTONIO", "DALLAS"], result.OrderedStopRefs);
    }

    [Fact]
    public async Task Result_is_a_permutation_of_the_input()
    {
        var sequencer = BuildSequencer();
        var request = new StopSequenceRequest(
        [
            new StopToSequence("LAREDO", "Laredo", "TX", 27.5306, -99.4803),
            new StopToSequence("HOUSTON", "Houston", "TX", 29.7604, -95.3698),
            new StopToSequence("AUSTIN", "Austin", "TX", 30.2672, -97.7431),
            new StopToSequence("DALLAS", "Dallas", "TX", 32.7767, -96.7970),
        ]);

        var result = await sequencer.SequenceAsync(request);

        Assert.Equal(
            new[] { "LAREDO", "HOUSTON", "AUSTIN", "DALLAS" }.OrderBy(x => x),
            result.OrderedStopRefs.OrderBy(x => x));
    }
}
