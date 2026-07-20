using System.Text.Json.Serialization;

namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// Normalized LTL decision-support read models.
///
/// These projections sit on top of the raw Alvys read models (<c>AlvysLoad</c>, etc.) and
/// add the operational/revenue-protection signals the dispatcher SPA needs: explicit
/// missing-data flags, billing readiness, match labels and exception reasons. They never
/// silently default a missing value — an absent rate/weight/POD/customer is surfaced as a
/// <see cref="MissingDataFlag"/> (and, where it blocks billing, as a billing risk) rather
/// than being coerced to zero.
///
/// This whole layer is read-only: nothing here is written back to Alvys (see
/// <c>LtlController</c> for the audited, internal-only assignment boundary).
/// </summary>
public static class LtlReadModelDocs;

/// <summary>
/// A field the LTL layer expected to find on a load but which Alvys did not supply. Surfaced
/// to the UI so dispatchers see "missing rate" rather than a misleading <c>$0</c>.
/// </summary>
public enum MissingDataFlag
{
    Customer,
    Rate,
    Pod,
    Weight,
    AccessorialReview,
    Mileage,
    Origin,
    Destination,
    PickupDate,
    DeliveryDate,
    Equipment,
    Commodity,
    InvoiceStatus,

    /// <summary>
    /// Per-item freight dimensions (length/width/height, freight class, stackability). The Alvys
    /// load projection carries only aggregate weight/volume/pallets — never a true LxWxH per item —
    /// so a 3D trailer-fit verdict cannot be computed today. Always surfaced so the UI shows
    /// "verify at dock" rather than implying a fit was checked.
    /// </summary>
    Dimensions,
}

/// <summary>Explainable match quality label for a driver/truck against a load.</summary>
public enum MatchLabel
{
    Excellent,
    Good,
    Possible,
    Risky,
    NotRecommended,
}

/// <summary>
/// Billing-readiness badge for a load. Mutually-informative (a load can carry several),
/// covering the revenue-protection states the worklist surfaces.
/// </summary>
public enum BillingBadge
{
    ReadyToBill,
    MissingRate,
    MissingPod,
    MissingAccessorialReview,
    MissingWeight,
    CustomerReviewNeeded,
    ExceptionBlockingBilling,
    AlreadyInvoiced,
}

/// <summary>
/// Where a load currently sits in the dispatcher workflow (Search → Match → Assign → Bill).
/// Derived purely from normalized signals; it never reflects an Alvys writeback. Search is the
/// universal entry point (every load is searchable), so a load's <i>current</i> stage is one of
/// the forward stages below.
/// </summary>
public enum WorkflowStage
{
    /// <summary>Unassigned/open — needs capacity. The dispatcher should review matches next.</summary>
    Match,
    /// <summary>Capacity committed and in motion (covered/dispatched/in-transit), pre-delivery.</summary>
    Assign,
    /// <summary>Delivered but not yet invoiced — needs billing attention.</summary>
    Bill,
    /// <summary>Already invoiced/closed financially — terminal.</summary>
    Billed,
}

/// <summary>
/// The workflow position of a load with the recommended next action and the evidence backing the
/// determination. Computed from the normalized summary (assignment, status, billing readiness,
/// exceptions, missing data, visibility) — a pure decision-support projection with no Alvys
/// writeback.
/// </summary>
public sealed class WorkflowState
{
    /// <summary>Neutral default used before the workflow has been evaluated.</summary>
    public static readonly WorkflowState Unknown = new()
    {
        Stage = WorkflowStage.Match,
        StageLabel = "Match",
        StepIndex = 2,
        RecommendedAction = "Review the load.",
    };

    public required WorkflowStage Stage { get; init; }

    /// <summary>Display label for the stage, e.g. "Match".</summary>
    public required string StageLabel { get; init; }

    /// <summary>
    /// 1-based position in the four-step Search → Match → Assign → Bill model, for a stepper UI:
    /// Search=1, Match=2, Assign=3, Bill/Billed=4.
    /// </summary>
    public required int StepIndex { get; init; }

    /// <summary>The single best next action a dispatcher should take on this load.</summary>
    public required string RecommendedAction { get; init; }

