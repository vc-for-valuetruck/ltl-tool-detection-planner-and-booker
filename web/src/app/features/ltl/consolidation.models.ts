import { LtlLoadSummary } from './ltl.models';

/**
 * TypeScript mirrors of the C# consolidation API contracts. Kept intentionally narrow so the
 * SPA cannot invent fields the server does not send — every value on-screen is either from the
 * API response or from static UI copy.
 */

export type ConsolidationFit = 'Unknown' | 'Good' | 'Tight' | 'Blocked';

export interface ConsolidationFactor {
  name: string;
  fit: ConsolidationFit;
  rationale: string;
}

export type CustomerConsolidationTier =
  | 'Unknown'
  | 'Allowed'
  | 'NotifyRequired'
  | 'Never';

/** Provenance of a resolved consolidation tier — mirrors the C# CustomerPolicySource enum. */
export type CustomerPolicySource = 'None' | 'CustomerNote' | 'DefaultPolicy';

export interface ConsolidationCandidate {
  loadId: string;
  loadNumber?: string;
  customerName?: string;
  originLabel?: string;
  destinationLabel?: string;
  scheduledPickupAt?: string;
  scheduledDeliveryAt?: string;
  revenue?: number;
  weightLbs?: number;
  corridorCode: string;
  factors: ConsolidationFactor[];
  isBlocked: boolean;
  customerTier: CustomerConsolidationTier;
}

export interface ConsolidationCandidateResponse {
  corridorCode: string;
  seed?: LtlLoadSummary | null;
  candidates: ConsolidationCandidate[];
  scanTruncated: boolean;
}

export interface ConsolidationPlanRequest {
  parentLoadId: string;
  siblingLoadIds: string[];
  corridorCode?: string;
}

export interface ConsolidationPlanSibling {
  loadId: string;
  loadNumber?: string;
  customerName?: string;
  originLabel?: string;
  destinationLabel?: string;
  scheduledPickupAt?: string;
  scheduledDeliveryAt?: string;
  revenue?: number;
  weightLbs?: number;
  /** Driver-facing trip rate (Trip.TripValue.Amount). Null when no trip is fetched. */
  driverTripRate?: number;
  /** Driver-facing loaded miles (Trip.LoadedMileage.Distance.Value). */
  loadedMiles?: number;
  customerTier: CustomerConsolidationTier;
  customerPolicySource: CustomerPolicySource;
  cautions: string[];
}

export interface ConsolidationClickCard {
  plainText: string;
  tripReferenceValue: string;
  mainLoadIdReferenceValue: string;
}

export interface ConsolidationPlanResponse {
  previewId: string;
  corridorCode: string;
  parent: LtlLoadSummary;
  siblings: ConsolidationPlanSibling[];
  /** Customer-billing total (kept for context; not the RPM numerator). */
  combinedRevenue?: number;
  /** Parent's customer-billing miles (kept for context; not the RPM denominator). */
  linehaulMiles?: number;
  /** Parent's driver-facing loaded miles — the actual RPM denominator. */
  driverLoadedMiles?: number;
  /** Combined driver trip value — the RPM numerator. */
  combinedDriverTripValue?: number;
  /** Combined driver RPM = combinedDriverTripValue / driverLoadedMiles. */
  combinedRevenuePerMile?: number;
  clickCard: ConsolidationClickCard;
  /**
   * Trailer-fit verdict for the combined load. Present only when the trailer-fit engine is
   * enabled server-side; `undefined` when the NullTrailerFitService is active so the SPA shows
   * "verify at dock" rather than implying a fit was checked.
   */
  trailerFit?: ConsolidationTrailerFit;
  blockers: string[];
}

/**
 * SPA mirror of the C# ConsolidationTrailerFit DTO. Every numeric field is optional and stays
 * absent when the value is genuinely unknown — never coerced to zero.
 */
export interface ConsolidationTrailerFit {
  /** Coarse verdict: 'Unknown' | 'Fits' | 'DoesNotFit'. */
  verdict: 'Unknown' | 'Fits' | 'DoesNotFit';
  rationale: string;
  /** True when the verdict was computed from assumed dimensions ("estimated fit"). */
  estimatedFit: boolean;
  linearFeet?: number;
  weightUtilization?: number;
  cubeUtilization?: number;
  totalWeightLbs?: number;
  trailerMaxWeightLbs?: number;
  totalPallets?: number;
  trailerMaxPallets?: number;
  /** True when combined weight/pallets exceed the trailer capacity. */
  capacityExceeded: boolean;
  /** True when one or more loads had no weight — the UI shows "≥ N lb". */
  weightUnknown: boolean;
}

export interface ConsolidationAuditRecord {
  id: string;
  corridorCode: string;
  parentLoadId: string;
  parentLoadNumber?: string;
  parentCustomerName?: string;
  siblingLoadIds: string[];
  siblingLoadNumbers: string[];
  combinedRevenue?: number;
  linehaulMiles?: number;
  driverLoadedMiles?: number;
  combinedDriverTripValue?: number;
  combinedRevenuePerMile?: number;
  blockers: string[];
  alvysWriteback: string;
  recordedBy: string;
  recordedAt: string;
}

/** Public projection of a configured consolidation corridor. Static config; safe to cache. */
export interface CorridorSummary {
  code: string;
  origin: WarehouseSummary;
  destination: WarehouseSummary;
  pickupWindowDays: number;
  deliveryWindowDays: number;
}

export interface WarehouseSummary {
  code: string;
  name: string;
  state: string;
  nearbyCities: string[];
}

/**
 * One live consolidation opportunity discovered by the "Today's consolidations" sweep
 * (`GET /ltl/consolidation/opportunities`). Every field is a live Alvys read grouped by
 * same-customer / same-day / same-lane — never fabricated. Reused by the Consolidate board to
 * discover live lanes (across all lanes, not just the pilot corridor) so the demo can walk the
 * full workflow against real data even when the pilot corridor has no open loads today.
 */
export interface ConsolidationOpportunityLoad {
  loadNumber: string;
  loadId: string;
  customerName: string;
  originCity: string;
  originState: string;
  destinationCity: string;
  destinationState: string;
  linehaulAmount: number;
  miles: number;
  rpm: number;
  weightPounds: number | null;
}

export interface ConsolidationOpportunity {
  rank: number;
  originState: string;
  destinationState: string;
  originCity: string;
  destinationCity: string;
  pickupDate: string;
  customerName: string;
  combinedRevenue: number;
  parentLinehaulMiles: number;
  combinedRpm: number;
  projectedUplift: number;
  parent: ConsolidationOpportunityLoad;
  siblings: ConsolidationOpportunityLoad[];
}

export interface ConsolidationOpportunitiesResponse {
  opportunities: ConsolidationOpportunity[];
  totalScanned: number;
  totalPairsFound: number;
  generatedAt: string;
  dataSource: string;
}

/**
 * Live per-corridor open-load count. `openLoadCount === null` means the Alvys read
 * degraded — the UI must show "unknown" honestly rather than a misleading zero.
 */
export interface CorridorHealth {
  code: string;
  openLoadCount: number | null;
  truncated: boolean;
  originCity: string;
  destinationCity: string;
  /** First open load on the lane — used to auto-seed the queue by default. Absent when empty. */
  seedLoadId?: string | null;
  seedLoadNumber?: string | null;
}
