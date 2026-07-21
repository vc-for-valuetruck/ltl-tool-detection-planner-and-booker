/**
 * Client-side types for the Phase 6 inbound signal-ingestion API. Mirrors `/api/ltl/signals`.
 * Enums are serialized as strings by the API.
 *
 * Guardrail: a signal is text-only. There is no numeric operational field here — `confidence` is an
 * extraction score, never revenue/weight/miles. Accepting a signal annotates internal LTL surfaces
 * and never writes to Alvys.
 */

export type SignalType =
  | 'AccessorialEvidence'
  | 'ConsolidationOpportunity'
  | 'CustomerVisibilityPosture'
  | 'BillingRisk'
  | 'DelayedLoad'
  | 'MissingDocs'
  | 'NewLane'
  | 'NewSite'
  | 'EquipmentNeed'
  | 'ContractSignal'
  | 'CompetitiveIntel'
  | 'ServiceIssue'
  | 'ContactSuggestion'
  | 'Other';

export type LtlSurface =
  | 'SearchFilter'
  | 'BillingWorklistBadge'
  | 'Exception'
  | 'MatchWarning'
  | 'SavedView'
  | 'AuditNote'
  | 'NextBestAction';

export type SignalStatus = 'Pending' | 'Accepted' | 'Rejected';

export type SignalSourceType = 'note' | 'email' | 'transcript' | 'call';

export interface SignalView {
  id: string;
  sourceType: string;
  sourceId: string;
  signalType: SignalType;
  confidence: number;
  evidenceQuote: string;
  suggestedSurface: LtlSurface;
  summary?: string | null;
  loadNumber?: string | null;
  status: SignalStatus;
  ingestedBy: string;
  createdAt: string;
  decidedAt?: string | null;
  decidedBy?: string | null;
}

export interface SignalIngestRequest {
  sourceType: SignalSourceType;
  sourceId: string;
  text: string;
  loadNumber?: string | null;
}

export interface SignalIngestResponse {
  count: number;
  signals: SignalView[];
}

export interface SignalExtractorStatus {
  name: string;
}
