import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { RUNTIME_CONFIG } from '../../runtime-config';
import { DispatchPreferenceView } from './dispatch-planner.models';

/**
 * Client for the read-only dispatch-planner surface (Alvys Public API). Utilises the dispatch-
 * preference data #134's typed client already exposes to show "preferred" driver/truck/trailer chips
 * on the Dock review + Assignments surfaces. Never writes to Alvys; the endpoint returns an honest
 * unresolved view (all ids null) when Alvys has no preference on file or rate-limits.
 */
@Injectable({ providedIn: 'root' })
export class DispatchPlannerService {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(RUNTIME_CONFIG).apiBaseUrl}/ltl/dispatch-planner`;

  /** Preferred pairing for any combination of driver/truck/trailer id. Absent ids are simply omitted. */
  getPreferredPairing(ids: {
    driverId?: string | null;
    truckId?: string | null;
    trailerId?: string | null;
  }): Observable<DispatchPreferenceView> {
    let params = new HttpParams();
    if (ids.driverId) params = params.set('driverId', ids.driverId);
    if (ids.truckId) params = params.set('truckId', ids.truckId);
    if (ids.trailerId) params = params.set('trailerId', ids.trailerId);
    return this.http.get<DispatchPreferenceView>(`${this.base}/preferred-pairing`, { params });
  }
}