    /// <summary>
    /// Short, human-readable signals that back the stage/action determination (e.g.
    /// "Status: Delivered", "Rate on file", "Unassigned"). Never fabricated — only present signals.
    /// </summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>True when something prevents the load from advancing to the next stage.</summary>
    public bool IsBlocked { get; init; }

    /// <summary>Why progression is blocked (missing data, billing-blocking exceptions, failed tracking).</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];
}

/// <summary>Coarse assignment state derived from load status (no Alvys writeback).</summary>
public enum AssignmentState
{
    /// <summary>Not yet covered/dispatched — an open LTL opportunity.</summary>
    Unassigned,
    /// <summary>Covered/dispatched/in-transit/delivered — capacity is committed.</summary>
    Assigned,
    /// <summary>Status does not map cleanly to assigned/unassigned.</summary>
    Unknown,
}

/// <summary>A city/state/zip location projected from an Alvys stop.</summary>
public sealed class LtlPlace
{
    public string? Name { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? Zip { get; init; }

    [JsonIgnore]
    public bool HasCityState => !string.IsNullOrWhiteSpace(City) && !string.IsNullOrWhiteSpace(State);

    /// <summary>"City, ST" when both are present, otherwise whichever is known, else null.</summary>
    public string? Label =>
        HasCityState ? $"{City}, {State}"
        : !string.IsNullOrWhiteSpace(City) ? City
        : !string.IsNullOrWhiteSpace(State) ? State
        : null;
}

/// <summary>
/// Normalized LTL load summary used by the search grid and worklists. Money/weight/mileage
/// are nullable on purpose: <c>null</c> means "Alvys did not supply it" (and a corresponding
/// <see cref="MissingDataFlag"/> is set), which the UI renders distinctly from a real zero.
/// </summary>
public sealed class LtlLoadSummary
{
    public required string Id { get; init; }
    public string? LoadNumber { get; init; }
    public string? OrderNumber { get; init; }
    public string? PoNumber { get; init; }
    public string? CustomerId { get; init; }
    public string? CustomerName { get; init; }

    public required string Status { get; init; }
    public AssignmentState Assignment { get; init; }

    public LtlPlace? Origin { get; init; }
    public LtlPlace? Destination { get; init; }

    public DateTimeOffset? ScheduledPickupAt { get; init; }
    public DateTimeOffset? ScheduledDeliveryAt { get; init; }
    public DateTimeOffset? ActualPickupAt { get; init; }
    public DateTimeOffset? ActualDeliveryAt { get; init; }

    public IReadOnlyList<string> Equipment { get; init; } = [];
    public decimal? WeightLbs { get; init; }
    public decimal? Volume { get; init; }

    /// <summary>Customer-facing revenue (rate). Null when no rate is on the load.</summary>
    public decimal? Revenue { get; init; }
    public decimal? Mileage { get; init; }

    /// <summary>
    /// Total amount payable to the carrier for this load's trip (Linehaul + Accessorials, from
    /// Alvys trip data). Null when no trip was fetched (list/search path) or the trip carries no
    /// carrier payable — never inferred. Detail-path and Billing Worklist only; see
    /// <see cref="MissingDataFlag"/> precedent for other detail-path-only fields.
    /// </summary>
    public decimal? CarrierPayable { get; init; }

    /// <summary>
    /// Driver-facing trip rate (<c>AlvysTrip.TripValue.Amount</c>). This is the number Reuben
    /// described at 33:06 in the 2026-07-17 sync as the "driver's rate" for RPM math. Distinct
    /// from <see cref="Revenue"/> (customer-facing billing rate) and
    /// <see cref="CarrierPayable"/> (Linehaul + Accessorials on the carrier row — which for
    /// company drivers equals TripValue but semantically means "payable", not "trip rate").
    /// Null when no trip was fetched or the trip carries no rate. Never inferred.
    /// </summary>
    public decimal? DriverTripRate { get; init; }

    /// <summary>
    /// Driver-facing loaded miles (<c>AlvysTrip.LoadedMileage.Distance.Value</c>). This is the
    /// number the Alvys dispatch language panel labels "loaded miles" — the field Poornima
    /// walks Holly through zeroing on consolidation children (Reuben 2026-07-17 sync, 15:55).
    /// Distinct from <see cref="Mileage"/>, which maps to customer-facing billing miles.
    /// Null when no trip was fetched or the trip carries no loaded mileage. Never inferred.
    /// </summary>
    public decimal? LoadedMiles { get; init; }

