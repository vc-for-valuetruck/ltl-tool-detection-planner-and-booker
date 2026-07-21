/**
 * TypeScript projections of the API's normalized LTL read models
 * (see src/LtlTool.Api/Features/Ltl/LtlReadModels.cs). Enums are modeled as string
 * unions because the API serializes enums by name (JsonStringEnumConverter); money/
 * weight/mileage are nullable on purpose — `null` means "Alvys did not supply it"
 * and is rendered distinctly from a real zero.
 */

export type MissingDataFlag =
  | 'Customer'
  | 'Rate'
  | 'Pod'
  | 'Weight'
  | 'AccessorialReview'
  | 'Mileage'
  | 'Origin'
  | 'Destination'
  | 'PickupDate'
  | 'DeliveryDate'
  | 'Equipment'
  | 'Commodity'
  | 'InvoiceStatus'
  | 'Dimensions';

export type MatchLabel = 'Excellent' | 'Good' | 'Possible' | 'Risky' | 'NotRecommended';

export type BillingBadge =
  | 'ReadyToBill'
  | 'MissingRate'
  | 'MissingPod'
  | 'MissingAccessorialReview'
  | 'MissingWeight'
  | 'CustomerReviewNeeded'
  | 'ExceptionBlockingBilling'
  | 'AlreadyInvoiced';

export type AssignmentState = 'Unassigned' | 'Assigned' | 'Unknown';

export type WorkflowStage = 'Match' | 'Assign' | 'Bill' | 'Billed';

export interface WorkflowState {
  stage: WorkflowStage;
  stageLabel: string;
  /** 1-based position in the Search→Match→Assign→Bill stepper (Search=1 … Bill/Billed=4). */
  stepIndex: number;
  recommendedAction: string;
  evidence: string[];
  isBlocked: boolean;
  blockers: string[];
}

export type MatchFactorStatus = 'Strong' | 'Neutral' | 'Weak' | 'Unavailable';

export type LtlSortField =
  | 'PickupDate'
  | 'DeliveryDate'
  | 'Revenue'
  | 'RevenuePerMile'
  | 'Distance'
  | 'Weight'
  | 'Customer'
  | 'Status'
  | 'BillingReadiness';

export interface LtlPlace {
  name: string | null;
  city: string | null;
  state: string | null;
  zip: string | null;
  label: string | null;
}

export interface LtlExceptionFlag {
  code: string;
  message: string;
  blocksBilling: boolean;
}

/**
 * Actual-late delivery signal, derived only from live Alvys trip-stop status: the delivery stop's
 * appointment/window end has passed with no arrival recorded. Null unless the load is an actual-late
 * delivery. Distinct from `predictedLate` (a forward-looking ETA estimate) — this is a past fact.
 */
export interface LtlLateDelivery {
  stopId: string;
  destinationCity: string | null;
  destinationState: string | null;
  windowEnd: string;
  windowBasis: string;
  hoursOverdue: number;
  message: string;
}

/** Accounts-receivable aging bucket for an unpaid invoice (Current/30/60/90+). */
export type InvoiceAgingBucket = 'Current' | 'Days1To30' | 'Days31To60' | 'Days61To90' | 'Over90Days';

export interface BillingReadinessResult {
  badges: BillingBadge[];
  missingFields: MissingDataFlag[];
  risks: string[];
  isReadyToBill: boolean;
  isAlreadyInvoiced: boolean;
  podEvaluated: boolean;
  /** Total unpaid balance across the load's invoices. Null when none are unpaid. */
  unpaidBalance: number | null;
  /** Aging bucket for the oldest unpaid invoice, by due date. Null when none are unpaid. */
  agingBucket: InvoiceAgingBucket | null;
  /** Days past due for the oldest unpaid invoice. Null on the same terms as agingBucket. */
  agingDays: number | null;
}

export interface VisibilityEventView {
  direction: string;
  eventType: string | null;
  status: string | null;
  sharedAt: string | null;
  destination: string | null;
  reason: string | null;
  error: string | null;
  isFailure: boolean;
}

export interface VisibilityContext {
  evaluated: boolean;
  events: VisibilityEventView[];
}

/** Type of an accessorial-signal evidence item extracted from Alvys notes/documents. */
export type AccessorialSignalType = 'Detention' | 'Layover' | 'Lumper' | 'Reconsignment' | 'Other';

/**
 * A single accessorial-signal evidence item extracted from an Alvys note or document name.
 * `evidenceQuote` is a verbatim excerpt from the source — never fabricated.
 * Confidence is 1.0 for deterministic keyword matches, <1.0 for AI-derived signals.
 */
export interface AccessorialSignal {
  type: AccessorialSignalType;
  /** Verbatim excerpt from the note/document text that triggered this signal. */
  evidenceQuote: string;
  /** The Alvys note or document id that is the source of this signal. */
  sourceId: string;
  /** "Note" or "Document". */
  sourceType: string;
  /** 0.0–1.0. Deterministic keyword matches are always 1.0. */
  confidence: number;
}

