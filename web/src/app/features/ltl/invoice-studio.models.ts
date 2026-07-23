/**
 * Invoice Studio contracts (SPA ↔ /api/ltl/invoices). Mirrors the C# read/request models. The studio
 * assembles a customer invoice from a consolidation (parent + sibling loads), keeps per-load charges
 * editable, computes totals + combined driver-RPM, tracks BOL presence, and previews the exact
 * contracted Alvys write payloads. Read-only against Alvys — nothing is pushed (AlvysWriteback stays
 * "NotPerformed").
 */

export type InvoiceStatus = 'Draft' | 'Final';

export type InvoiceChargeType =
  | 'Linehaul'
  | 'FuelSurcharge'
  | 'Accessorial'
  | 'Detention'
  | 'Other';

export const INVOICE_CHARGE_TYPES: readonly InvoiceChargeType[] = [
  'Linehaul',
  'FuelSurcharge',
  'Accessorial',
  'Detention',
  'Other',
];

export interface InvoiceCharge {
  readonly id: string;
  readonly type: InvoiceChargeType;
  readonly description?: string | null;
  readonly amount: number;
}

export interface InvoiceLoadLine {
  readonly loadId: string;
  readonly loadNumber?: string | null;
  readonly isParent: boolean;
  readonly customerName?: string | null;
  readonly status?: string | null;
  readonly alvysLoadUrl?: string | null;
  readonly bolPresent: boolean;
  readonly bolArtifactId?: string | null;
  readonly loadedMiles?: number | null;
  readonly driverTripRate?: number | null;
  readonly charges: readonly InvoiceCharge[];
  readonly lineTotal: number;
}

export interface InvoiceEditEvent {
  readonly at: string;
  readonly by: string;
  readonly action: string;
  readonly detail?: string | null;
}

export interface InvoiceView {
  readonly id: string;
  readonly invoiceNumber: string;
  readonly status: InvoiceStatus;
  readonly corridorCode?: string | null;
  readonly customerId?: string | null;
  readonly customerName?: string | null;
  readonly parentLoadId?: string | null;
  readonly parentLoadNumber?: string | null;
  readonly notes?: string | null;
  readonly loads: readonly InvoiceLoadLine[];
  readonly editHistory: readonly InvoiceEditEvent[];
  readonly invoiceTotal: number;
  readonly combinedRevenue: number;
  readonly combinedDriverTripValue?: number | null;
  readonly driverLoadedMiles?: number | null;
  readonly combinedRevenuePerMile?: number | null;
  readonly loadsMissingBol: readonly string[];
  readonly createdBy: string;
  readonly createdAt: string;
  readonly updatedBy: string;
  readonly updatedAt: string;
  readonly finalizedAt?: string | null;
  readonly finalizedBy?: string | null;
  readonly alvysWriteback: string;
}

export interface InvoiceSummary {
  readonly id: string;
  readonly invoiceNumber: string;
  readonly status: InvoiceStatus;
  readonly customerName?: string | null;
  readonly parentLoadNumber?: string | null;
  readonly loadCount: number;
  readonly loadsMissingBolCount: number;
  readonly invoiceTotal: number;
  readonly combinedRevenuePerMile?: number | null;
  readonly updatedAt: string;
  readonly alvysWriteback: string;
}

// --- Request shapes (SPA → API) -------------------------------------------------------------------

export interface InvoiceChargeInput {
  type: InvoiceChargeType;
  description?: string | null;
  amount: number;
}

export interface InvoiceLoadInput {
  loadId?: string | null;
  loadNumber?: string | null;
  isParent: boolean;
  customerName?: string | null;
  status?: string | null;
  alvysLoadUrl?: string | null;
  bolPresent: boolean;
  bolArtifactId?: string | null;
  loadedMiles?: number | null;
  driverTripRate?: number | null;
  charges: InvoiceChargeInput[];
}

export interface AssembleInvoiceRequest {
  invoiceNumber?: string | null;
  corridorCode?: string | null;
  customerId?: string | null;
  customerName?: string | null;
  notes?: string | null;
  loads: InvoiceLoadInput[];
}

export interface UpdateInvoiceRequest {
  notes?: string | null;
  loads: InvoiceLoadInput[];
}

// --- Alvys write-payload preview (gated) ----------------------------------------------------------

export interface AlvysOperationIssue {
  readonly code: string;
  readonly message: string;
}

export interface AlvysOperationPayload {
  readonly operationCode: string;
  readonly targetDescription: string;
  readonly requiresEtag: boolean;
  readonly etagSupplied: boolean;
  readonly body: Record<string, unknown>;
}

export interface AlvysOperationOutcome {
  readonly operationCode: string;
  readonly title: string;
  readonly mode: string;
  readonly disposition: string;
  readonly executed: boolean;
  readonly sandboxExecutionEligible: boolean;
  readonly internalExecutionEligible: boolean;
  readonly message: string;
  readonly payload?: AlvysOperationPayload | null;
  readonly validation: readonly AlvysOperationIssue[];
  readonly blockers: readonly string[];
  readonly requiredToEnable?: string | null;
}
