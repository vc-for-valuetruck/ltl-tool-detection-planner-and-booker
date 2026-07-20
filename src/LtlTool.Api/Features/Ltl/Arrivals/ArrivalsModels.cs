namespace LtlTool.Api.Features.Ltl.Arrivals;

/// <summary>
/// The Laredo Arrivals Board (Phase 8.1): every truck/trailer scheduled to arrive at the Laredo
/// yard on a given day, read live and read-only from Alvys trips. This is the FIRST surface on the
/// LTL home page — Ben Beddes / Jordan Baumgart monitor it for Laredo → Dallas LTL opportunities.
///
/// <para>
/// Every value is derived from a real Alvys trip/stop. Nothing is fabricated: when an arrival
/// window, driver name, equipment unit or ownership cannot be determined it is surfaced as null /
/// <see cref="ArrivalOwnership.Unknown"/>, never guessed. A bounded sweep that hits its cap is
/// reported via <see cref="Truncated"/> so the UI can say the list is a floor, not a total.
/// </para>
/// </summary>
public sealed class LaredoArrivalsBoard
{
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>The yard day this board covers (date-only, UTC-anchored), from the request.</summary>
    public required DateOnly Date { get; init; }

    /// <summary>Yard code this board is scoped to. Phase 8.1 is Laredo only.</summary>
    public string Yard { get; init; } = "LAREDO";

    /// <summary>
    /// Arrivals for the day, Dallas-bound first (the pilot corridor), then by scheduled arrival.
    /// </summary>
    public IReadOnlyList<LaredoArrival> Arrivals { get; init; } = [];

    /// <summary>True when the underlying trip sweep hit its scan cap; the list is then a floor.</summary>
    public bool Truncated { get; init; }

    public string Source { get; init; } =
        "Live Alvys trips with a Laredo-area stop scheduled this day. Read-only; no writeback.";
}

/// <summary>One truck/trailer arriving at Laredo, with its onward Laredo → Dallas framing.</summary>
public sealed class LaredoArrival
{
    public required string TripId { get; init; }
    public string? TripNumber { get; init; }
    public string? LoadNumber { get; init; }
    public string? OrderNumber { get; init; }

    /// <summary>The tractor on the trip (unit + fleet + honest ownership).</summary>
    public ArrivalEquipment? Truck { get; init; }

    /// <summary>The trailer on the trip (unit + equipment type/length + honest ownership).</summary>
    public ArrivalEquipment? Trailer { get; init; }

    /// <summary>Driver display name only (no contact detail); null when Alvys carries none.</summary>
    public string? DriverName { get; init; }

    /// <summary>Where the truck is inbound from — the trip's first (origin) stop.</summary>
    public ArrivalPlace? InboundFrom { get; init; }

    /// <summary>The Laredo-area stop the truck is arriving at.</summary>
    public required ArrivalPlace Laredo { get; init; }

    /// <summary>Scheduled arrival window at the Laredo stop (begin), null when Alvys carries none.</summary>
    public DateTimeOffset? ScheduledArrivalStart { get; init; }

    /// <summary>Scheduled arrival window at the Laredo stop (end), null when Alvys carries none.</summary>
    public DateTimeOffset? ScheduledArrivalEnd { get; init; }

    /// <summary>Actual arrival timestamp at the Laredo stop, once Alvys reports it.</summary>
    public DateTimeOffset? ArrivedAt { get; init; }

    /// <summary>Actual departure timestamp from the Laredo stop, once Alvys reports it.</summary>
    public DateTimeOffset? DepartedAt { get; init; }

    /// <summary>Live status chip: Scheduled → Arrived → Departed, from the Laredo stop.</summary>
    public required ArrivalStatus Status { get; init; }

    /// <summary>
    /// Predicted arrival instant, reusing the Phase 7.3 ETA layer (loaded miles ÷ average speed,
    /// anchored at actual pickup). Null when the trip is not in transit or carries no miles.
    /// </summary>
    public DateTimeOffset? PredictedArrivalAt { get; init; }

    /// <summary>Provenance of <see cref="PredictedArrivalAt"/>; always set when an ETA reason exists.</summary>
    public string? EtaBasis { get; init; }

    /// <summary>True when the predicted arrival is past the scheduled window plus grace.</summary>
    public bool PredictedLate { get; init; }

    /// <summary>
    /// True when a stop after Laredo resolves to the Dallas yard — the pilot LTL opportunity.
    /// These arrivals sort first and are highlighted on the board.
    /// </summary>
    public bool DallasBound { get; init; }

    /// <summary>
    /// The Laredo → Dallas → destination(s) onward chain: the cities/states of the stops that
    /// come after the Laredo stop on this trip. Empty when Laredo is the final stop.
    /// </summary>
    public IReadOnlyList<ArrivalPlace> OnwardStops { get; init; } = [];
}

/// <summary>A truck or trailer on an arriving trip, with honest ownership labelling.</summary>
public sealed class ArrivalEquipment
{
    /// <summary>The Alvys equipment id (always present on the trip reference).</summary>
    public required string Id { get; init; }

    /// <summary>Unit number resolved from equipment master data; null when unresolved.</summary>
    public string? Unit { get; init; }

    /// <summary>Equipment type (e.g. Dry Van), from the trip trailer ref or master data.</summary>
    public string? EquipmentType { get; init; }

    /// <summary>Trailer length in feet from master data; null for tractors / when unknown.</summary>
    public decimal? LengthFeet { get; init; }

    /// <summary>Owning fleet name from master data; null when unresolved.</summary>
    public string? FleetName { get; init; }

    /// <summary>Honest ownership label — never guessed.</summary>
    public ArrivalOwnership Ownership { get; init; } = ArrivalOwnership.Unknown;
}

/// <summary>A place on the trip (city/state) — the union of load/trip stop address shapes.</summary>
public sealed class ArrivalPlace
{
    public string? City { get; init; }
    public string? State { get; init; }

    /// <summary>A "City, ST" label, or whichever part is present; null when neither is.</summary>
    public string? Label =>
        (string.IsNullOrWhiteSpace(City), string.IsNullOrWhiteSpace(State)) switch
        {
            (false, false) => $"{City}, {State}",
            (false, true) => City,
            (true, false) => State,
            _ => null,
        };
}

/// <summary>Live arrival status of a truck at the Laredo stop.</summary>
public enum ArrivalStatus
{
    /// <summary>Not yet arrived at the Laredo stop.</summary>
    Scheduled = 0,

    /// <summary>Arrived at the Laredo stop (ArrivedAt reported, not yet departed).</summary>
    Arrived = 1,

    /// <summary>Departed the Laredo stop (DepartedAt reported).</summary>
    Departed = 2,
}

/// <summary>
/// Honest ownership posture for a truck/trailer. Derived only from determinable Alvys fleet
/// signals — never guessed. <see cref="ThirdPartyLeased"/> is reserved for when Alvys carries a
/// determinable third-party/leased marker; today the resolver only ever returns
/// <see cref="Fleet"/> (a fleet resolved from master data) or <see cref="Unknown"/> (gray, honest).
/// </summary>
public enum ArrivalOwnership
{
    /// <summary>Ownership not determinable from Alvys — shown gray. Honest, not a guess.</summary>
    Unknown = 0,

    /// <summary>Subsidiary-owned: the asset resolves to a known internal fleet.</summary>
    Fleet = 1,

    /// <summary>Third-party-leased asset, when Alvys carries a determinable marker.</summary>
    ThirdPartyLeased = 2,
}
