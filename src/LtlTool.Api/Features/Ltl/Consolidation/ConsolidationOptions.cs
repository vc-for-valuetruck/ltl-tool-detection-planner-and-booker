namespace LtlTool.Api.Features.Ltl.Consolidation;

/// <summary>
/// Configuration for the Laredo → Dallas consolidation planner (Phase 1 pilot).
/// Bound from the <c>Ltl:Consolidation</c> section so corridor thresholds and per-customer
/// visibility policy can be tuned per environment without code changes. All values are
/// deterministic and explainable — nothing is inferred from missing Alvys data.
///
/// <para>
/// Phase 1 is intentionally scoped to one corridor (Laredo → Dallas) and one posture
/// (read-only against Alvys, click-card output). Widening or auto-writeback happens in later
/// phases via new options, not by relaxing the defaults here.
/// </para>
/// </summary>
public sealed class ConsolidationOptions
{
    public const string SectionName = "Ltl:Consolidation";

    /// <summary>
    /// Whether the consolidation planner is enabled at all. Ships true; a hard off-switch
    /// leadership can flip while the tool is in early pilot without redeploying.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Regions from which consolidation candidates are allowed. Defaults to <c>US</c> only:
    /// south-of-border consolidation points are disqualified per the anti-failure map (3j) so
    /// customs/bond/liability posture is not accidentally mixed. Comparison is case-insensitive
    /// and matches the two-letter ISO code on the load's origin place.
    /// </summary>
    public List<string> AllowedRegions { get; set; } = ["US"];

    /// <summary>Configured consolidation warehouses the planner can route through.</summary>
    public List<ConsolidationWarehouseOptions> Warehouses { get; set; } =
    [
        // Phase 1 pilot yards. Real geo-coordinates live in a Phase 2 lookup; the two-letter
        // state is what Phase 1 filters on because Alvys origin/destination already carry it.
        new ConsolidationWarehouseOptions
        {
            Code = "LAREDO",
            Name = "Laredo yard",
            State = "TX",
            NearbyCities = ["Laredo", "Nuevo Laredo", "Encinal"],
        },
        new ConsolidationWarehouseOptions
        {
            Code = "DALLAS",
            Name = "Dallas 154-door yard",
            State = "TX",
            NearbyCities =
            [
                "Dallas", "Fort Worth", "Arlington", "Grand Prairie",
                "Irving", "Denton", "Mesquite", "Garland", "Plano",
            ],
        },
    ];

    /// <summary>
    /// Pilot corridors the planner recognises. Each corridor pairs an origin warehouse with a
    /// destination warehouse and defines the timing window inside which sibling candidates are
    /// eligible. Phase 1 has exactly one corridor: LAREDO → DALLAS.
    /// </summary>
    public List<ConsolidationCorridorOptions> Corridors { get; set; } =
    [
        new ConsolidationCorridorOptions
        {
            Code = "LAREDO_TO_DALLAS",
            OriginWarehouseCode = "LAREDO",
            DestinationWarehouseCode = "DALLAS",
            PickupWindowDays = 2,
            DeliveryWindowDays = 3,
        },
    ];

    /// <summary>
    /// Per-customer consolidation policy. Ships empty so the planner defaults every customer
    /// to <see cref="CustomerConsolidationTier.Unknown"/> → "confirm with account owner" per
    /// anti-failure map 3f. Real Value Truck values are seeded from the yard visit:
    /// Kroger/Ring never, Masonite/Irving notify, Verdef is the current friction point.
    /// </summary>
    public List<ConsolidationCustomerPolicyOptions> CustomerPolicies { get; set; } =
    [
        new() { Customer = "Kroger", Tier = CustomerConsolidationTier.Never },
        new() { Customer = "Ring",   Tier = CustomerConsolidationTier.Never },
        new() { Customer = "Masonite", Tier = CustomerConsolidationTier.NotifyRequired },
        new() { Customer = "Irving",   Tier = CustomerConsolidationTier.NotifyRequired },
    ];

    /// <summary>Upper bound on candidates returned per candidate-list request.</summary>
    public int MaxCandidatesReturned { get; set; } = 25;
}

/// <summary>A configured consolidation yard.</summary>
public sealed class ConsolidationWarehouseOptions
{
    /// <summary>Stable identifier used in corridors and audit records (e.g. <c>LAREDO</c>).</summary>
    public string Code { get; set; } = "";

    /// <summary>Human-facing label ("Laredo yard").</summary>
    public string Name { get; set; } = "";

    /// <summary>Two-letter ISO state code the warehouse sits in.</summary>
    public string State { get; set; } = "";

    /// <summary>
    /// Cities considered "near" this warehouse for lane-fit evaluation. Case-insensitive
    /// prefix / substring match against the load's origin/destination place.
    /// </summary>
    public List<string> NearbyCities { get; set; } = [];
}

/// <summary>A pilot consolidation corridor pairing an origin and destination warehouse.</summary>
public sealed class ConsolidationCorridorOptions
{
    /// <summary>Stable code (e.g. <c>LAREDO_TO_DALLAS</c>).</summary>
    public string Code { get; set; } = "";

    /// <summary>Origin warehouse Code (must match one of <see cref="ConsolidationOptions.Warehouses"/>).</summary>
    public string OriginWarehouseCode { get; set; } = "";

    /// <summary>Destination warehouse Code.</summary>
    public string DestinationWarehouseCode { get; set; } = "";

    /// <summary>
    /// Pickup window in days: siblings whose scheduled pickup is within ±N days of the seed
    /// load's pickup count as "timing-fit good". Beyond that but within 2×N is "tight".
    /// Beyond 2×N is disqualified.
    /// </summary>
    public int PickupWindowDays { get; set; } = 2;

    /// <summary>Delivery window in days: same evaluation shape as PickupWindowDays.</summary>
    public int DeliveryWindowDays { get; set; } = 3;
}

/// <summary>
/// Per-customer consolidation policy. Case-insensitive customer-name match against the load's
/// CustomerName. If a load's customer does not match any policy row, the tier defaults to
/// <see cref="CustomerConsolidationTier.Unknown"/> and the planner flags it as
/// "confirm with account owner" rather than silent-allow.
/// </summary>
public sealed class ConsolidationCustomerPolicyOptions
{
    public string Customer { get; set; } = "";
    public CustomerConsolidationTier Tier { get; set; } = CustomerConsolidationTier.Unknown;
}

/// <summary>
/// Customer consolidation posture. Deliberately narrow so per-customer nuance stays a data
/// concern (the policy list) instead of a code concern.
/// </summary>
public enum CustomerConsolidationTier
{
    /// <summary>No policy on file. UI prompts "confirm with account owner".</summary>
    Unknown = 0,

    /// <summary>Customer accepts consolidation without notification.</summary>
    Allowed = 1,

    /// <summary>Customer accepts consolidation but requires notification to the right people.</summary>
    NotifyRequired = 2,

    /// <summary>Customer never accepts consolidation. Candidate is blocked.</summary>
    Never = 3,
}
