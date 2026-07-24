namespace LtlTool.Api.Features.Ltl.Reporting;

/// <summary>
/// Which party an accessorial line belongs to. Matches the parties Alvys itself distinguishes:
/// the customer-side charge (on the Load), and the carrier-side pay context on the Trip (Carrier,
/// each of the two driver slots, and an owner-operator). There is no generic "Driver" party in the
/// verified Alvys trip shape — <see cref="AlvysTrip.Driver1"/>/<see cref="AlvysTrip.Driver2"/> are
/// the two team-driver slots, so this enum mirrors them by name rather than collapsing them.
/// </summary>
public enum AccessorialEntityType
{
    Customer,
    Carrier,
    Driver1,
    Driver2,
    OwnerOperator,
}

/// <summary>
/// A normalized accessorial line, captured as a byproduct of an existing Alvys load/trip read
/// (never a dedicated Alvys call of its own). Alvys does not expose a standalone accessorials
/// endpoint or a stable per-line id — accessorials arrive nested on Loads (customer side) and
/// Trips (carrier/driver/owner-operator side) as <c>{Type, Description, Amount}</c> tuples with no
/// identity of their own. Because there is no stable Alvys id to upsert against, capture is
/// content-keyed (<see cref="LoadId"/> + <see cref="TripId"/> + <see cref="EntityType"/> +
/// <see cref="Type"/> + <see cref="Description"/> + <see cref="Amount"/>): an unchanged line is
/// re-seen and only <see cref="LastSeenAt"/> advances; a line whose amount/description changes is
/// captured as a new row, giving a durable history rather than silently overwriting the prior value.
///
/// <para>
/// Unified across customer/carrier/driver/owner-operator so a single table serves customer billing
/// review, carrier/driver settlement reconciliation, and external reporting (e.g. Power BI) without
/// hand-joining four different nested JSON shapes per load.
/// </para>
/// </summary>
public sealed class AccessorialRecord
{
    public required string Id { get; set; }

    /// <summary>Alvys load id (GUID). Always present — every accessorial line traces to a load.</summary>
    public required string LoadId { get; set; }

    /// <summary>Human load number, when known at capture time. Denormalized for reporting joins.</summary>
    public string? LoadNumber { get; set; }

    /// <summary>Alvys trip id. Null for customer-side lines (those live on the Load, not a Trip).</summary>
    public string? TripId { get; set; }

    public required AccessorialEntityType EntityType { get; set; }

    /// <summary>Accessorial category (Detention, Lumper, TONU, Layover, etc.) verbatim from Alvys.</summary>
    public string? Type { get; set; }

    public string? Description { get; set; }

    public decimal? Amount { get; set; }

    /// <summary>When this tool first captured this exact line (content-key match).</summary>
    public required DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>When this tool most recently re-observed this exact line unchanged.</summary>
    public required DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>
/// A normalized, point-in-time assignment snapshot, captured as a byproduct of an existing Alvys
/// trip read. Alvys has no standalone assignment resource or assignment history of its own —
/// assignment fields (driver/truck/trailer/carrier/dispatcher) live on the Trip, and Alvys only
/// ever reports the current values, not who held them before. This table is what gives the LTL
/// tool reassignment history / driver-truck-dispatcher utilization reporting: a new row is
/// appended only when the captured snapshot actually differs from the last one stored for the
/// same load (change detection), so re-viewing an unchanged load never bloats the table — the row
/// count reflects real reassignment events, not read frequency.
/// </summary>
public sealed class LoadAssignmentRecord
{
    public required string Id { get; set; }

    public required string LoadId { get; set; }
    public string? LoadNumber { get; set; }

    /// <summary>Alvys trip id this snapshot was read from. Null if the load has no trip yet.</summary>
    public string? TripId { get; set; }

    /// <summary>Trip status at capture time (e.g. Dispatched, Delivered) — Alvys' own field.</summary>
    public string? Status { get; set; }

    public string? CarrierId { get; set; }
    public string? CarrierName { get; set; }

    public string? Driver1Id { get; set; }
    public string? Driver1Name { get; set; }

    public string? Driver2Id { get; set; }
    public string? Driver2Name { get; set; }

    public string? OwnerOperatorId { get; set; }
    public string? OwnerOperatorName { get; set; }

    /// <summary>Alvys truck reference id. Alvys' equipment-ref shape carries only an id, no number.</summary>
    public string? TruckId { get; set; }

    public string? TrailerId { get; set; }

    public string? DispatcherId { get; set; }
    public string? DispatchedBy { get; set; }

    /// <summary>When Alvys recorded the carrier assignment on the trip, if known.</summary>
    public DateTimeOffset? CarrierAssignedAt { get; set; }

    /// <summary>When this tool captured this snapshot (its own clock, not an Alvys timestamp).</summary>
    public required DateTimeOffset CapturedAt { get; set; }
}
