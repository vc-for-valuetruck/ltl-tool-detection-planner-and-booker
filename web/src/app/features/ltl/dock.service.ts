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
  DockOpportunitiesResponse,
  DockPresenceResponse,
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
   * Downloads the combined BOL packet / dock manifest as a server-side PDF (the "Download PDF"
   * companion to the print view). Read-only against Alvys — rebuilds the plan and renders it,
   * records no audit and sends no notification. Returns the raw PDF blob for the caller to save.
   */
  downloadBolPacket(request: DockCombineRequest): Observable<Blob> {
    return this.http.post(`${this.base}/bol-packet.pdf`, request, { responseType: 'blob' });
  }

  /**
   * Fire-and-forget effectiveness metric (time-to-combine + tap count). Status-only, no PII; a
   * failure to record must never affect the dock worker, so callers ignore the result.
   */
  recordCombineMetric(metric: DockCombineMetric): Observable<void> {
    return this.http.post<void>(`${this.base}/metrics/combine`, metric);
  }

  /**
   * Yard presence for a proposed pairing, for the Review-step chip. Read-only and honest — a grey
   * "unavailable" shape comes back when the Yard integration is off or the yard is unreachable;
   * presence is never fabricated into a pass. Alvys stays authoritative.
   */
  getPresence(tractor?: string, trailer?: string, driverId?: string): Observable<DockPresenceResponse> {
    let params = new HttpParams();
    if (tractor) params = params.set('tractor', tractor);
    if (trailer) params = params.set('trailer', trailer);
    if (driverId) params = params.set('driverId', driverId);
    return this.http.get<DockPresenceResponse>(`${this.base}/presence`, { params });
  }

  /**
   * Yard-originated LTL consolidation opportunities (from `LtlDraftCreated` webhooks), newest first.
   * Inbound suggestions only — the dock acts on them inside its own Alvys-backed combine flow.
   */
  getOpportunities(max = 50): Observable<DockOpportunitiesResponse> {
    const params = new HttpParams().set('max', String(max));
    return this.http.get<DockOpportunitiesResponse>(`${this.base}/opportunities`, { params });
  }
}
