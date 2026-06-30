import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { RUNTIME_CONFIG } from '../../runtime-config';
import {
  AlvysOperationOutcome,
  AlvysOperationRequest,
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

  /** Builds and validates the payload for preview without ever sending it to Alvys. */
  dryRun(operation: string, request: AlvysOperationRequest): Observable<AlvysOperationOutcome> {
    return this.http.post<AlvysOperationOutcome>(
      `${this.base}/${encodeURIComponent(operation)}/dry-run`,
      request,
    );
  }

  /** Attempts an operation. Honours the configured writeback mode; never mutates Alvys here. */
  execute(operation: string, request: AlvysOperationRequest): Observable<AlvysOperationOutcome> {
    return this.http.post<AlvysOperationOutcome>(
      `${this.base}/${encodeURIComponent(operation)}/execute`,
      request,
    );
  }

  /** Opt-in bounded read probe that records a "last successful read" time. Never a mutation. */
  probe(): Observable<AlvysReadinessStatus> {
    return this.http.post<AlvysReadinessStatus>(`${this.base}/sync/probe`, {});
  }
}
