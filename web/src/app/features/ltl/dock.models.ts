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
