/**
 * TypeScript mirror of the read-only dispatch-planner contracts
 * (src/LtlTool.Api/Features/Ltl/DispatchPlanner/DispatchPlannerModels.cs). The planner data comes
 * from the Alvys Public API dispatch-preference read; the SPA only ever displays what the API sends.
 * When `resolved` is false every id is null — the UI must render "—", never invent a pairing.
 */
export interface DispatchPreferenceView {
  resolved: boolean;
  dispatcherId?: string | null;
  driver1Id?: string | null;
  driver2Id?: string | null;
  truckId?: string | null;
  trailerId?: string | null;
  updatedAt?: string | null;
  source: string;
}