    /// <summary>
    /// Revenue − <see cref="CarrierPayable"/>. Null unless both are present — a missing carrier
    /// payable is never treated as zero cost.
    /// </summary>
    public decimal? GrossMargin { get; init; }

    /// <summary>GrossMargin as a percent of Revenue. Null unless both Revenue (&gt; 0) and GrossMargin are known.</summary>
    public decimal? GrossMarginPercent { get; init; }

    /// <summary>
    /// Revenue per mile when both revenue and mileage are present; a cheap "complexity/quality"
    /// proxy for the High-Revenue/Low-Complexity saved view. Null when either input is missing.
    /// </summary>
    public decimal? RevenuePerMile { get; init; }

    /// <summary>
    /// True when this load is classified as LTL/partial/volume freight. Null when the load
    /// carries no classification signal (load type / equipment) at all — surfaced rather than
    /// guessed.
    /// </summary>
    public bool? IsLtl { get; init; }
    public string? LtlClassification { get; init; }

    public IReadOnlyList<MissingDataFlag> MissingData { get; init; } = [];
    public BillingReadinessResult Billing { get; init; } = new();
    public IReadOnlyList<LtlExceptionFlag> Exceptions { get; init; } = [];

    /// <summary>
    /// Where this load sits in the Search → Match → Assign → Bill workflow, with the recommended
    /// next action and backing evidence. Derived from the signals above; no Alvys writeback.
    /// </summary>
    public WorkflowState Workflow { get; init; } = WorkflowState.Unknown;

    /// <summary>
    /// Inbound/outbound visibility-history context for the load. Only populated on the detail path
    /// (a per-load fetch); on bulk lists it stays <see cref="VisibilityContext.NotEvaluated"/> so
    /// the UI never mistakes "not fetched" for "no problems".
    /// </summary>
    public VisibilityContext Visibility { get; init; } = VisibilityContext.NotEvaluated;

    [JsonIgnore]
    public bool HasExceptions => Exceptions.Count > 0;
}

/// <summary>An operational exception on a load (revenue-protection / data-quality signal).</summary>
public sealed class LtlExceptionFlag
{
    public required string Code { get; init; }
    public required string Message { get; init; }

    /// <summary>True when this exception blocks clean billing.</summary>
    public bool BlocksBilling { get; init; }
}

/// <summary>
/// Visibility-history context folded onto a load. <see cref="Evaluated"/> records whether the
/// inbound/outbound history was actually fetched — when false, an empty <see cref="Events"/> means
/// "not looked at", never "no events". <see cref="Events"/> carries the noteworthy
/// (failure/appointment/arrival/departure/delivery) events for the detail timeline.
/// </summary>
public sealed class VisibilityContext
{
    /// <summary>Shared singleton for the bulk/list path where visibility was not fetched.</summary>
    public static readonly VisibilityContext NotEvaluated = new();

    public bool Evaluated { get; init; }
    public IReadOnlyList<VisibilityEventView> Events { get; init; } = [];

    [JsonIgnore]
    public bool HasFailures => Events.Any(e => e.IsFailure);
}

/// <summary>A single visibility-history event projected for the detail timeline.</summary>
public sealed class VisibilityEventView
{
    /// <summary>"Inbound" or "Outbound" — which history feed shared the event.</summary>
    public required string Direction { get; init; }
    public string? EventType { get; init; }
    public string? Status { get; init; }
    public DateTimeOffset? SharedAt { get; init; }
    public string? Destination { get; init; }
    public string? Reason { get; init; }
    public string? Error { get; init; }

    /// <summary>True when the event status is Failed/Error or carries non-empty error text.</summary>
    public bool IsFailure { get; init; }
}

/// <summary>
/// Accounts-receivable aging bucket for an unpaid invoice, mirroring the standard
/// Current/30/60/90+ convention. Computed from invoice <c>DueDate</c> vs now — never asserted
/// when no unpaid invoice was found.
/// </summary>
public enum InvoiceAgingBucket
{
    Current,
    Days1To30,
    Days31To60,
    Days61To90,
    Over90Days,
}

/// <summary>Result of inspecting a load for billing readiness (no silent defaulting).</summary>
public sealed class BillingReadinessResult
{
    public IReadOnlyList<BillingBadge> Badges { get; init; } = [];
    public IReadOnlyList<MissingDataFlag> MissingFields { get; init; } = [];
    public IReadOnlyList<string> Risks { get; init; } = [];

