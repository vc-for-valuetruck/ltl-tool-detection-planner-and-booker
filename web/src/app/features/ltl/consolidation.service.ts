import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { RUNTIME_CONFIG } from '../../runtime-config';
import {
  CombinedPlanBillingView,
  ConsolidationAuditRecord,
  ConsolidationCandidateResponse,
  ConsolidationOpportunitiesResponse,
  ConsolidationPlanRequest,
  ConsolidationPlanResponse,
  CorridorSummary,
  CorridorHealthSnapshot,
  LaneTemplateView,
  SaveLaneTemplateRequest,
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
  getCorridorHealth(): Observable<CorridorHealthSnapshot> {
    return this.http.get<CorridorHealthSnapshot>(`${this.base}/corridors/health`);
  }

  /**
   * The "Today's consolidations" sweep: same-customer / same-day / same-lane opportunities
   * discovered from live Alvys loads across ALL lanes (not just the pilot corridor). The
   * Consolidate board reuses this to surface live lanes in the corridor picker so the workflow
   * can be walked against real data when the pilot corridor is empty. Read-only.
   */
  getOpportunities(limit = 25): Observable<ConsolidationOpportunitiesResponse> {
    const params = new HttpParams().set('limit', limit);
    return this.http.get<ConsolidationOpportunitiesResponse>(
      `${this.base}/opportunities`,
      { params },
    );
  }

  /**
   * Combined-RPM billing view for a consolidation-audited parent load. Returns `{ found: false }`
   * when the load has no consolidation audit on file. Read-only, driver-math RPM.
   */
  getCombinedRpm(loadId: string): Observable<CombinedPlanBillingView> {
    const params = new HttpParams().set('loadId', loadId);
    return this.http.get<CombinedPlanBillingView>(`${this.base}/plan/combined-rpm`, { params });
  }

  /** Saves a recurring-lane template (Phase 2.5). Internal data — never an Alvys write. */
  saveLaneTemplate(request: SaveLaneTemplateRequest): Observable<LaneTemplateView> {
    return this.http.post<LaneTemplateView>(`${this.base}/plan/templates`, request);
  }

  /** Lists recurring-lane templates, newest first, optionally filtered by corridor/customer. */
  getLaneTemplates(corridorCode?: string, customerName?: string): Observable<LaneTemplateView[]> {
    let params = new HttpParams();
    if (corridorCode) params = params.set('corridorCode', corridorCode);
    if (customerName) params = params.set('customerName', customerName);
    return this.http.get<LaneTemplateView[]>(`${this.base}/plan/templates`, { params });
  }

  /** Deletes a recurring-lane template by id. */
  deleteLaneTemplate(id: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/plan/templates/${encodeURIComponent(id)}`);
  }

  /**
   * Fire-and-forget effectiveness metric: the dispatcher copied a plan's click card. Status-only —
   * no plan body, no PII — so leadership can see how many generated plans reached the paste step.
   */
  recordClickCardCopied(corridorCode: string, siblingCount: number): Observable<void> {
    return this.http.post<void>(`${this.base}/plan/metrics/click-card-copied`, {
      corridorCode,
      siblingCount,
    });
  }
}
