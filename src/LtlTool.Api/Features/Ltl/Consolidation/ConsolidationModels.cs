namespace LtlTool.Api.Features.Ltl.Consolidation;

public sealed record ConsolidationOpportunitiesResponse
{
    public required IReadOnlyList<ConsolidationOpportunity> Opportunities { get; init; }
    public required int TotalScanned { get; init; }
    public required int TotalPairsFound { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required string DataSource { get; init; }
}

public sealed record ConsolidationOpportunity
{
    public required int Rank { get; init; }
    public required string OriginState { get; init; }
    public required string DestinationState { get; init; }
    public required string OriginCity { get; init; }
    public required string DestinationCity { get; init; }
    public required DateOnly PickupDate { get; init; }
    public required string CustomerName { get; init; }
    public required decimal CombinedRevenue { get; init; }
    public required decimal ParentLinehaulMiles { get; init; }
    public required decimal CombinedRpm { get; init; }
    public required decimal ProjectedUplift { get; init; }
    public required ConsolidationOpportunityLoad Parent { get; init; }
    public required IReadOnlyList<ConsolidationOpportunityLoad> Siblings { get; init; }

    /// <summary>
    /// OR-Tools capacity/cost annotation. Null when <c>Ltl:Optimization:Solver:Enabled</c> is off or
    /// the solver could not produce a plan — in which case the heuristic ranking stands unchanged.
    /// </summary>
    public ConsolidationOptimizationAnnotation? Optimization { get; init; }
}

public sealed record ConsolidationOpportunityLoad
{
    public required string LoadNumber { get; init; }
    public required string LoadId { get; init; }
    public required string CustomerName { get; init; }
    public required string OriginCity { get; init; }
    public required string OriginState { get; init; }
    public required string DestinationCity { get; init; }
    public required string DestinationState { get; init; }
    public required decimal LinehaulAmount { get; init; }
    public required decimal Miles { get; init; }
    public required decimal Rpm { get; init; }
    public required decimal? WeightPounds { get; init; }
}

public sealed record ConsolidationAuditRequest
{
    public required string ParentLoadNumber { get; init; }
    public required IReadOnlyList<string> SiblingLoadNumbers { get; init; }
    public decimal? CombinedRevenue { get; init; }
    public decimal? CombinedRpm { get; init; }
}

public sealed record ConsolidationAuditResponse
{
    public required string AuditId { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
    public required string RecordedBy { get; init; }
    public required string ParentLoadNumber { get; init; }
    public required IReadOnlyList<string> SiblingLoadNumbers { get; init; }
    public decimal? CombinedRevenue { get; init; }
    public decimal? CombinedRpm { get; init; }
}

/// <summary>
/// Fit status for a single consolidation factor. Deliberately narrow: green/yellow/red/gray.
/// The UI renders each factor as a single-word chip; no numeric score, no black box. Missing
/// data resolves to <see cref="Unknown"/>, never invented.
/// </summary>
public enum ConsolidationFit
{
    /// <summary>Factor could not be evaluated because required Alvys data was missing.</summary>
    Unknown = 0,

    /// <summary>Factor evaluated as fine — green chip.</summary>
    Good = 1,

    /// <summary>Factor evaluated as marginal — yellow chip; dispatcher confirmation recommended.</summary>
    Tight = 2,

    /// <summary>Factor evaluated as a hard disqualifier — red chip; candidate is blocked.</summary>
    Blocked = 3,
}

/// <summary>
/// One evaluated factor on a consolidation candidate. Every factor cites the Alvys
/// signal it was derived from so a dispatcher can trace a chip back to the source
/// record.
/// </summary>
public sealed class ConsolidationFactor
{
    /// <summary>Factor name for the UI chip: "Lane fit", "Timing fit", "Customer".</summary>
    public required string Name { get; init; }

    /// <summary>Evaluated fit status.</summary>
    public required ConsolidationFit Fit { get; init; }

