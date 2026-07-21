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
  AlvysWebhookAdminView,
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
  private readonly apiBaseUrl = inject(RUNTIME_CONFIG).apiBaseUrl;
  private readonly base = `${this.apiBaseUrl}/alvys/ops`;

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

  /**
   * Uploads a single billing document to a load via the Public-API multipart endpoint. The file bytes
   * go straight into a multipart body — never into JSON, never persisted client-side beyond the request.
   * Honours the configured writeback mode server-side; the response record states whether it was pushed
   * to the sandbox or recorded internally only, plus any post-write reconciliation outcome.
   */
  uploadLoadDocument(
    loadNumber: string,
    documentType: string,
    file: File,
    reason?: string,
    idempotencyKey?: string,
  ): Observable<AlvysOperationResponse> {
    const form = new FormData();
    form.append('File', file, file.name);
    form.append('LoadNumber', loadNumber);
    form.append('DocumentType', documentType);
    if (reason) form.append('Reason', reason);
    return this.http.post<AlvysOperationResponse>(
      `${this.base}/upload-load-document`,
      form,
      idempotencyKey ? { headers: new HttpHeaders({ 'Idempotency-Key': idempotencyKey }) } : {},
    );
  }

  /** Uploads a single document to a trip via the Public-API multipart endpoint. See {@link uploadLoadDocument}. */
  uploadTripDocument(
    tripId: string,
    documentType: string,
    file: File,
    reason?: string,
    idempotencyKey?: string,
  ): Observable<AlvysOperationResponse> {
    const form = new FormData();
    form.append('File', file, file.name);
    form.append('TripId', tripId);
    form.append('DocumentType', documentType);
    if (reason) form.append('Reason', reason);
    return this.http.post<AlvysOperationResponse>(
      `${this.base}/upload-trip-document`,
      form,
      idempotencyKey ? { headers: new HttpHeaders({ 'Idempotency-Key': idempotencyKey }) } : {},
    );
  }

  /** The read-only webhook admin snapshot: recent received events + receiver configuration state. */
  webhookEvents(max?: number): Observable<AlvysWebhookAdminView> {
    const url = max
      ? `${this.apiBaseUrl}/alvys/webhooks/events?max=${max}`
      : `${this.apiBaseUrl}/alvys/webhooks/events`;
    return this.http.get<AlvysWebhookAdminView>(url);
  }
}
