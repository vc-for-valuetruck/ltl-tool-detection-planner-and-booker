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

    /// <summary>
    /// Notes/documents contain a keyword-detected accessorial event (detention, layover, lumper,
    /// reconsignment) but the load carries no customer accessorial charge at all — a likely missed
    /// accessorial rather than an itemization gap (see <see cref="MissingAccessorialReview"/> for
    /// that case). Only computed on the detail path, where notes/documents are fetched.
    /// </summary>
    PossibleUnbilledAccessorial,

    /// <summary>
    /// The carrier's accessorial total (from the trip's <c>Carrier.Accessorials</c>) exceeds what
    /// the customer was billed for accessorials — the carrier was paid for detention/liftgate/
    /// etc. but that cost was never passed through to the customer. A numeric, higher-confidence
    /// sibling of <see cref="PossibleUnbilledAccessorial"/> (which is keyword-evidence-based).
    /// Available anywhere trip economics are fetched (detail and billing worklist).
    /// </summary>
    CarrierAccessorialMismatch,

    /// <summary>
    /// A posted invoice's total differs from the load's quoted revenue by more than
    /// <see cref="LtlOptions.InvoiceDriftThresholdPercent"/> — a proxy for a reclass/reweigh
    /// adjustment applied after the quote. Informational only (the load is already invoiced by
    /// definition); flags the invoice for a supplemental-billing/credit-memo review.
    /// </summary>
    InvoiceAmountDrift,

    /// <summary>
    /// An unpaid invoice on the load is past its payment terms by at least
    /// <see cref="BillingOptions.DaysPastTermsThreshold"/> days — money owed and overdue. The due
    /// date is the invoice's own <c>DueDate</c> when present, otherwise derived from the customer's
    /// configured net terms (<see cref="BillingOptions.CustomerTermsDays"/>); when neither exists
    /// the invoice's aging is left unevaluated and this badge never fires (terms are never invented).
    /// </summary>
    DaysPastTerms,
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

    /// <summary>
    /// Alvys sales-rep id (<c>AlvysLoad.CustomerRepId</c>). Opaque id only — the Alvys load
    /// projection carries no human-readable rep name field, so the UI must render this as an id,
    /// never invent a name for it. Used to group the margin rollup by rep.
    /// </summary>
    public string? CustomerRepId { get; init; }

    public required string Status { get; init; }
    public AssignmentState Assignment { get; init; }

    public LtlPlace? Origin { get; init; }
    public LtlPlace? Destination { get; init; }

    public DateTimeOffset? ScheduledPickupAt { get; init; }
    public DateTimeOffset? ScheduledDeliveryAt { get; init; }
    public DateTimeOffset? ActualPickupAt { get; init; }
    public DateTimeOffset? ActualDeliveryAt { get; init; }

    /// <summary>
    /// Predicted delivery instant for an in-transit load (Phase 7.3). Derived from PCMiler loaded
    /// miles via Alvys ÷ a configured average line-haul speed, anchored at actual pickup. Null when
    /// the load is not in transit or carries no mileage to estimate from — never guessed. See
    /// <see cref="EtaBasis"/> for the exact derivation shown to the user.
    /// </summary>
    public DateTimeOffset? PredictedDeliveryAt { get; init; }

    /// <summary>True when <see cref="PredictedDeliveryAt"/> is past the scheduled delivery window (+grace).</summary>
    public bool PredictedLate { get; init; }

    /// <summary>Provenance/rationale for the ETA, so the UI never presents it as a routing-API promise.</summary>
    public string? EtaBasis { get; init; }

    /// <summary>
    /// Actual-late delivery signal for an in-transit trip: the delivery stop's window/appointment has
    /// passed with no arrival recorded on the Alvys stop. Null when the delivery is on time, already
    /// arrived, or carries no usable window. Distinct from <see cref="PredictedLate"/> (a
    /// forward-looking ETA estimate) — this is a past-fact derived from live Alvys stop status, never
    /// a projection. See <see cref="LtlLateDelivery"/>.
    /// </summary>
    public LtlLateDelivery? LateDelivery { get; init; }

    /// <summary>
    /// Stuck-at-stop signal for an in-transit trip: a stop the truck arrived at but never recorded a
    /// departure from, dwelling past a configured threshold. Null when no such stop exists. Derived
    /// only from live Alvys stop status and — critically — always carries the honest caveat that this
    /// may just be an unclosed stop, not a stranded truck. See <see cref="LtlStuckStop"/>.
    /// </summary>
    public LtlStuckStop? StuckStop { get; init; }

    public IReadOnlyList<string> Equipment { get; init; } = [];
    public decimal? WeightLbs { get; init; }
    public decimal? Volume { get; init; }

    /// <summary>
    /// Pallet/piece/weight/volume detail married onto this load from a matched inbound EDI tender
    /// (Phase 7.2). The load record itself frequently lacks these dimensions; the EDI tender that
    /// created the freight carries them per stop. Populated only when a tender shares an identifier
    /// (ShipmentId / LoadNumber / order PO number or reference id) with this load — otherwise the
    /// load's pallet/piece data honestly stays <c>null</c> (unknown), never fabricated. The pallet
    /// count in <see cref="LtlEdiEnrichment.PalletEstimate"/> is always an estimate, never a
    /// verified count.
    /// </summary>
    public LtlEdiEnrichment? EdiEnrichment { get; init; }

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

    /// <summary>
    /// Deterministic accessorial-review candidates (Phase 3.5). Only populated on the detail path
    /// where trip stops and notes/documents are fetched; on the list/search path it is
    /// <see cref="AccessorialReviewResult.NotEvaluated"/> (not "no accessorials").
    /// </summary>
    public AccessorialReviewResult AccessorialReview { get; init; } = AccessorialReviewResult.NotEvaluated;
    public IReadOnlyList<LtlExceptionFlag> Exceptions { get; init; } = [];

    /// <summary>
    /// Relative dispatch/billing-attention priority — dollars-at-risk + days-stale-unbilled +
    /// exception weight combined into one sortable number (see <see cref="LtlUrgencyOptions"/> for
    /// the weights). It is a ranking score, not a currency amount or an Alvys field: every load
    /// gets a score (loads with no risk signal at all score exactly 0, meaning "nothing raises
    /// urgency" — never "unknown"). Always computed, so it is safe to sort/filter on directly.
    /// </summary>
    public decimal UrgencyScore { get; init; }

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