    /// <summary>One-sentence rationale suitable to render as chip hover / tooltip.</summary>
    public required string Rationale { get; init; }
}

/// <summary>
/// A consolidation candidate: a load that could be combined with the seed load into one
/// linehaul trip through the pilot corridor. All values are derived from live Alvys data;
/// missing signals surface as <see cref="ConsolidationFit.Unknown"/> chips rather than being
/// inferred.
/// </summary>
public sealed class ConsolidationCandidate
{
    public required string LoadId { get; init; }
    public string? LoadNumber { get; init; }
    public string? CustomerName { get; init; }

    /// <summary>Origin place labeled "City, ST" from Alvys.</summary>
    public string? OriginLabel { get; init; }

    /// <summary>Destination place labeled "City, ST" from Alvys.</summary>
    public string? DestinationLabel { get; init; }

    /// <summary>Scheduled pickup timestamp on the load, if known.</summary>
    public DateTimeOffset? ScheduledPickupAt { get; init; }

    /// <summary>Scheduled delivery timestamp on the load, if known.</summary>
    public DateTimeOffset? ScheduledDeliveryAt { get; init; }

    /// <summary>Customer-facing revenue when Alvys has a rate on the load; null otherwise.</summary>
    public decimal? Revenue { get; init; }

    /// <summary>Weight in lbs from Alvys, or null when not provided. Never fabricated.</summary>
    public decimal? WeightLbs { get; init; }

    /// <summary>Which corridor this candidate matched (e.g. <c>LAREDO_TO_DALLAS</c>).</summary>
    public required string CorridorCode { get; init; }

    /// <summary>Per-factor fit chips: Lane fit, Timing fit, Customer.</summary>
    public required IReadOnlyList<ConsolidationFactor> Factors { get; init; }

    /// <summary>
    /// True when at least one factor is <see cref="ConsolidationFit.Blocked"/> and the
    /// candidate cannot be added to a plan without operator override. The UI hides the
    /// "Add sibling" action for blocked candidates.
    /// </summary>
    public bool IsBlocked { get; init; }

    /// <summary>
    /// Customer consolidation posture applied to this candidate (from the policy list).
    /// The UI uses this to show the "confirm with account owner" prompt when Unknown.
    /// </summary>
    public CustomerConsolidationTier CustomerTier { get; init; }
}

/// <summary>
/// Client request for a consolidation plan preview: a parent load plus one or more sibling
/// loads on the same pilot corridor. The server verifies every id resolves against live
/// Alvys, applies the same corridor + customer-policy gates as the candidate service, and
/// returns the plan preview + click-card content. Nothing writes upstream.
/// </summary>
public sealed class ConsolidationPlanRequest
{
    public string ParentLoadId { get; set; } = "";
    public List<string> SiblingLoadIds { get; set; } = [];
    public string? CorridorCode { get; set; }
}

/// <summary>
/// One sibling row inside a plan preview: the load's identifier, its origin/destination
/// labels, its revenue (when Alvys carries a rate), and any operational cautions the
/// planner surfaced (missing pallet count, unknown customer policy, etc). No fabricated
/// values.
/// </summary>
public sealed class ConsolidationPlanSibling
{
    public required string LoadId { get; init; }
    public string? LoadNumber { get; init; }
    public string? CustomerName { get; init; }
    public string? OriginLabel { get; init; }
    public string? DestinationLabel { get; init; }
    public DateTimeOffset? ScheduledPickupAt { get; init; }
    public DateTimeOffset? ScheduledDeliveryAt { get; init; }
    public decimal? Revenue { get; init; }
    public decimal? WeightLbs { get; init; }

    /// <summary>
    /// Driver trip rate (<c>Trip.TripValue.Amount</c>). Null when no trip was fetched or the
    /// trip carries no rate. Used for combined-RPM math — the operator-facing number Junior /
    /// Holly / Brian care about.
    /// </summary>
    public decimal? DriverTripRate { get; init; }

    /// <summary>
    /// Driver loaded miles (<c>Trip.LoadedMileage.Distance.Value</c>). Null when no trip was
    /// fetched. On the child sibling this is the miles Phase 5 will zero out; on the parent
    /// it stays populated and is the denominator of the combined-RPM formula.
    /// </summary>
    public decimal? LoadedMiles { get; init; }

