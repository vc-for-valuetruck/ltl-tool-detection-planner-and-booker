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

    /// <summary>
    /// Minimum number of recently-delivered, priced loads on a lane before a revenue-per-mile
    /// range is shown (Phase 7.4 lane rate context). Below this the lane reports an honest
    /// "not enough samples" verdict rather than a misleadingly thin range.
    /// </summary>
    public int LaneRateMinSamples { get; set; } = 3;

    /// <summary>
    /// Grace window (minutes) past a delivery stop's appointment/window end before an in-transit
    /// trip with no recorded arrival is flagged as an actual-late delivery. Absorbs normal check-in
    /// slack so the exception does not flap right at the window boundary. Distinct from
    /// <see cref="EtaOptions.LateGraceMinutes"/>, which governs the forward-looking predicted-late ETA.
    /// </summary>
    public int LateDeliveryGraceMinutes { get; set; } = 30;

    /// <summary>
    /// Dwell threshold (hours) past a stop's recorded arrival, with no recorded departure, before an
    /// in-transit trip is flagged as stuck at that stop. Defaults to 6h. Deliberately conservative
    /// because a long dwell very often just means the driver never closed the stop in Alvys, not that
    /// the truck is physically stranded — the surfaced signal always carries that honest caveat.
    /// </summary>
    public int StuckAtStopThresholdHours { get; set; } = 6;

    /// <summary>Match scoring weights/thresholds.</summary>
    public LtlMatchOptions Match { get; set; } = new();

    /// <summary>Delivery-ETA prediction settings (Phase 7.3). Bound from <c>Ltl:Eta</c>.</summary>
    public EtaOptions Eta { get; set; } = new();

    /// <summary>
    /// Gross margin percent (Revenue − Carrier payable, over Revenue) at/below which a load is
    /// flagged as a margin risk. Only evaluated when both revenue and carrier payable are known —
    /// never inferred from a missing value. A negative margin is always flagged regardless of
    /// this threshold.
    /// </summary>
    public double MarginRiskThresholdPercent { get; set; } = 10.0;

    /// <summary>
    /// Percent difference between a posted invoice's total and the load's quoted revenue
    /// (<c>LtlNormalizationService.ResolveRevenue</c>) at/above which the invoice is flagged as
    /// drifted from the quote — a proxy for an after-the-fact reclass/reweigh adjustment. Only
    /// evaluated against posted invoices with a known total; never inferred from a missing value.
    /// </summary>
    public double InvoiceDriftThresholdPercent { get; set; } = 5.0;

    /// <summary>
    /// Accessorial-signal AI extraction (Phase 6). Disabled by default — set
    /// <see cref="AccessorialAiOptions.Enabled"/> to <c>true</c> and supply credentials
    /// server-side only to activate the Azure OpenAI extractor. The deterministic keyword
    /// extractor always runs regardless of this setting.
    /// </summary>
    public AccessorialAiOptions AccessorialAi { get; set; } = new();

    /// <summary>
    /// Phase 2 optimization engines (trailer fit, capacity/cost solver, agent commands). Every
    /// sub-flag defaults to <c>false</c>, so a fresh clone registers only the <c>Null…</c>
    /// no-op implementations and no half-built engine can affect the demo. Flip a sub-flag on
    /// only once its real engine is wired and validated against Alvys-derived inputs.
    /// </summary>
    public OptimizationOptions Optimization { get; set; } = new();

    /// <summary>
    /// Weights for the dispatch/billing-attention urgency score (<see cref="LtlLoadSummary.UrgencyScore"/>).
    /// Bound from <c>Ltl:Urgency</c>.
    /// </summary>
    public LtlUrgencyOptions Urgency { get; set; } = new();
}

