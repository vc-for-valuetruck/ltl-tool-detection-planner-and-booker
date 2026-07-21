import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { RUNTIME_CONFIG } from '../../runtime-config';
import { LaredoArrivalsBoard } from './arrivals.models';
import { ConsolidationCandidateResponse } from './consolidation.models';
import {
  DockCombineMetric,
  DockCombineRequest,
  DockCombineResponse,
  DockNotificationResult,
  DockUndoRequest,
  DockUndoResponse,
  DockWarehousesResponse,
} from './dock.models';

/**
 * Client for the Phase 2.5 Dock mode API. Read-only against Alvys — arrivals, candidates and the
 * combined plan are derived from live Alvys reads or static config; the one state-changing call
 * (`combine`) records an internal audit only (`AlvysWriteback = NotPerformed`), never an Alvys write.
 */
@Injectable({ providedIn: 'root' })
export class DockService {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(RUNTIME_CONFIG).apiBaseUrl}/ltl/dock`;

  /** The configured yards a dock worker can pick. Static config; safe to cache for the session. */
  getWarehouses(): Observable<DockWarehousesResponse> {
    return this.http.get<DockWarehousesResponse>(`${this.base}/warehouses`);
  }

  /** Trucks/loads at or inbound to a warehouse on a day (default today). Reuses the Arrivals Board. */
  getArrivals(warehouse: string, date?: string): Observable<LaredoArrivalsBoard> {
    let params = new HttpParams().set('warehouse', warehouse);
    if (date) params = params.set('date', date);
    return this.http.get<LaredoArrivalsBoard>(`${this.base}/arrivals`, { params });
  }

  /** Eligible sibling suggestions for a chosen parent load. */
  getCandidates(
    parentLoadId: string,
    corridor = 'LAREDO_TO_DALLAS',
  ): Observable<ConsolidationCandidateResponse> {
    const params = new HttpParams().set('parentLoadId', parentLoadId).set('corridor', corridor);
    return this.http.get<ConsolidationCandidateResponse>(`${this.base}/candidates`, { params });
  }

  /**
   * Combines a parent + siblings into a plan preview and records the internal audit. Returns the
   * plan (click card + combined economics) and the audit. Read-only against Alvys.
   */
  combine(request: DockCombineRequest): Observable<DockCombineResponse> {
    return this.http.post<DockCombineResponse>(`${this.base}/combine`, request);
  }

  /**
   * Records a one-tap Undo of a just-committed combine. Writes a retraction audit entry only —
   * the combine wrote nothing to Alvys, so an undo reverses nothing there. Read-only against Alvys.
   */
  undo(request: DockUndoRequest): Observable<DockUndoResponse> {
    return this.http.post<DockUndoResponse>(`${this.base}/undo`, request);
  }

  /** Re-sends the combine notification (retry chip). Records no new audit. */
  renotify(request: DockCombineRequest): Observable<DockNotificationResult> {
    return this.http.post<DockNotificationResult>(`${this.base}/notify`, request);
  }

  /**
   * Fire-and-forget effectiveness metric (time-to-combine + tap count). Status-only, no PII; a
   * failure to record must never affect the dock worker, so callers ignore the result.
   */
  recordCombineMetric(metric: DockCombineMetric): Observable<void> {
    return this.http.post<void>(`${this.base}/metrics/combine`, metric);
  }
}