    public CustomerConsolidationTier CustomerTier { get; init; }

    /// <summary>
    /// Where <see cref="CustomerTier"/> came from — a customer-authored Alvys note, the static
    /// default policy, or nothing on file — so the UI can badge the policy's provenance honestly.
    /// </summary>
    public CustomerPolicySource CustomerPolicySource { get; init; }

    /// <summary>
    /// Plain-language cautions the dispatcher must resolve before executing this plan.
    /// Examples: "L-100237 pallet count is missing — visual verify at Laredo dock",
    /// "Masonite requires customer notification before consolidation".
    /// </summary>
    public IReadOnlyList<string> Cautions { get; init; } = [];
}

/// <summary>
/// Server-generated content of the copy-pasteable Alvys click card. The service produces
/// this deterministically so the dispatcher pastes the exact text into Alvys — no
/// hand-editing of trip-reference values or waypoint instructions.
/// </summary>
public sealed class ConsolidationClickCard
{
    /// <summary>The full click card as one multi-line string, ready to copy.</summary>
    public required string PlainText { get; init; }

    /// <summary>The Alvys trip-reference value the dispatcher pastes on parent + siblings.</summary>
    public required string TripReferenceValue { get; init; }

    /// <summary>The main-load id the dispatcher pastes on siblings pointing at the parent.</summary>
    public required string MainLoadIdReferenceValue { get; init; }
}

/// <summary>
/// Full plan preview response. The service treats plan generation as a preview action —
/// even the audit store is a separate call (see follow-up PR) — because Phase 1 is
/// explicitly read-only and each step is legible.
/// </summary>
public sealed class ConsolidationPlanResponse
{
    /// <summary>Unique preview id, useful for auditing and support. Not persisted at this step.</summary>
    public required string PreviewId { get; init; }

    public required string CorridorCode { get; init; }

    /// <summary>Parent load, normalized.</summary>
    public required LtlLoadSummary Parent { get; init; }

    /// <summary>Siblings that will be zeroed-out and reference the parent.</summary>
    public required IReadOnlyList<ConsolidationPlanSibling> Siblings { get; init; }

    /// <summary>
    /// Sum of Parent.Revenue + all sibling Revenue values (customer-billing rates). This is
    /// the total the customer owes for the moves being combined — kept for operator context,
    /// not used as the RPM numerator.
    /// </summary>
    public decimal? CombinedRevenue { get; init; }

    /// <summary>
    /// Parent's customer-facing linehaul mileage (<c>Load.CustomerMileage</c>). Kept for
    /// operator context alongside <see cref="DriverLoadedMiles"/>. Never used as the RPM
    /// denominator — that would mix billing miles with driver-facing math.
    /// </summary>
    public decimal? LinehaulMiles { get; init; }

    /// <summary>
    /// Parent's driver-facing loaded miles (<c>Trip.LoadedMileage.Distance.Value</c>). This is
    /// the actual denominator of <see cref="CombinedRevenuePerMile"/>. Null when no trip was
    /// fetched — in which case the RPM stays null (never guessed).
    /// </summary>
    public decimal? DriverLoadedMiles { get; init; }

    /// <summary>
    /// Sum of Parent.DriverTripRate + all sibling DriverTripRate values. The numerator of
    /// <see cref="CombinedRevenuePerMile"/>.
    /// </summary>
    public decimal? CombinedDriverTripValue { get; init; }

    /// <summary>
    /// Combined driver trip value divided by parent's driver loaded miles. This is the
    /// operator-facing “did we catch a good consolidation?” number — the driver-RPM
    /// leadership will read against the audit trail. Null unless both inputs are known.
    /// Corrected 2026-07-18 per Reuben 2026-07-17 sync + empirical MCP verification (see
    /// <c>docs/ALVYS_API_DECISIONS.md</c>).
    /// </summary>
    public decimal? CombinedRevenuePerMile { get; init; }

