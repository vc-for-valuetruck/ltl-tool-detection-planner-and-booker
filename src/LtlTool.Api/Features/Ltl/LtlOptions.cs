namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// Configuration for the LTL decision-support layer. Bound from the <c>Ltl</c> configuration
/// section so thresholds/weights/classification can be tuned per environment without code
/// changes. All values have safe, explainable defaults.
/// </summary>
public sealed class LtlOptions
{
    public const string SectionName = "Ltl";

    /// <summary>
    /// How many loads a normalized search/worklist/exception sweep will pull from Alvys before
    /// stopping. Bounds the number of upstream calls; results past this are reported as truncated.
    /// </summary>
    public int MaxLoadsScanned { get; set; } = 500;

    /// <summary>Page size used when sweeping Alvys loads/search internally.</summary>
    public int AlvysPageSize { get; set; } = 100;

    /// <summary>
    /// Upper bound on driver/equipment candidates scored for a single match request, so a large
    /// fleet does not turn one "recommend a driver" call into an unbounded scoring sweep.
    /// </summary>
    public int MaxMatchCandidates { get; set; } = 50;

    /// <summary>Default number of ranked match recommendations returned for a load.</summary>
    public int DefaultMatchResults { get; set; } = 5;

    /// <summary>
    /// Load-type tokens (case-insensitive substring) that classify a load as LTL/partial/volume.
    /// </summary>
    public List<string> LtlLoadTypes { get; set; } = ["LTL", "Partial", "Volume"];

    /// <summary>
    /// Equipment tokens (case-insensitive substring) that hint at LTL/partial freight when the
    /// load type itself is silent.
    /// </summary>
    public List<string> LtlEquipmentHints { get; set; } = ["LTL", "Partial"];

    /// <summary>
    /// Days after delivery without an invoice before a load is flagged as a stale
    /// not-yet-invoiced billing exception.
    /// </summary>
    public int StaleUninvoicedDays { get; set; } = 7;

    /// <summary>
    /// Upper bound on how many already-flagged loads in the exception sweep are enriched with a
    /// per-load visibility-history fetch. Bounds the extra upstream calls; loads past the cap keep
    /// their load-derived exceptions only (visibility-only signals still surface on the detail path).
    /// </summary>
    public int MaxVisibilityEnriched { get; set; } = 25;

    /// <summary>
    /// Equipment-event types (case-insensitive substring) that count as an availability conflict
    /// when they overlap a load's pickup/delivery window (repair/maintenance/out-of-service/other).
    /// </summary>
    public List<string> EquipmentConflictEventTypes { get; set; } =
        ["Repair", "Maintenance", "Out of Service", "Other"];

    /// <summary>Match scoring weights/thresholds.</summary>
    public LtlMatchOptions Match { get; set; } = new();
}

/// <summary>
/// Deterministic match-scoring configuration: per-factor weights and the score thresholds that
/// map a 0–100 score onto an explainable label. Defaults are intentionally conservative.
/// </summary>
public sealed class LtlMatchOptions
{
    public double EquipmentWeight { get; set; } = 30;
    public double WeightCapacityWeight { get; set; } = 25;
    public double DriverReadinessWeight { get; set; } = 25;
    public double FleetAlignmentWeight { get; set; } = 10;
    public double GeographyWeight { get; set; } = 10;

    /// <summary>
    /// Weight of the equipment-availability factor (truck/trailer repair/maintenance events
    /// overlapping the load window). Only scored when event data was actually fetched for the
    /// candidate over a known window — otherwise reported as not-scored and excluded from the
    /// denominator.
    /// </summary>
    public double EquipmentEventsWeight { get; set; } = 15;

    /// <summary>Score (0–100) at/above which a candidate is an Excellent Match.</summary>
    public int ExcellentThreshold { get; set; } = 85;
    public int GoodThreshold { get; set; } = 70;
    public int PossibleThreshold { get; set; } = 55;
    public int RiskyThreshold { get; set; } = 40;

    /// <summary>
    /// Days before a license/medical expiry at which a driver is flagged as "expiring soon"
    /// (a weak factor, not a disqualifier).
    /// </summary>
    public int CredentialExpiryWarningDays { get; set; } = 30;
}
