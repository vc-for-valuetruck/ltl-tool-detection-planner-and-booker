namespace LtlTool.Api.Features.Ltl.Consolidation;

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
    public CustomerConsolidationTier CustomerTier { get; init; }

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

    /// <summary>Sum of Parent.Revenue + all sibling Revenue values (null-safe).</summary>
    public decimal? CombinedRevenue { get; init; }

    /// <summary>Parent's linehaul mileage — the miles the driver actually gets paid on.</summary>
    public decimal? LinehaulMiles { get; init; }

    /// <summary>Combined revenue divided by linehaul miles. Null unless both are known.</summary>
    public decimal? CombinedRevenuePerMile { get; init; }

    /// <summary>The click card the dispatcher pastes into Alvys.</summary>
    public required ConsolidationClickCard ClickCard { get; init; }

    /// <summary>
    /// Blockers that make this plan illegal even as a preview: the parent isn't a load, a
    /// sibling isn't on the corridor, a sibling belongs to a Never-consolidate customer.
    /// When non-empty, the SPA must not offer the copy-card action.
    /// </summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];
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