/// <summary>
/// Pallet/piece/weight/volume detail lifted from a matched inbound EDI tender (Phase 7.2). Every
/// field carries the <see cref="Source"/> label so the UI always shows where the number came from,
/// and <see cref="PalletEstimate"/> is explicitly an estimate (see <see cref="PalletBasis"/> for
/// the math) — never presented as a verified pallet count. Absent entirely when no tender matched.
/// </summary>
public sealed class LtlEdiEnrichment
{
    /// <summary>Fixed provenance label shown next to every enriched value. Always "EDI tender".</summary>
    public string Source { get; init; } = "EDI tender";

    /// <summary>The matched tender's <c>ShipmentId</c>, for audit/traceability.</summary>
    public string? TenderShipmentId { get; init; }

    /// <summary>
    /// Which identifiers joined the load to the tender (e.g. "load OrderNumber = tender ShipmentId"),
    /// so a dispatcher can see the match was real and not guessed.
    /// </summary>
    public string? MatchedOn { get; init; }

    /// <summary>Total pieces (sum of the tender pickup stop's <c>Orders[].Quantity</c>). Null when none reported.</summary>
    public int? PieceCount { get; init; }

    /// <summary>Tender weight in pounds. Null when the tender carries no weight.</summary>
    public decimal? WeightLbs { get; init; }

    /// <summary>Tender volume in cubic feet. Null when the tender carries no volume.</summary>
    public decimal? Volume { get; init; }

    /// <summary>
    /// Estimated pallet count derived from <see cref="Volume"/>. Always an estimate; null when the
    /// tender reports no volume to estimate from. See <see cref="PalletBasis"/> for the exact math.
    /// </summary>
    public int? PalletEstimate { get; init; }

    /// <summary>Human-readable derivation behind <see cref="PalletEstimate"/> for the "est." tooltip.</summary>
    public string? PalletBasis { get; init; }
}

/// <summary>
/// An actual-late delivery, derived only from live Alvys trip-stop status: the delivery stop's
/// appointment date or window end has passed and the stop carries no recorded arrival. Every field
/// comes straight from the Alvys stop — nothing is projected. Feeds the Exceptions worklist chip and
/// the T8 exception notification (deduped on load + stop + window end).
/// </summary>
public sealed class LtlLateDelivery
{
    /// <summary>Alvys delivery-stop id — the stop identity half of the notification dedupe key.</summary>
    public required string StopId { get; init; }

    public string? DestinationCity { get; init; }
    public string? DestinationState { get; init; }

    /// <summary>The passed window boundary (delivery-stop appointment date or window end).</summary>
    public required DateTimeOffset WindowEnd { get; init; }

    /// <summary>Whether <see cref="WindowEnd"/> came from an appointment or a delivery window.</summary>
    public required string WindowBasis { get; init; }