    /// <summary>The click card the dispatcher pastes into Alvys.</summary>
    public required ConsolidationClickCard ClickCard { get; init; }

    /// <summary>
    /// Trailer-fit verdict for the combined load (parent + corridor-valid siblings). Present only
    /// when the trailer-fit engine is enabled; null when the <c>NullTrailerFitService</c> is active
    /// so the SPA shows "verify at dock" rather than implying a fit was checked. Never carries a
    /// fabricated number — an unknown verdict surfaces as <see cref="ConsolidationTrailerFit.Verdict"/>
    /// = <c>Unknown</c>.
    /// </summary>
    public ConsolidationTrailerFit? TrailerFit { get; init; }

    /// <summary>
    /// Blockers that make this plan illegal even as a preview: the parent isn't a load, a
    /// sibling isn't on the corridor, a sibling belongs to a Never-consolidate customer.
    /// When non-empty, the SPA must not offer the copy-card action.
    /// </summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];

    /// <summary>
    /// Sibling delivery waypoints in the driven order the click card uses (parent origin anchored
    /// separately). Order matches <see cref="Siblings"/>. Stored so the future write path can
    /// reproduce the same sequence.
    /// </summary>
    public IReadOnlyList<string> StopSequence { get; init; } = [];

    /// <summary>
    /// True when the OR-Tools stop sequencer actually reordered the siblings. False when input
    /// order was preserved — the honest default when the solver is off or no stop coordinates are
    /// available (Alvys exposes city/state only today).
    /// </summary>
    public bool StopsOptimized { get; init; }
}

/// <summary>
/// SPA-facing projection of a trailer-fit evaluation for a consolidation plan. Mirrors the fields
/// the plan-detail page renders (issue #76): a coarse verdict, whether the numbers came from assumed
/// dimensions, the linear-feet / utilization the packer reported, and the weight/pallet totals versus
/// the trailer capacity. Every numeric field is nullable and stays null when the value is genuinely
/// unknown — never coerced to zero.
/// </summary>
public sealed class ConsolidationTrailerFit
{
    /// <summary>Coarse verdict: <c>Unknown</c> / <c>Fits</c> / <c>DoesNotFit</c>.</summary>
    public required string Verdict { get; init; }

    /// <summary>Plain-language rationale for the UI (safe to render directly).</summary>
    public required string Rationale { get; init; }

    /// <summary>True when the verdict was computed from assumed dimensions ("estimated fit").</summary>
    public bool EstimatedFit { get; init; }

    /// <summary>Linear feet of trailer floor occupied (packer KPI). Null when the packer did not run.</summary>
    public decimal? LinearFeet { get; init; }

    /// <summary>Weight utilization 0–1 (combined weight ÷ trailer max). Null when weight is unknown.</summary>
    public decimal? WeightUtilization { get; init; }

    /// <summary>Cube/floor utilization 0–1 from the packer. Null when the packer did not run.</summary>
    public decimal? CubeUtilization { get; init; }

    /// <summary>Combined known weight (lbs) — a floor when <see cref="WeightUnknown"/> is true.</summary>
    public decimal? TotalWeightLbs { get; init; }

    /// <summary>Trailer max payload (lbs) the plan was checked against.</summary>
    public decimal? TrailerMaxWeightLbs { get; init; }

    /// <summary>Combined pallet count when derivable; null otherwise.</summary>
    public int? TotalPallets { get; init; }

    /// <summary>Trailer pallet positions the plan was checked against.</summary>
    public int? TrailerMaxPallets { get; init; }

    /// <summary>True when combined weight/pallets exceed the trailer capacity.</summary>
    public bool CapacityExceeded { get; init; }

    /// <summary>True when one or more loads had no weight — the UI shows "≥ N lb".</summary>
    public bool WeightUnknown { get; init; }
}

/// <summary>
/// The full candidate-list response for a seed load. Includes the seed itself (echoed so the
/// SPA can render the parent row without a second fetch) plus the ranked candidate siblings.
/// </summary>
public sealed class ConsolidationCandidateResponse
{
    public required string CorridorCode { get; init; }