    /// <summary>True only when nothing blocks billing and the load is not already invoiced.</summary>
    public bool IsReadyToBill { get; init; }

    /// <summary>True when the load has already been invoiced (billing is done, not pending).</summary>
    public bool IsAlreadyInvoiced { get; init; }

    /// <summary>
    /// Whether POD presence could actually be evaluated. POD lives on the separate documents
    /// listing; when that was not supplied, POD is reported as not-evaluated rather than missing.
    /// </summary>
    public bool PodEvaluated { get; init; }

    /// <summary>
    /// Total unpaid balance across the load's invoices (sum of positive <c>RemainingBalance</c>).
    /// Null when no invoices were supplied or none carry an unpaid balance.
    /// </summary>
    public decimal? UnpaidBalance { get; init; }

    /// <summary>
    /// Aging bucket for the oldest unpaid invoice on this load, by due date. Null when no
    /// invoices were supplied or none are unpaid — never defaulted to "Current".
    /// </summary>
    public InvoiceAgingBucket? AgingBucket { get; init; }

    /// <summary>Days past due for the oldest unpaid invoice. Null on the same terms as <see cref="AgingBucket"/>.</summary>
    public int? AgingDays { get; init; }
}

/// <summary>
/// One scored driver/truck candidate for a load, with a fully explainable breakdown. The
/// <see cref="Score"/> is 0–100 over the <i>available</i> factors only, so unavailable data
/// (e.g. HOS) neither inflates nor unfairly deflates the result — it is reported as a
/// not-scored factor instead.
/// </summary>
public sealed class MatchResult
{
    public string? DriverId { get; init; }
    public string? DriverName { get; init; }
    public string? TruckId { get; init; }
    public string? TruckNumber { get; init; }
    public string? TrailerId { get; init; }
    public string? TrailerNumber { get; init; }

    public required MatchLabel Label { get; init; }

    /// <summary>Display label, e.g. "Excellent Match".</summary>
    public required string LabelText { get; init; }

    public int Score { get; init; }

    /// <summary>One-line human summary of why this label was assigned.</summary>
    public required string Summary { get; init; }

    public IReadOnlyList<MatchFactor> Factors { get; init; } = [];

    /// <summary>
    /// Hard, explainable reasons the candidate was capped/disqualified (expired credentials,
    /// over capacity, terminated driver). Empty when no hard rule fired.
    /// </summary>
    public IReadOnlyList<string> Disqualifiers { get; init; } = [];
}

/// <summary>Status of a single match factor's contribution.</summary>
public enum MatchFactorStatus
{
    Strong,
    Neutral,
    Weak,
    /// <summary>The underlying data was not available, so the factor was not scored.</summary>
    Unavailable,
}

/// <summary>A single explainable component of a <see cref="MatchResult"/>.</summary>
public sealed class MatchFactor
{
    public required string Name { get; init; }
    public required MatchFactorStatus Status { get; init; }
    public required string Detail { get; init; }

    /// <summary>Points earned by this factor (0 when unavailable).</summary>
    public double Points { get; init; }

    /// <summary>
    /// Points this factor could contribute. 0 when the factor is unavailable, so it is
    /// excluded from the score denominator rather than penalizing the candidate.
    /// </summary>
    public double MaxPoints { get; init; }
}

/// <summary>Filter/sort/paging inputs for the normalized LTL search (bound from query string).</summary>
public sealed class LtlSearchQuery
{
    public string? Keyword { get; set; }
    public string? Customer { get; set; }
    public string? OriginState { get; set; }
    public string? OriginCity { get; set; }
    public string? DestinationState { get; set; }
    public string? DestinationCity { get; set; }
    public string? EquipmentType { get; set; }
    public List<string>? Status { get; set; }
    public DateTimeOffset? PickupFrom { get; set; }
    public DateTimeOffset? PickupTo { get; set; }
    public DateTimeOffset? DeliveryFrom { get; set; }
    public DateTimeOffset? DeliveryTo { get; set; }