/**
 * Accessorial-signal review context for a load. `evaluated=false` means no notes/documents
 * were available (not evaluated ≠ clean — never assume clean when not evaluated).
 */
export interface AccessorialReviewContext {
  evaluated: boolean;
  signals: AccessorialSignal[];
}

/**
 * EDI-tender pallet/piece/weight/volume detail lifted onto a load (Phase 7.2). Every field carries
 * the `source` label ("EDI tender") and `palletEstimate` is explicitly an estimate — see
 * `palletBasis` for the math shown in the tooltip. Never a verified pallet count.
 */
export interface LtlEdiEnrichment {
  source: string;
  tenderShipmentId: string | null;
  matchedOn: string | null;
  pieceCount: number | null;
  weightLbs: number | null;
  volume: number | null;
  palletEstimate: number | null;
  palletBasis: string | null;
}

export interface LtlLoadSummary {
  id: string;
  loadNumber: string | null;
  orderNumber: string | null;
  poNumber: string | null;
  customerId: string | null;
  customerName: string | null;
  status: string;
  assignment: AssignmentState;
  origin: LtlPlace | null;
  destination: LtlPlace | null;
  scheduledPickupAt: string | null;
  scheduledDeliveryAt: string | null;
  actualPickupAt: string | null;
  actualDeliveryAt: string | null;
  /**
   * Predicted delivery instant for an in-transit load (Phase 7.3). Derived from PCMiler loaded
   * miles via Alvys ÷ a configured average line-haul speed, anchored at actual pickup. Null when
   * the load is not in transit or carries no mileage to estimate from — never guessed. See
   * etaBasis for the exact derivation to show the user.
   */
  predictedDeliveryAt: string | null;
  /** True when predictedDeliveryAt is past the scheduled delivery window (+grace). */
  predictedLate: boolean;
  /** Provenance for the ETA, so the UI never presents it as a routing-API promise. */
  etaBasis: string | null;
  /**
   * Actual-late delivery signal for this load (delivery-stop window passed with no arrival
   * recorded on Alvys). Null unless the load is an actual-late delivery — distinct from
   * predictedLate, which is a forward-looking ETA estimate.
   */
  lateDelivery: LtlLateDelivery | null;
  equipment: string[];
  weightLbs: number | null;
  volume: number | null;
  /**
   * Pallet/piece/weight/volume detail married onto the load from a matched inbound EDI tender
   * (Phase 7.2). Null when no tender shared an identifier — the load's pallet data then stays
   * honestly unknown rather than fabricated.
   */
  ediEnrichment: LtlEdiEnrichment | null;
  revenue: number | null;
  mileage: number | null;
  revenuePerMile: number | null;
  /**
   * Carrier's total payable for this load's trip, fetched from Alvys trip data. Null on the
   * search-grid list path (detail/Billing Worklist only) or when no trip/carrier cost is known —
   * never inferred as zero cost.
   */
  carrierPayable: number | null;
  /**
   * Driver-facing trip rate (Trip.TripValue.Amount). Null on the search-grid list path or
   * when no trip/rate is known. Distinct from `revenue` (customer-billing rate) and
   * `carrierPayable` (Linehaul + Accessorials on the carrier row) — this is the number the
   * Consolidation Planner divides by `loadedMiles` to get driver RPM.
   */
  driverTripRate: number | null;
  /**
   * Driver-facing loaded miles (Trip.LoadedMileage.Distance.Value). Null on the search-grid
   * list path. Distinct from `mileage` (customer-billing mileage). This is the field Phase 5
   * zeroes on consolidation children in Alvys.
   */
  loadedMiles: number | null;
  /** revenue - carrierPayable. Null unless both are known. */
  grossMargin: number | null;
  /** grossMargin as a percent of revenue. Null unless both are known. */
  grossMarginPercent: number | null;
  isLtl: boolean | null;
  ltlClassification: string | null;
  missingData: MissingDataFlag[];
  billing: BillingReadinessResult;
  exceptions: LtlExceptionFlag[];
  hasExceptions: boolean;
  visibility: VisibilityContext;
  workflow: WorkflowState;
}

export interface LtlSearchResponse {
  page: number;
  pageSize: number;
  total: number;
  items: LtlLoadSummary[];
  truncated: boolean;
}

export interface MatchFactor {
  name: string;
  status: MatchFactorStatus;
  detail: string;
  points: number;
  maxPoints: number;
}

export interface MatchResult {
  driverId: string | null;
  driverName: string | null;
  truckId: string | null;
  truckNumber: string | null;
  trailerId: string | null;
  trailerNumber: string | null;
  label: MatchLabel;
  labelText: string;
  score: number;
  summary: string;
  factors: MatchFactor[];
  disqualifiers: string[];
}

