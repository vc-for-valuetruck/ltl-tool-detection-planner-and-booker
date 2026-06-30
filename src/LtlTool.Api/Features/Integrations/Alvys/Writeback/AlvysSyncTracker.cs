namespace LtlTool.Api.Features.Integrations.Alvys.Writeback;

/// <summary>Outcome of the most recent Alvys read-sync probe.</summary>
public enum AlvysSyncOutcome
{
    /// <summary>No probe has run yet.</summary>
    Unknown,
    /// <summary>The last probe reached the Alvys read endpoint successfully.</summary>
    Success,
    /// <summary>The last probe failed (endpoint unavailable, auth/config, or transport).</summary>
    Failure,
}

/// <summary>The recorded result of the last Alvys read-sync probe.</summary>
public sealed record AlvysSyncSnapshot(
    AlvysSyncOutcome Outcome, DateTimeOffset? At, string? Detail);

/// <summary>
/// Records the result of the last Alvys read-sync readiness probe so the readiness status can
/// report endpoint availability and a "last successful read" time without holding any Alvys data.
/// In-memory and process-local; the probe that updates it is explicit and opt-in (never runs in
/// tests/CI by default).
/// </summary>
public interface IAlvysSyncTracker
{
    AlvysSyncSnapshot Current { get; }
    void Record(AlvysSyncOutcome outcome, DateTimeOffset at, string? detail = null);
}

/// <inheritdoc cref="IAlvysSyncTracker"/>
public sealed class InMemoryAlvysSyncTracker : IAlvysSyncTracker
{
    private volatile AlvysSyncSnapshot _current = new(AlvysSyncOutcome.Unknown, null, null);

    public AlvysSyncSnapshot Current => _current;

    public void Record(AlvysSyncOutcome outcome, DateTimeOffset at, string? detail = null) =>
        _current = new AlvysSyncSnapshot(outcome, at, detail);
}