/// <summary>
/// Per-signal weights combined into <see cref="LtlLoadSummary.UrgencyScore"/>: unpaid dollars,
/// days stale since delivery with no invoice, and exception count (blocking exceptions weigh more
/// than non-blocking ones). Defaults are relative, not calibrated dollar-equivalents — tune per
/// environment once real prioritization feedback comes in.
/// </summary>
public sealed class LtlUrgencyOptions
{
    /// <summary>Points added per dollar of unpaid invoice balance.</summary>
    public double DollarWeight { get; set; } = 1.0;

    /// <summary>Points added per day delivered-but-unbilled beyond zero.</summary>
    public double StaleDayWeight { get; set; } = 50.0;

    /// <summary>Points added per billing-blocking exception on the load.</summary>
    public double BlockingExceptionWeight { get; set; } = 500.0;

    /// <summary>Points added per non-blocking (advisory) exception on the load.</summary>
    public double ExceptionWeight { get; set; } = 100.0;
}

/// <summary>
/// Feature toggles for the Phase 2 optimization layer (bound from <c>Ltl:Optimization</c>). Each
/// engine follows the AccessorialAI precedent: a real interface with a <c>Null…</c> fallback that
/// is registered until the matching flag is turned on. Optimization consumes only Alvys-derived
/// operational data passed in by the API — the engines never fetch data themselves.
/// </summary>
public sealed class OptimizationOptions
{
    /// <summary>3D / weight-and-cube trailer-fit engine (Phase 2). Off by default.</summary>
    public TrailerFitOptions TrailerFit { get; set; } = new();

    /// <summary>Capacity/cost balancing solver, e.g. OR-Tools (Phase 2). Off by default.</summary>
    public OptimizationFeatureToggle Solver { get; set; } = new();

    /// <summary>Natural-language / agent command surface over optimization (Phase 2). Off by default.</summary>
    public OptimizationFeatureToggle AgentCommands { get; set; } = new();
}

/// <summary>A single on/off toggle for an optimization engine. Defaults to disabled.</summary>
public class OptimizationFeatureToggle
{
    /// <summary>
    /// When <c>false</c> (default), the engine's <c>Null…</c> no-op implementation is registered
    /// and no optimization computation runs.
    /// </summary>
    public bool Enabled { get; set; } = false;
}

/// <summary>
/// Configuration for the trailer-fit engine (bound from <c>Ltl:Optimization:TrailerFit</c>).
/// Adds the packing sidecar connection details to the base <see cref="OptimizationFeatureToggle.Enabled"/>
/// flag. When <see cref="OptimizationFeatureToggle.Enabled"/> is <c>false</c> (default) the
/// <c>NullTrailerFitService</c> is registered and none of the connection fields are read.
///
/// <para>
/// The standard-trailer fields describe the equipment a consolidation targets (a 53' dry van by
/// default). These are physical equipment specifications — not Alvys operational data — so they may
/// safely have defaults; they match the 45,000 lb constant already used in the SPA. They are only a
/// fallback: when an actual assigned trailer's Alvys capacity is known, the caller passes it instead.
/// </para>
/// </summary>
public sealed class TrailerFitOptions : OptimizationFeatureToggle
{
    public const string SectionName = "Ltl:Optimization:TrailerFit";

    /// <summary>Base URL of the trailer-fit packing sidecar (e.g. <c>http://trailer-fit:8000</c>).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Per-request timeout for a sidecar call. On timeout the service degrades to an
    /// <c>Unknown</c> verdict ("verify at dock") rather than failing the plan request.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Standard equipment code sent to the sidecar for the target trailer (see its GET /equipment-types).</summary>
    public string EquipmentCode { get; set; } = "DV_53";

    /// <summary>Standard 53' dry-van max payload (lbs) used when no assigned-trailer capacity is supplied.</summary>
    public decimal StandardTrailerMaxWeightLbs { get; set; } = 45_000m;

    /// <summary>Standard 53' dry-van pallet positions used when no assigned-trailer capacity is supplied.</summary>
    public int StandardTrailerMaxPallets { get; set; } = 26;

    /// <summary>
    /// Assumed weight per pallet (lbs) used only to derive a pallet count when neither an
    /// explicit pallet count nor a volume is available for a load. Never overrides a real Alvys value.
    /// </summary>
    public decimal AssumedWeightPerPalletLbs { get; set; } = 1_500m;

