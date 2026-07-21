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
}
