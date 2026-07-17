import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { RUNTIME_CONFIG } from '../../runtime-config';
import {
  ConsolidationAuditRecord,
  ConsolidationCandidateResponse,
  ConsolidationPlanRequest,
  ConsolidationPlanResponse,
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
}
