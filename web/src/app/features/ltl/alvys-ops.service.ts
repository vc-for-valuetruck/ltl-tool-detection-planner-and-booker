import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { RUNTIME_CONFIG } from '../../runtime-config';
import { HttpHeaders } from '@angular/common/http';
import {
  AlvysOperationRecordView,
  AlvysOperationRequest,
  AlvysOperationResponse,
  AlvysReadinessStatus,
  AlvysWriteOperationDescriptor,
} from './alvys-ops.models';

/**
 * Client for the sandbox-gated Alvys operation boundary (`/api/alvys/ops/*`). Bearer tokens are
 * attached by the MSAL interceptor when auth is configured. This service only reads readiness and
 * previews/attempts operations — no path here ever performs a live Alvys mutation in this phase.
 */
@Injectable({ providedIn: 'root' })
export class AlvysOpsService {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(RUNTIME_CONFIG).apiBaseUrl}/alvys/ops`;

  status(): Observable<AlvysReadinessStatus> {
    return this.http.get<AlvysReadinessStatus>(`${this.base}/status`);
  }

  operations(): Observable<AlvysWriteOperationDescriptor[]> {
    return this.http.get<AlvysWriteOperationDescriptor[]>(`${this.base}/operations`);
  }

  /**
   * Previews and records the payload for an operation as an auditable dry-run, without ever sending
   * it to Alvys. Returns the outcome plus the audit record that was written.
   */
  dryRun(operation: string, request: AlvysOperationRequest): Observable<AlvysOperationResponse> {
    return this.http.post<AlvysOperationResponse>(
      `${this.base}/${encodeURIComponent(operation)}/dry-run`,
      request,
    );
  }

  /**
   * Attempts an operation. Honours the configured writeback mode; never mutates Alvys here. An
   * idempotency key (sent via the `Idempotency-Key` header) de-duplicates equivalent retries and
   * surfaces a 409 conflict when reused with a different payload.
   */
  execute(
    operation: string,
    request: AlvysOperationRequest,
    idempotencyKey?: string,
  ): Observable<AlvysOperationResponse> {
    const headers = idempotencyKey
      ? new HttpHeaders({ 'Idempotency-Key': idempotencyKey })
      : undefined;
    return this.http.post<AlvysOperationResponse>(
      `${this.base}/${encodeURIComponent(operation)}/execute`,
      request,
      headers ? { headers } : {},
    );
  }

  /** The current owner's operation history (audit/outbox), newest first. */
  history(limit?: number): Observable<AlvysOperationRecordView[]> {
    const url = limit ? `${this.base}/history?limit=${limit}` : `${this.base}/history`;
    return this.http.get<AlvysOperationRecordView[]>(url);
  }

  /** Opt-in bounded read probe that records a "last successful read" time. Never a mutation. */
  probe(): Observable<AlvysReadinessStatus> {
    return this.http.post<AlvysReadinessStatus>(`${this.base}/sync/probe`, {});
  }
}
