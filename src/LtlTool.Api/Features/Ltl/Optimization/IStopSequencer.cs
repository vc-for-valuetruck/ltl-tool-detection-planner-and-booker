namespace LtlTool.Api.Features.Ltl.Optimization;

/// <summary>
/// Phase 2 stop-sequencing engine: order a consolidation plan's parent + sibling stops into a
/// sensible route before the click-card text is emitted (today waypoints are listed in input
/// order). Selected at startup by <c>Ltl:Optimization:AgentCommands:Enabled</c> — when disabled
/// the <see cref="NullStopSequencer"/> is registered and the input order is preserved.
///
/// <para>Pure compute: all stop data is Alvys-derived and supplied by the API.</para>
/// </summary>
public interface IStopSequencer
{
    /// <summary>True when a real sequencer is wired; false for the null implementation.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Return the stops in the order they should be driven. The null implementation returns the
    /// input order unchanged so behavior is unsurprising until a real sequencer is wired.
    /// </summary>
    Task<StopSequenceResult> SequenceAsync(StopSequenceRequest request, CancellationToken ct = default);
}

/// <summary>One stop to be sequenced. Coordinates are optional Alvys-derived values, never invented.</summary>
public sealed record StopToSequence(string StopRef, string? City, string? State, double? Latitude, double? Longitude);

/// <summary>Inputs to a stop-sequencing request.</summary>
public sealed record StopSequenceRequest(IReadOnlyList<StopToSequence> Stops);

/// <summary>
/// Result of stop sequencing: the ordered stop refs plus whether a real optimization reordered
/// them (<see cref="Optimized"/> false means the input order was preserved as-is).
/// </summary>
public sealed record StopSequenceResult(
    IReadOnlyList<string> OrderedStopRefs,
    bool Optimized,
    string Rationale,
    DateTimeOffset SequencedAt);

/// <summary>
/// No-op <see cref="IStopSequencer"/> registered when <c>Ltl:Optimization:AgentCommands:Enabled = false</c>
/// (the default). Preserves the input order and reports <see cref="StopSequenceResult.Optimized"/> = false.
/// </summary>
public sealed class NullStopSequencer(TimeProvider timeProvider) : IStopSequencer
{
    public bool IsEnabled => false;

    public Task<StopSequenceResult> SequenceAsync(StopSequenceRequest request, CancellationToken ct = default)
        => Task.FromResult(new StopSequenceResult(
            request.Stops.Select(s => s.StopRef).ToList(),
            Optimized: false,
            "Stop sequencer is not enabled — input order preserved.",
            timeProvider.GetUtcNow()));
}
