/**
 * TypeScript projections of the sandbox-gated Alvys writeback boundary
 * (see src/LtlTool.Api/Features/Integrations/Alvys/Writeback/*). Enums are modeled as string
 * unions because the API serializes enums by name (JsonStringEnumConverter). Nothing here ever
 * mutates Alvys in this phase: every disposition is audit-only, simulated or unsupported and
 * `executed` is always false.
 */

export type AlvysWritebackMode = 'Disabled' | 'Simulation' | 'Sandbox';

export type AlvysOperationDisposition =
  | 'Blocked'
  | 'AuditOnly'
  | 'Simulated'
  | 'Unsupported'
  | 'SandboxExecuted';

export type AlvysOperationEligibility =
  | 'AuditOnly'
  | 'SimulationOnly'
  | 'SandboxEligible'
  | 'Unsupported';

export type AlvysSyncOutcome = 'Unknown' | 'Success' | 'Failure';

/** Static catalogue entry describing a write-oriented operation and its live-execution support. */
export interface AlvysWriteOperationDescriptor {
  code: string;
  kind: string;
  title: string;
  description: string;
  workflowStage: string;
  requiresEtag: boolean;
  liveSupport: 'Supported' | 'Unsupported';
  requiredToEnable?: string | null;
}

/** Per-operation readiness shown in the operational-readiness panel. */
export interface AlvysOperationReadiness {
  code: string;
  title: string;
  workflowStage: string;
  requiresEtag: boolean;
  eligibility: AlvysOperationEligibility;
  blockers: string[];
  requiredToEnable?: string | null;
}

/** The Alvys sandbox/writeback readiness snapshot. Carries no secrets — only a host root + flag. */
export interface AlvysReadinessStatus {
  provider: string;
  hasCredentials: boolean;
  apiBaseUrl: string;
  writebackMode: AlvysWritebackMode;
  environment: string;
  writebackEnabled: boolean;
  sandboxExecutionConfigured: boolean;
  sandboxBaseUrl?: string | null;
  lastReadSyncOutcome: AlvysSyncOutcome;
  lastReadSyncAt?: string | null;
  lastReadSyncDetail?: string | null;
  blockers: string[];
  operations: AlvysOperationReadiness[];
}

/** Inputs for a write-oriented operation. Only fields relevant to the operation are read. */
export interface AlvysOperationRequest {
  loadNumber?: string;
  tenderId?: string;
  tripId?: string;
  stopId?: string;
  noteText?: string;
  noteType?: string;
  arrivedAt?: string;
  departedAt?: string;
  etag?: string;
  fields?: Record<string, string | null>;
  reason?: string;
}

/** The dry-run preview of the body that would be sent (never is, in this phase). */
export interface AlvysOperationPayload {
  operationCode: string;
  targetDescription: string;
  requiresEtag: boolean;
  etagSupplied: boolean;
  body: Record<string, unknown>;
}

export interface AlvysOperationIssue {
  code: string;
  message: string;
}

/** The outcome of a dry-run/execute request. `executed` is always false in this phase. */
export interface AlvysOperationOutcome {
  operationCode: string;
  title: string;
  mode: AlvysWritebackMode;
  disposition: AlvysOperationDisposition;
  executed: boolean;
  message: string;
  payload?: AlvysOperationPayload | null;
  validation: AlvysOperationIssue[];
  blockers: string[];
  requiredToEnable?: string | null;
}
