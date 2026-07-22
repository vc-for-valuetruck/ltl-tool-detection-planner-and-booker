namespace LtlTool.Api.Features.Integrations.Yard;

/// <summary>
/// Which photo/inspection gates the yard has captured for a piece of equipment. Each flag is honest:
/// false means "not captured", never "failed" — a missing gate is surfaced, not coerced to a pass.
/// </summary>
public sealed record PhotoGates(bool Tractor, bool Trailer, bool Seal)
{
    /// <summary>No gate captured — the honest default when the yard reports nothing.</summary>
    public static readonly PhotoGates None = new(false, false, false);
}

/// <summary>
/// A read-only snapshot of a piece of equipment's / driver's physical presence at a yard, as reported
/// by the adjacent yard-management system. This is a peer signal folded into assignment validation and
/// the dock flow — it is <b>never</b> a source of operational truth (Alvys stays authoritative). No
/// photo bytes and no driver PII beyond ids cross this boundary.
/// </summary>
public sealed record YardPresence
{
    /// <summary>True when the equipment is physically present at the yard right now.</summary>
    public bool AtYard { get; init; }

    /// <summary>When the load was released from the yard (gate-out), if it has been. Null while held/onsite.</summary>
    public DateTimeOffset? ReleasedAt { get; init; }

    /// <summary>Which photo/inspection gates the yard has captured. Honest false for uncaptured gates.</summary>
    public PhotoGates Gates { get; init; } = PhotoGates.None;

    /// <summary>True when the driver has checked in / is physically present at the yard.</summary>
    public bool DriverPresent { get; init; }

    /// <summary>
    /// True when the yard has placed a security hold on the release (e.g. seal mismatch, failed
    /// inspection). A hold blocks assignment as <c>SECURITY_HOLD_ON_RELEASE</c>. Defaults false (honest:
    /// absence of a reported hold is not a reported hold).
    /// </summary>
    public bool SecurityHold { get; init; }

    /// <summary>The most recent yard event timestamp for this equipment/driver, if any.</summary>
    public DateTimeOffset? LastEventAt { get; init; }

    /// <summary>
    /// True when the yard has a record for the queried equipment/driver. False for the
    /// <see cref="NotOnRecord"/> sentinel returned on a 404 — the yard is reachable and answered, but has
    /// simply never seen this equipment. That is distinct from an <b>unavailable</b> presence (null),
    /// which means the yard could not be reached or is not configured.
    /// </summary>
    public bool OnRecord { get; init; } = true;

    /// <summary>
    /// Sentinel for "the yard answered 404 — it has no record of this equipment/driver". Everything is
    /// honestly empty/false and <see cref="OnRecord"/> is false, so callers can tell this apart from a
    /// genuine at-yard snapshot and from an unavailable (null) result.
    /// </summary>
    public static readonly YardPresence NotOnRecord = new() { OnRecord = false };
}
