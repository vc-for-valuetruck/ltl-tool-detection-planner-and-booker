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
  | 'InvoiceStatus';

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

export interface BillingReadinessResult {
  badges: BillingBadge[];
  missingFields: MissingDataFlag[];
  risks: string[];
  isReadyToBill: boolean;
  isAlreadyInvoiced: boolean;
  podEvaluated: boolean;
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
  equipment: string[];
  weightLbs: number | null;
  volume: number | null;
  revenue: number | null;
  mileage: number | null;
  revenuePerMile: number | null;
  isLtl: boolean | null;
  ltlClassification: string | null;
  missingData: MissingDataFlag[];
  billing: BillingReadinessResult;
  exceptions: LtlExceptionFlag[];
  hasExceptions: boolean;
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
  assignment?: AssignmentState;
  ltlOnly?: boolean;
  readyToBill?: boolean;
  missingBillingData?: boolean;
  exceptionsOnly?: boolean;
  sort?: LtlSortField;
  sortDescending?: boolean;
  page?: number;
  pageSize?: number;
}