    /// <summary>Whole/decimal hours the delivery is overdue as of the evaluation instant.</summary>
    public required double HoursOverdue { get; init; }

    /// <summary>Honest, fixed operator-facing wording shown on the Exceptions worklist.</summary>
    public required string Message { get; init; }
}

/// <summary>
/// A stuck-at-stop signal, derived only from live Alvys trip-stop status: the truck recorded an
/// arrival at the stop but no departure, and has now dwelled past a configured threshold. This is a
/// data-quality-sensitive signal — a long dwell very often means the driver simply never closed the
/// stop in Alvys, NOT that the truck is physically stranded. The <see cref="Message"/> therefore
/// always carries the honest caveat "Per Alvys stop status — driver may not have closed the stop".
/// Feeds the Exceptions worklist chip and the T8 exception notification (deduped on load + stop).
/// </summary>
public sealed class LtlStuckStop
{
    /// <summary>Alvys stop id — the stop identity half of the notification dedupe key.</summary>
    public required string StopId { get; init; }

    /// <summary>Stop type as reported by Alvys (e.g. "Pickup"/"Delivery").</summary>
    public string? StopType { get; init; }

    public string? City { get; init; }
    public string? State { get; init; }

    /// <summary>The recorded arrival instant the dwell is measured from.</summary>
    public required DateTimeOffset ArrivedAt { get; init; }

    /// <summary>Whole/decimal hours since arrival with no recorded departure, as of evaluation.</summary>
    public required double HoursSinceArrival { get; init; }

    /// <summary>Honest operator-facing wording — always includes the "may not have closed" caveat.</summary>
    public required string Message { get; init; }
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

    /// <summary>
    /// Non-blocking, overridable cautions surfaced alongside the recommendation (e.g. a co-driver
    /// in a team pairing is inactive/terminated, or the candidate's current trip does not clear
    /// before pickup). Distinct from <see cref="Disqualifiers"/>, which cap the label; warnings
    /// inform the dispatcher but do not by themselves force Not Recommended. Empty when none.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// How this ranking was derived. <c>"AlvysPredictionUnavailable"</c> means Alvys' beta
    /// best-driver prediction was not available and the tool fell back to its own deterministic
    /// factor-based ranking (never silently substituted). Null on direct scorer calls that do not
    /// consult the prediction provider (e.g. single-candidate assignment validation).
    /// </summary>
    public string? PredictionBasis { get; init; }
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

    /// <summary>One-sentence, human-readable rationale for this factor's status.</summary>
    public required string Detail { get; init; }

    /// <summary>
    /// The raw measured input behind this factor (e.g. "8,000 lb / 40,000 lb capacity",
    /// "TX vs TX", "delivers 2026-07-01 08:00"). Null when there was no underlying value to show
    /// (unavailable factors). Never a fabricated value.
    /// </summary>
    public string? RawValue { get; init; }

    /// <summary>
    /// The factor's configured maximum weight, for explainability display ("12 / 15 pts"). This is
    /// informational and, unlike <see cref="MaxPoints"/>, is reported even for an unavailable factor
    /// so the drawer can show "weight 15 — not scored". It does <b>not</b> enter the denominator.
    /// </summary>
    public double Weight { get; init; }

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
    UrgencyScore,
}

/// <summary>Grouping dimension for the margin rollup (<see cref="MarginRollupRow"/>).</summary>
public enum RollupGroupBy
{
    Customer,
    Rep,
    Lane,
}

/// <summary>
/// One grouped row in the margin rollup: aggregates already-normalized <see cref="LtlLoadSummary"/>
/// values by customer, rep, or lane. Every total is null when no load in the group carries the
/// underlying value — an empty group never reports <c>$0</c>. Read-only, entirely Alvys-derived;
/// no external BI/reporting connection.
/// </summary>
public sealed class MarginRollupRow
{
    /// <summary>Stable grouping key (customer id/name, rep id, or lane string).</summary>
    public required string Key { get; init; }

    /// <summary>Display label. See <see cref="LabelIsId"/> before treating this as a human name.</summary>
    public required string Label { get; init; }

    /// <summary>
    /// True when <see cref="Label"/> is only an opaque Alvys id (rep grouping — no human-readable
    /// rep name exists on the Alvys load projection today) rather than a real name.
    /// </summary>
    public bool LabelIsId { get; init; }

    public int LoadCount { get; init; }

    /// <summary>Sum of <see cref="LtlLoadSummary.Revenue"/> across loads with a known revenue. Null if none.</summary>
    public decimal? TotalRevenue { get; init; }

