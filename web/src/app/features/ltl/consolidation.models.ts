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
  blockers: string[];
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