    /// <summary>Filter by assignment state (unassigned/assigned). Null = no filter.</summary>
    public AssignmentState? Assignment { get; set; }

    /// <summary>When true, only loads classified as LTL/partial/volume are returned.</summary>
    public bool LtlOnly { get; set; }
    public bool ReadyToBill { get; set; }
    public bool MissingBillingData { get; set; }
    public bool ExceptionsOnly { get; set; }

    /// <summary>Filter to loads carrying a specific billing-readiness badge. Null = no filter.</summary>
    public BillingBadge? BillingBadge { get; set; }

    /// <summary>Filter to loads at a specific workflow stage (derived, enforced in-memory). Null = no filter.</summary>
    public WorkflowStage? Stage { get; set; }

    /// <summary>When true, only loads whose workflow is currently blocked are returned.</summary>
    public bool BlockedOnly { get; set; }

    public LtlSortField Sort { get; set; } = LtlSortField.PickupDate;
    public bool SortDescending { get; set; }

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;

    /// <summary>Hard upper bound on page size to keep an Alvys sweep bounded.</summary>
    public const int MaxPageSize = 100;
}

/// <summary>Sortable concepts exposed by the LTL search grid.</summary>
public enum LtlSortField
{
    PickupDate,
    DeliveryDate,
    Revenue,
    RevenuePerMile,
    Distance,
    Weight,
    Customer,
    Status,
    BillingReadiness,
}

/// <summary>
/// Type of an accessorial-signal evidence item extracted from Alvys notes/documents.
/// Deterministic keyword classification; humans price — the analyzer never asserts a dollar amount.
/// </summary>
public enum AccessorialSignalType
{
    Detention,
    Layover,
    Lumper,
    Reconsignment,
    Other,
}

/// <summary>
/// A single accessorial-signal evidence item extracted from an Alvys note or document name.
/// The <see cref="EvidenceQuote"/> is a verbatim excerpt from the source; it never fabricates.
/// Confidence is 1.0 for deterministic keyword matches, &lt;1.0 for AI-derived signals.
/// </summary>
public sealed class AccessorialSignal
{
    public required AccessorialSignalType Type { get; init; }

    /// <summary>Verbatim excerpt from the note/document text that triggered this signal.</summary>
    public required string EvidenceQuote { get; init; }

    /// <summary>The Alvys note or document id that is the source of this signal.</summary>
    public required string SourceId { get; init; }

    /// <summary>"Note" or "Document".</summary>
    public required string SourceType { get; init; }

    /// <summary>0.0–1.0. Deterministic keyword matches are always 1.0.</summary>
    public double Confidence { get; init; } = 1.0;
}

/// <summary>
/// Accessorial-signal review context for a load. <see cref="Evaluated"/> records whether
/// notes/documents were available and inspected — when false an empty <see cref="Signals"/>
/// means "not looked at", never "no signals". Mirrors the <see cref="VisibilityContext"/>
/// not-evaluated / evaluated distinction.
/// </summary>
public sealed class AccessorialReviewContext
{
    /// <summary>
    /// Shared singleton for the case where no notes/documents were supplied for analysis
    /// (bulk/list path, or detail load with neither notes nor documents). An empty
    /// <see cref="Signals"/> on the singleton must never be read as "no accessorials needed" —
    /// it means "not evaluated".
    /// </summary>
    public static readonly AccessorialReviewContext NotEvaluated = new();

    public bool Evaluated { get; init; }
    public IReadOnlyList<AccessorialSignal> Signals { get; init; } = [];
}

/// <summary>Paged normalized LTL search response.</summary>
public sealed class LtlSearchResponse
{
    public required int Page { get; init; }
    public required int PageSize { get; init; }

    /// <summary>Total matching the post-Alvys filters in the swept window.</summary>
    public required int Total { get; init; }
    public IReadOnlyList<LtlLoadSummary> Items { get; init; } = [];

    /// <summary>
    /// True when the underlying Alvys sweep hit its bound and more loads may exist upstream
    /// than were considered — surfaced so the UI can show "showing first N" honestly.
    /// </summary>
    public bool Truncated { get; init; }
}