    /// <summary>Sum of <see cref="LtlLoadSummary.CarrierPayable"/> across loads with a known value. Null if none.</summary>
    public decimal? TotalCarrierPayable { get; init; }

    /// <summary>
    /// Sum of <see cref="LtlLoadSummary.GrossMargin"/> across loads where it is known (both revenue
    /// and carrier payable present) — never a partial sum that mixes a missing side in as zero.
    /// </summary>
    public decimal? TotalGrossMargin { get; init; }

    /// <summary>
    /// TotalGrossMargin over the revenue of the same margin-known loads, as a percent — a
    /// revenue-weighted rollup, not a naive average of per-load percentages. Null unless
    /// <see cref="TotalGrossMargin"/> is known and its revenue basis is positive.
    /// </summary>
    public decimal? GrossMarginPercent { get; init; }

    /// <summary>Sum of <see cref="BillingReadinessResult.UnpaidBalance"/> across loads with one. Null if none.</summary>
    public decimal? TotalUnpaidBalance { get; init; }

    /// <summary>Sum of exception counts across loads in this group.</summary>
    public int ExceptionCount { get; init; }

    /// <summary>Count of loads in this group currently badged Ready to Bill.</summary>
    public int ReadyToBillCount { get; init; }
}

/// <summary>Margin rollup response: grouping dimension, rows, and honest sweep-truncation state.</summary>
public sealed class MarginRollupResponse
{
    public required RollupGroupBy GroupBy { get; init; }
    public IReadOnlyList<MarginRollupRow> Rows { get; init; } = [];

    /// <summary>True when the underlying load sweep hit its bound — same meaning as <see cref="LtlSearchResponse.Truncated"/>.</summary>
    public bool Truncated { get; init; }
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
    Handling,
    InsideDelivery,
    WeekendDelivery,
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

/// <summary>
/// Confidence of an <see cref="AccessorialReviewCandidate"/> — deliberately three-valued so the
/// tool never over- or under-claims. <see cref="CannotEvaluate"/> is distinct from
/// <see cref="Unknown"/>: it means the deterministic rule could not run because required config
/// (e.g. a customer's free-time minutes) is missing, and is surfaced as such rather than assumed.
/// </summary>
public enum AccessorialCandidateStatus
{
    /// <summary>The evidence deterministically supports billing this accessorial for review.</summary>
    Likely,

    /// <summary>Evidence hints at the accessorial but is not conclusive (weak keyword, ambiguous).</summary>
    Unknown,

    /// <summary>The rule requires per-customer config that is not set — flagged, never assumed.</summary>
    CannotEvaluate,
}

/// <summary>
/// One deterministically-derived accessorial-review candidate for a load, each citing the exact
/// Alvys record it came from (a stop, a load note, or a document). The analyzer never computes a
/// dollar value — pricing is the accessorial team's job; this only flags that the underlying
/// evidence exists so revenue is not left on the table.
/// </summary>
public sealed class AccessorialReviewCandidate
{
    public required AccessorialSignalType Type { get; init; }

    public required AccessorialCandidateStatus Status { get; init; }

    /// <summary>Human-readable reason, e.g. "Detention — 3h over free time".</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Verbatim / cited evidence, e.g. "stop 2, arrived 10:00, departed 16:00, customer free time = 180m"
    /// or a note excerpt. Never fabricated — always sourced from <see cref="SourceType"/>/<see cref="SourceId"/>.
    /// </summary>
    public required string Evidence { get; init; }

    /// <summary>The Alvys record id this candidate cites (stop id / note id / document id).</summary>
    public string? SourceId { get; init; }

    /// <summary>"Stop", "Note", or "Document".</summary>
    public string? SourceType { get; init; }
}

/// <summary>
/// Deterministic accessorial-review result for a single load. <see cref="Evaluated"/> follows the
/// same honesty rule as <see cref="AccessorialReviewContext"/>: when no stops and no notes/documents
/// were available to inspect it is <c>false</c>, and an empty <see cref="Candidates"/> must never be
/// read as "no accessorials to bill" — it means "not evaluated".
/// </summary>
public sealed class AccessorialReviewResult
{
    /// <summary>Shared not-evaluated singleton (no stops and no notes/documents inspected).</summary>
    public static readonly AccessorialReviewResult NotEvaluated = new();

    public bool Evaluated { get; init; }

    public IReadOnlyList<AccessorialReviewCandidate> Candidates { get; init; } = [];

    /// <summary>True when at least one candidate is a deterministic <see cref="AccessorialCandidateStatus.Likely"/> hit.</summary>
    public bool HasLikelyCandidate =>
        Candidates.Any(c => c.Status == AccessorialCandidateStatus.Likely);
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