    /// <summary>Assumed piece height (inches) used when a load carries no volume to derive height from.</summary>
    public decimal AssumedPalletHeightInches { get; set; } = 48m;
}

/// <summary>
/// Configuration for the optional AI-powered accessorial signal extractor (Phase 6).
/// Disabled by default. When enabled, Azure OpenAI credentials must be supplied server-side
/// only — never in SPA, source, tests, or committed environment files.
/// </summary>
public sealed class AccessorialAiOptions
{
    /// <summary>
    /// When <c>false</c> (default), the <c>NullAccessorialSignalExtractor</c> is registered and
    /// no LLM call is ever made. Flip to <c>true</c> only when credentials are also configured.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Azure OpenAI endpoint URL (placeholder — supply via environment/Key Vault only).</summary>
    public string? Endpoint { get; set; }

    /// <summary>Azure OpenAI deployment/model name (placeholder — supply via environment/Key Vault only).</summary>
    public string? DeploymentName { get; set; }

    /// <summary>
    /// Azure OpenAI API key (server-side only — never in SPA, source, tests, docs, or
    /// committed env files). Supply via environment variable or Azure Key Vault reference.
    /// </summary>
    public string? ApiKey { get; set; }
}
/// <summary>
/// Settings for the delivery-ETA predictor (Phase 7.3). The ETA is a simple, honest estimate:
/// remaining loaded miles (PCMiler-sourced via Alvys) divided by an assumed average line-haul
/// speed, added to the actual pickup time. It is never presented as a routing-API ETA and always
/// carries a "derived from PCMiler miles via Alvys" provenance label.
/// </summary>
public sealed class EtaOptions
{
    /// <summary>
    /// Assumed average line-haul speed (mph) including a simple rest/duty allowance. Deliberately
    /// conservative (below highway cruise) so the ETA does not run early and mask a predicted-late
    /// arrival. Configurable per environment.
    /// </summary>
    public decimal AverageSpeedMph { get; set; } = 47m;

    /// <summary>
    /// Grace window (minutes) past the scheduled delivery window before a predicted arrival is
    /// flagged "predicted late" — absorbs normal appointment slack so the exception isn't noisy.
    /// </summary>
    public int LateGraceMinutes { get; set; } = 30;
}

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

    /// <summary>
    /// Weight of the window-feasibility factor (can the candidate's current Alvys trip commitment
    /// clear before this load's pickup?). Only scored when the active-trip search was actually
    /// issued for a known pickup instant — otherwise not-scored and excluded from the denominator,
    /// never an implicit penalty.
    /// </summary>
    public double WindowFeasibilityWeight { get; set; } = 15;

    /// <summary>
    /// Weight of the dispatch-preference affinity factor (the candidate matches a dispatcher-curated
    /// driver/truck/trailer pairing). A positive-only signal: absence of a preference is not-scored,
    /// never a penalty.
    /// </summary>
    public double DispatchPreferenceWeight { get; set; } = 10;

    /// <summary>
    /// Alvys trip statuses (case-insensitive) treated as an active/committed trip when assessing
    /// window feasibility — a candidate on such a trip is not free until it clears. Observed Alvys
    /// status vocabulary; tune per environment rather than inventing states in code.
    /// </summary>
    public List<string> ActiveTripStatuses { get; set; } =
        ["Dispatched", "In Transit", "InTransit", "Covered", "Assigned", "At Shipper", "At Consignee"];

    /// <summary>
    /// Upper bound on how many distinct active trips have their stop detail fetched
    /// (<c>ListTripStopsAsync</c>) when assessing window feasibility for one match request. Bounds
    /// the extra upstream calls; candidates on trips past the cap keep an unavailable feasibility
    /// factor rather than a guessed one.
    /// </summary>
    public int MaxWindowFeasibilityTripFetches { get; set; } = 25;

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
