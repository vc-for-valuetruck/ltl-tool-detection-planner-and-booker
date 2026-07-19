import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { RUNTIME_CONFIG } from '../../runtime-config';
import {
  ConsolidationAuditRecord,
  ConsolidationCandidateResponse,
  ConsolidationPlanRequest,
  ConsolidationPlanResponse,
  CorridorSummary,
  CorridorHealth,
} from './consolidation.models';

/**
 * Client for the Phase 1 consolidation API. Read-only against Alvys — every call this service
 * makes returns data derived from live Alvys reads or from static configuration; nothing this
 * service exposes writes to Alvys.
 */
@Injectable({ providedIn: 'root' })
export class ConsolidationService {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(RUNTIME_CONFIG).apiBaseUrl}/ltl/consolidation`;

  getCandidates(
    loadId: string,
    corridor = 'LAREDO_TO_DALLAS',
  ): Observable<ConsolidationCandidateResponse> {
    const params = new HttpParams()
      .set('loadId', loadId)
      .set('corridor', corridor);
    return this.http.get<ConsolidationCandidateResponse>(
      `${this.base}/candidates`,
      { params },
    );
  }

  buildPlan(request: ConsolidationPlanRequest): Observable<ConsolidationPlanResponse> {
    return this.http.post<ConsolidationPlanResponse>(`${this.base}/plan`, request);
  }

  recordPlanAudit(request: ConsolidationPlanRequest): Observable<ConsolidationAuditRecord> {
    return this.http.post<ConsolidationAuditRecord>(`${this.base}/plan/audit`, request);
  }

  getPlanAudits(parentLoadId?: string): Observable<ConsolidationAuditRecord[]> {
    let params = new HttpParams();
    if (parentLoadId) params = params.set('parentLoadId', parentLoadId);
    return this.http.get<ConsolidationAuditRecord[]>(`${this.base}/plan/audits`, { params });
  }

  /**
   * Configured corridors, joined with warehouse metadata. Static config, cheap to fetch,
   * safe to cache in the caller's memory for the session.
   */
  getCorridors(): Observable<CorridorSummary[]> {
    return this.http.get<CorridorSummary[]>(`${this.base}/corridors`);
  }

  /**
   * Live per-corridor open-load counts. Hits Alvys once per corridor; the caller should not
   * poll aggressively — open on tab-load is enough.
   */
  getCorridorHealth(): Observable<CorridorHealth[]> {
    return this.http.get<CorridorHealth[]>(`${this.base}/corridors/health`);
  }
}