export type AssignmentIssueSeverity = 'Block' | 'Warn';

export interface AssignmentIssue {
  code: string;
  message: string;
  severity: AssignmentIssueSeverity;
}

export interface AssignmentValidationResult {
  issues: AssignmentIssue[];
  blockers: AssignmentIssue[];
  warnings: AssignmentIssue[];
  hasBlockers: boolean;
}

/** Body for recording an internal (non-Alvys) assignment decision. */
export interface AssignmentRequest {
  driverId?: string;
  truckId?: string;
  trailerId?: string;
  matchScore?: number;
  matchLabel?: string;
  notes?: string;
  overrideReason?: string;
}

export interface AssignmentAudit {
  id: string;
  loadId: string;
  driverId: string | null;
  truckId: string | null;
  trailerId: string | null;
  matchScore: number | null;
  matchLabel: string | null;
  notes: string | null;
  overrideReason: string | null;
  warnings: AssignmentIssue[];
  recordedBy: string;
  recordedAt: string;
  alvysWriteback: string;
}

/**
 * Serializable snapshot of the workbench filter/sort state behind a saved view. Mirrors the
 * server `SavedViewFilters` (see src/LtlTool.Api/Features/Ltl/SavedViews/SavedViewModels.cs);
 * dates are the raw yyyy-MM-dd strings the date inputs produce. Nullable enums use `null` for
 * "any". Tool-local only — applying or saving a view never touches Alvys.
 */
export interface SavedViewFilters {
  keyword?: string | null;
  customer?: string | null;
  originState?: string | null;
  originCity?: string | null;
  destinationState?: string | null;
  destinationCity?: string | null;
  equipmentType?: string | null;
  assignment?: AssignmentState | null;
  pickupFrom?: string | null;
  pickupTo?: string | null;
  deliveryFrom?: string | null;
  deliveryTo?: string | null;
  billingBadge?: BillingBadge | null;
  stage?: WorkflowStage | null;
  ltlOnly: boolean;
  readyToBill: boolean;
  missingBillingData: boolean;
  exceptionsOnly: boolean;
  blockedOnly: boolean;
  sort: LtlSortField;
  sortDescending: boolean;
}

/** A named, persisted workbench view (shared built-in preset or dispatcher-owned). */
export interface SavedView {
  id: string;
  name: string;
  description: string | null;
  filters: SavedViewFilters;
  isBuiltIn: boolean;
  ownerId: string | null;
  createdAt: string | null;
  updatedAt: string | null;
}

/** Create/update payload for a dispatcher saved view. */
export interface SavedViewRequest {
  name: string;
  description?: string | null;
  filters: SavedViewFilters;
}

/** The saved-view collection: shared presets and the dispatcher's own views. */
export interface SavedViewCollection {
  presets: SavedView[];
  views: SavedView[];
}

/** Filter/sort/paging inputs for the normalized LTL search (mapped to query string). */
export interface LtlSearchQuery {
  keyword?: string;
  customer?: string;
  originState?: string;
  originCity?: string;
  destinationState?: string;
  destinationCity?: string;
  equipmentType?: string;
  status?: string[];
  pickupFrom?: string;
  pickupTo?: string;
  deliveryFrom?: string;
  deliveryTo?: string;
  assignment?: AssignmentState;
  ltlOnly?: boolean;
  readyToBill?: boolean;
  missingBillingData?: boolean;
  exceptionsOnly?: boolean;
  billingBadge?: BillingBadge;
  stage?: WorkflowStage;
  blockedOnly?: boolean;
  sort?: LtlSortField;
  sortDescending?: boolean;
  page?: number;
  pageSize?: number;
}

/** One equipment-type bucket in the trailer pool breakdown (Phase 7.4 capacity snapshot). */
export interface TrailerTypeCount {
  equipmentType: string;
  count: number;
}

/**
 * Live "Capacity today" snapshot (Phase 7.4): active trucks, trailer pool by equipment type, and
 * in-transit trips — every count a read-only Alvys read. `truncated` means a sweep hit its scan cap
 * so the counts are a floor ("at least N"), surfaced honestly rather than as an exact total.
 */
export interface CapacitySnapshot {
  generatedAt: string;
  activeTrucks: number;
  totalTrucks: number;
  inTransitTrips: number;
  totalTrailers: number;
  trailersByType: TrailerTypeCount[];
  truncated: boolean;
  source: string;
}

/**
 * Recent lane rate context (Phase 7.4): revenue-per-mile spread across recently delivered loads on
 * the same origin→destination state pair. Recent tenant history (what Value Truck billed), NOT a
 * DAT/Greenscreens market rate. Null RPMs mean too few priced samples — surfaced, never guessed.
 */
export interface LaneRateContext {
  originState: string;
  destinationState: string;
  sampleSize: number;
  medianRpm: number | null;
  minRpm: number | null;
  maxRpm: number | null;
  basis: string;
  generatedAt: string;
}
