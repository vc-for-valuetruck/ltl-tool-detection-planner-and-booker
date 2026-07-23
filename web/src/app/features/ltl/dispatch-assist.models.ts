/**
 * Dispatch Assist view models — the SPA-side mirror of the backend
 * `Features/Ltl/DispatchAssist` contracts. Read-only against Alvys: recommendations are assembled
 * from live Alvys reads, an assembly is recorded app-side only (`alvysWriteback = "NotPerformed"`),
 * and the notify step honours the `Ltl:Comms` override/flag. Unknown fields arrive as null and
 * render as "—"; nothing here is fabricated.
 */

/** The pickup geography a set of recommendations was ranked against, plus its provenance. */
export interface DispatchTarget {
  readonly loadId: string | null;
  readonly loadNumber: string | null;
  readonly originCity: string | null;
  readonly originState: string | null;
  readonly destinationCity: string | null;
  readonly destinationState: string | null;
  readonly requiredEquipment: readonly string[];
  readonly source: string;
}

/** One ranked driver+truck+trailer candidate, with the reasons behind its score. */
export interface DispatchCandidate {
  readonly driverId: string | null;
  readonly driverName: string | null;
  readonly driverEmail: string | null;
  readonly driverPhone: string | null;
  readonly driverHomeState: string | null;
  readonly dutyStatus: string;
  readonly truckId: string | null;
  readonly truckNumber: string | null;
  readonly trailerId: string | null;
  readonly trailerNumber: string | null;
  readonly trailerEquipmentType: string | null;
  readonly preferredDispatcherId: string | null;
  readonly isPreferredPairing: boolean;
  readonly referenceMilesFromOrigin: number | null;
  readonly score: number;
  readonly reasons: readonly string[];
}

/** Ranked recommendations for a target, best first, with the honest Alvys posture banner. */
export interface DispatchRecommendationsResponse {
  readonly target: DispatchTarget;
  readonly candidates: readonly DispatchCandidate[];
  readonly truncated: boolean;
  readonly alvysPosture: string;
}

/** Query for the recommendations endpoint. A loadId resolves the target from Alvys; else ad-hoc lane. */
export interface DispatchRecommendationsQuery {
  readonly loadId?: string;
  readonly originCity?: string;
  readonly originState?: string;
  readonly destinationCity?: string;
  readonly destinationState?: string;
  readonly top?: number;
}

/** Request to record a chosen driver+truck+trailer assembly app-side. */
export interface DispatchAssembleRequest {
  readonly loadId?: string | null;
  readonly loadNumber?: string | null;
  readonly driverId?: string | null;
  readonly truckId?: string | null;
  readonly trailerId?: string | null;
  readonly score?: number | null;
  readonly reasons?: readonly string[];
}

/** An intended notification recipient resolved from Alvys contacts. */
export interface DispatchNotifyRecipient {
  readonly role: string;
  readonly name: string | null;
  readonly address: string | null;
}

/** The notify-step outcome fired on assembly, including the override banner state. */
export interface DispatchNotifyResult {
  readonly sent: boolean;
  readonly state: 'NotEnabled' | 'Sent' | 'Failed' | 'NoRecipients' | string;
  readonly overrideActive: boolean;
  readonly overrideRecipient: string | null;
  readonly intendedRecipients: readonly DispatchNotifyRecipient[];
  readonly effectiveRecipients: readonly string[];
  readonly detail: string | null;
}

/** The app-side record of an assembly decision. `alvysWriteback` stays "NotPerformed" in this slice. */
export interface DispatchAssembly {
  readonly id: string;
  readonly recordedAt: string;
  readonly recordedBy: string;
  readonly loadId: string | null;
  readonly loadNumber: string | null;
  readonly driverId: string | null;
  readonly truckId: string | null;
  readonly trailerId: string | null;
  readonly score: number | null;
  readonly reasons: readonly string[];
  readonly alvysWriteback: string;
  readonly notify: DispatchNotifyResult;
}
