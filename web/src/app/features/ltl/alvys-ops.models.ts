/**
 * TypeScript projections of the sandbox-gated Alvys writeback boundary
 * (see src/LtlTool.Api/Features/Integrations/Alvys/Writeback/*). Enums are modeled as string
 * unions because the API serializes enums by name (JsonStringEnumConverter). Every operation is
 * config-gated: `AuditOnly`/`Simulated`/`Unsupported` never reach Alvys, and `SandboxExecuted`/
 * `SandboxFailed` only occur when Mode=Sandbox is fully configured against a non-production host
 * — never a production tenant (see docs/ltl-tool.md).
 */

export type AlvysWritebackMode = 'Disabled' | 'Simulation' | 'Sandbox';

export type AlvysOperationDisposition =
  | 'Blocked'
  | 'AuditOnly'
  | 'Simulated'
  | 'Unsupported'
  | 'SandboxExecuted'
  | 'SandboxFailed';

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

/** Maps a tender stop to the Alvys company linked to it on acceptance (tender-accept). */
export interface TenderStopCompanyLink {
  stopId: string;
  companyId: string;
}

/** Inputs for a write-oriented operation. Only fields relevant to the operation are read. */
export interface AlvysOperationRequest {
  loadNumber?: string;
  tenderId?: string;
  tripId?: string;
  stopId?: string;
  carrierId?: string;
  driverId?: string;
  truckId?: string;
  trailerId?: string;
  status?: string;
  noteText?: string;
  noteType?: string;
  stopCompanyLinks?: TenderStopCompanyLink[];
  fleetId?: string;
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

/** Which channel produced an audit record: a dry-run preview or an execute attempt. */
export type AlvysOperationChannel = 'DryRun' | 'Execute';

/** Lifecycle status of a persisted audit/outbox record. Nothing is ever sent to Alvys in this phase. */
export type AlvysOperationRecordStatus = 'Recorded' | 'Blocked' | 'Unsupported';

/**
 * Owner-safe projection of a persisted Alvys operation audit/outbox record. Carries no secrets and
 * never another dispatcher's data — only the auditable facts of one of the current owner's attempts.
 */
export interface AlvysOperationRecordView {
  id: string;
  operationCode: string;
  channel: AlvysOperationChannel;
  resourceType?: string | null;
  resourceId?: string | null;
  idempotencyKey?: string | null;
  payloadHash: string;
  payloadPreview?: string | null;
  mode: AlvysWritebackMode;
  disposition: AlvysOperationDisposition;
  status: AlvysOperationRecordStatus;
  reason?: string | null;
  lastError?: string | null;
  attemptCount: number;
  correlationId: string;
  createdAt: string;
  updatedAt: string;
}

/**
 * The dry-run/execute response: the gateway outcome plus the auditable record it produced and
 * whether the request was an idempotent replay of an existing executable record.
 */
export interface AlvysOperationResponse {
  outcome: AlvysOperationOutcome;
  record?: AlvysOperationRecordView | null;
  replayed: boolean;
}

/**
 * The 409 conflict body returned when an idempotency key is reused with a different payload.
 * Nothing was recorded; reuse the original payload or choose a new key.
 */
export interface AlvysOperationConflict {
  message: string;
  idempotencyKey: string;
  existingRecordId: string;
}
