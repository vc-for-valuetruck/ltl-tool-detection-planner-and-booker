namespace LtlTool.Api.Features.Ltl.DispatchAssist;

/// <summary>
/// The pickup geography a set of recommendations is ranked against, plus its provenance. When a
/// <c>loadId</c> was supplied the origin is the resolved Alvys load's origin; otherwise it is the
/// caller-supplied origin params. Nothing here is fabricated — an absent origin state simply means
/// proximity cannot be scored (it is reported unavailable, never guessed as 0 miles).
/// </summary>
public sealed class DispatchTarget
{
    public string? LoadId { get; init; }
    public string? LoadNumber { get; init; }
    public string? OriginCity { get; init; }
    public string? OriginState { get; init; }
    public string? DestinationCity { get; init; }
    public string? DestinationState { get; init; }

    /// <summary>Required equipment strings from the load (empty when unknown / not a load-based query).</summary>
    public IReadOnlyList<string> RequiredEquipment { get; init; } = [];

    /// <summary>Honest provenance so the UI never mistakes caller input for live Alvys data.</summary>
    public required string Source { get; init; }
}

/// <summary>
/// One ranked driver+truck+trailer candidate the dispatcher can assemble. Every row carries the
/// human-readable <see cref="Reasons"/> that produced its <see cref="Score"/> so the recommendation
/// is explainable ("14 mi from origin, OFF DUTY 6h, preferred pairing with TRK-214"). Ids/labels are
/// projected straight from the read-only Alvys master data; unknown fields stay null and render "—".
/// </summary>
public sealed class DispatchCandidate
{
    public string? DriverId { get; init; }
    public string? DriverName { get; init; }
    public string? DriverEmail { get; init; }
    public string? DriverPhone { get; init; }
    public string? DriverHomeState { get; init; }

    /// <summary>
    /// Alvys driver availability signal (driver <c>Status</c> / <c>IsActive</c>). This is <b>not</b>
    /// a real-time ELD hours-of-service duty status — that is not exposed by the read-only Alvys
    /// Public API today, so it is reported honestly rather than fabricated. An OFF/available driver
    /// is still rankable (see <see cref="DispatchAssistService"/>): availability is a factor, not a
    /// hard disqualifier.
    /// </summary>
    public string DutyStatus { get; init; } = "Unknown";

    public string? TruckId { get; init; }
    public string? TruckNumber { get; init; }
    public string? TrailerId { get; init; }
    public string? TrailerNumber { get; init; }
    public string? TrailerEquipmentType { get; init; }

    /// <summary>Preferred dispatcher id from the Alvys dispatch-preference this row came from, if any.</summary>
    public string? PreferredDispatcherId { get; init; }

    /// <summary>True when this candidate matched a dispatcher-curated Alvys dispatch preference.</summary>
    public bool IsPreferredPairing { get; init; }

    /// <summary>State-centroid <i>reference</i> distance (home base → origin), or null when unknown.</summary>
    public int? ReferenceMilesFromOrigin { get; init; }

    /// <summary>Deterministic 0–100 rank score over the available factors (unavailable factors excluded).</summary>
    public int Score { get; init; }

    public IReadOnlyList<string> Reasons { get; init; } = [];
}

/// <summary>
/// Ranked recommendations for a target, best first, with the honest posture banner. Read-only:
/// every field derives from a live Alvys read or the caller's own params; nothing is written back.
/// </summary>
public sealed class DispatchRecommendationsResponse
{
    public required DispatchTarget Target { get; init; }
    public IReadOnlyList<DispatchCandidate> Candidates { get; init; } = [];

    /// <summary>True when the candidate sweep was capped (more fleet exists than was scored).</summary>
    public bool Truncated { get; init; }

    public string AlvysPosture { get; init; } =
        "Read-only. Candidates assembled from Alvys drivers/trucks/trailers/dispatch-preferences. " +
        "Nothing is written back to Alvys.";
}

/// <summary>Request to assemble (record app-side) a chosen driver+truck+trailer for a load.</summary>
public sealed class DispatchAssembleRequest
{
    public string? LoadId { get; init; }
    public string? LoadNumber { get; init; }
    public string? DriverId { get; init; }
    public string? TruckId { get; init; }
    public string? TrailerId { get; init; }

    /// <summary>The match score of the chosen candidate at selection time (audit context).</summary>
    public int? Score { get; init; }

    /// <summary>The reasons shown to the dispatcher for the chosen candidate (audit context).</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];
}

/// <summary>
/// The app-side record of an assembly decision. Mirrors the assignment-audit posture:
/// <see cref="AlvysWriteback"/> is always <c>NotPerformed</c> in this slice — the Alvys write client
/// is owned by a separate workstream and reached only through <see cref="IDispatchAssemblyWriteback"/>.
/// </summary>
public sealed record DispatchAssembly
{
    public required string Id { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
    public required string RecordedBy { get; init; }
    public string? LoadId { get; init; }
    public string? LoadNumber { get; init; }
    public string? DriverId { get; init; }
    public string? TruckId { get; init; }
    public string? TrailerId { get; init; }
    public int? Score { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>Always "NotPerformed" here — set by the Alvys write client if/when it wires in.</summary>
    public string AlvysWriteback { get; init; } = "NotPerformed";

    /// <summary>The notify-step outcome (driver + dispatcher), including the override banner state.</summary>
    public required DispatchNotifyResult Notify { get; init; }
}

/// <summary>The outcome of the notify step fired on assembly. Honest by construction.</summary>
public sealed class DispatchNotifyResult
{
    /// <summary>False when comms are flag-disabled or nothing could be sent — never a fabricated send.</summary>
    public bool Sent { get; init; }

    /// <summary>NotEnabled / Sent / Failed / NoRecipients — surfaced verbatim in the UI.</summary>
    public required string State { get; init; }

    /// <summary>True when Ltl:Comms:OverrideRecipient rerouted all mail to a single safe address.</summary>
    public bool OverrideActive { get; init; }

    /// <summary>The override address in effect (when <see cref="OverrideActive"/>), for the UI banner.</summary>
    public string? OverrideRecipient { get; init; }

    /// <summary>Who the mail was <i>intended</i> for (resolved from Alvys), shown even under override.</summary>
    public IReadOnlyList<DispatchNotifyRecipient> IntendedRecipients { get; init; } = [];

    /// <summary>Where the mail was actually addressed (the override, or the intended addresses).</summary>
    public IReadOnlyList<string> EffectiveRecipients { get; init; } = [];

    public string? Detail { get; init; }
}

/// <summary>An intended notification recipient resolved from Alvys contacts.</summary>
public sealed class DispatchNotifyRecipient
{
    /// <summary>driver / dispatcher.</summary>
    public required string Role { get; init; }
    public string? Name { get; init; }
    public string? Address { get; init; }
}
