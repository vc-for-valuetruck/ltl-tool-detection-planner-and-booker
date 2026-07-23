import { ConsolidationAuditRecord, ConsolidationPlanResponse, WarehouseSummary } from './consolidation.models';

/**
 * TypeScript mirrors of the Phase 2.5 Dock mode API contracts
 * (see src/LtlTool.Api/Features/Ltl/Dock/DockModels.cs). Dock mode is a thin orchestration over
 * the existing arrivals + consolidation contracts — it introduces no new economic types, only the
 * warehouse/combine envelopes. Every value on-screen is either from the API response or from static
 * UI copy; the SPA cannot invent fields the server does not send.
 */

/** The configured yards a dock worker can pick (Laredo / Dallas in the pilot). */
export interface DockWarehousesResponse {
  warehouses: WarehouseSummary[];
}

/** Request to combine a parent load with sibling loads at the dock. Never an Alvys write. */
export interface DockCombineRequest {
  parentLoadId: string;
  siblingLoadIds: string[];
  corridorCode?: string;
  /** Yard the combine happened at — drives which per-warehouse notify recipients are emailed. */
  warehouseCode?: string;
}

/**
 * Honest outcome of the combine-summary notification. `state` mirrors the delivery state
 * (`Delivered` / `Pending` / `NotConfigured` / `Failed`) plus a Dock-specific `Disabled` when the
 * yard has no configured recipients. The SPA shows a one-tap retry chip only for `Failed`.
 */
export interface DockNotificationResult {
  state: 'Delivered' | 'Pending' | 'NotConfigured' | 'Failed' | 'Disabled';
  recipients: string[];
  detail?: string;
}

/** Request to record a one-tap Undo of a just-committed combine. Never an Alvys write. */
export interface DockUndoRequest {
  parentLoadId: string;
  siblingLoadIds: string[];
  corridorCode?: string;
}

/** Result of an undo: the retraction audit record (`audit.action === 'Undo'`). */
export interface DockUndoResponse {
  audit: ConsolidationAuditRecord;
}

/** Fire-and-forget dock combine effectiveness metric (time-to-combine + tap count). */
export interface DockCombineMetric {
  warehouseCode?: string;
  siblingCount?: number;
  tapCount?: number;
  timeToCombineMs?: number;
}

/**
 * Result of a dock combine: the full consolidation plan preview (click card + combined economics,
 * blockers) plus the internal audit record the combine wrote. The SPA renders the BOL packet / dock
 * manifest and the Alvys click card from the plan; the audit is the leadership-visible record that a
 * combine happened. `audit.alvysWriteback` is always `NotPerformed`.
 */
export interface DockCombineResponse {
  plan: ConsolidationPlanResponse;
  audit: ConsolidationAuditRecord;
  /** Outcome of the combine-summary notification; `state === 'Disabled'` when no recipients configured. */
  notification: DockNotificationResult;
}

/** Which photo/inspection gates the yard captured. Honest `false` for an uncaptured gate. */
export interface PhotoGates {
  tractor: boolean;
  trailer: boolean;
  seal: boolean;
}

/**
 * Yard-presence projection for the Review-step chip (mirrors DockPresenceResponse). Presence is a
 * peer signal, never operational truth: `configured === false` (integration off) and `available ===
 * false` (yard unreachable) both render the grey "unavailable" chip. `securityHold` is the red state
 * that disables Combine; `atYard === false` is amber; otherwise green. Never fabricated into a pass.
 */
export interface DockPresenceResponse {
  configured: boolean;
  available: boolean;
  onRecord: boolean;
  atYard: boolean;
  driverPresent: boolean;
  securityHold: boolean;
  releasedAt?: string;
  lastEventAt?: string;
  gates?: PhotoGates;
}

/** One freight line on a yard-originated LTL draft. Every measure nullable — render "—", never 0. */
export interface YardFreightLine {
  loadId?: string;
  pallets?: number;
  pieces?: number;
  weightLbs?: number;
  dims?: { lengthIn?: number; widthIn?: number; heightIn?: number };
  osd?: { overage?: boolean; shortage?: boolean; damage?: boolean; notes?: string };
}

/**
 * A yard-originated LTL consolidation opportunity (from an `LtlDraftCreated` webhook), surfaced as a
 * dock incoming-opportunity card. Inbound suggestion only — the dock acts on it inside its own
 * Alvys-backed combine flow. Null fields are rendered "—" (honest missing state, never fabricated).
 */
export interface YardOpportunityView {
  id: string;
  draftId: string;
  yardCode?: string;
  parentLoadId?: string;
  siblingLoadIds: string[];
  freight: YardFreightLine[];
  createdByStation?: string;
  scannedAt?: string;
  receivedAt: string;
}

/** The dock incoming-opportunity list (newest first). */
export interface DockOpportunitiesResponse {
  opportunities: YardOpportunityView[];
}