    /// <summary>The seed load, normalized. Null when the seed id could not be resolved.</summary>
    public LtlLoadSummary? Seed { get; init; }

    /// <summary>
    /// Ranked candidates. Non-blocked candidates come first, sorted by pickup-window proximity;
    /// blocked candidates come last so the UI can still show them dimmed with the reason.
    /// </summary>
    public required IReadOnlyList<ConsolidationCandidate> Candidates { get; init; }

    /// <summary>
    /// True when the underlying Alvys sweep hit the <see cref="LtlOptions.MaxLoadsScanned"/>
    /// bound and could not scan every open load; the UI surfaces the honest banner.
    /// </summary>
    public bool ScanTruncated { get; init; }
}

/// <summary>
/// Public projection of a configured consolidation corridor, joined with warehouse details.
/// Returned by <c>GET /api/ltl/consolidation/corridors</c>. Stable enough for automation
/// (E2E tests, UI corridor pickers) to depend on it directly.
/// </summary>
public sealed class CorridorSummary
{
    /// <summary>Stable code, e.g. <c>LAREDO_TO_DALLAS</c>.</summary>
    public required string Code { get; init; }

    /// <summary>Origin warehouse projection.</summary>
    public required WarehouseSummary Origin { get; init; }

    /// <summary>Destination warehouse projection.</summary>
    public required WarehouseSummary Destination { get; init; }

    /// <summary>Days on either side of the seed's pickup used to gate sibling candidates.</summary>
    public required int PickupWindowDays { get; init; }

    /// <summary>Days on either side of the seed's delivery used to gate sibling candidates.</summary>
    public required int DeliveryWindowDays { get; init; }
}

/// <summary>
/// Live-count signal for one corridor. Companion to <see cref="CorridorSummary"/>:
/// <c>/corridors</c> paints the picker with static shape, <c>/corridors/health</c>
/// fills in "how many loads are open right now?" so leadership can see at a glance
/// which corridor has plannable pairs.
/// </summary>
public sealed class CorridorHealth
{
    /// <summary>Corridor code (matches <see cref="CorridorSummary.Code"/>).</summary>
    public required string Code { get; init; }

    /// <summary>
    /// Loads open on this corridor's canonical city pair right now.
    /// <c>null</c> when the Alvys read degraded — the UI shows "unknown" rather than a
    /// misleading zero.
    /// </summary>
    public required int? OpenLoadCount { get; init; }

    /// <summary>True when the underlying Alvys sweep hit its bound; the real count may be higher.</summary>
    public required bool Truncated { get; init; }

    /// <summary>Canonical origin city sampled for this count (first entry in NearbyCities).</summary>
    public required string OriginCity { get; init; }

    /// <summary>Canonical destination city sampled for this count.</summary>
    public required string DestinationCity { get; init; }

    /// <summary>
    /// The Alvys load id of the first open load on this corridor's canonical lane, if any.
    /// Lets the UI auto-seed the candidate queue by DEFAULT (no app-settings / manual seed
    /// required) so the pilot corridor is populated the moment the Consolidate tab opens.
    /// <c>null</c> when the lane has no open loads or the Alvys read degraded — never fabricated.
    /// </summary>
    public string? SeedLoadId { get; init; }

    /// <summary>Human-facing load number for <see cref="SeedLoadId"/> (preferred for display).</summary>
    public string? SeedLoadNumber { get; init; }
}

/// <summary>Public projection of a warehouse. Never carries geolocation — Phase 1 filters on state + NearbyCities.</summary>
public sealed class WarehouseSummary
{
    /// <summary>Stable code, e.g. <c>LAREDO</c>.</summary>
    public required string Code { get; init; }

    /// <summary>Human label ("Laredo yard").</summary>
    public required string Name { get; init; }

    /// <summary>Two-letter ISO state code.</summary>
    public required string State { get; init; }

    /// <summary>Cities considered "near" this warehouse for lane-fit evaluation.</summary>
    public required IReadOnlyList<string> NearbyCities { get; init; }
}
