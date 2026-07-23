import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { RUNTIME_CONFIG } from '../../runtime-config';
import {
  DispatchAssembleRequest,
  DispatchAssembly,
  DispatchRecommendationsQuery,
  DispatchRecommendationsResponse,
} from './dispatch-assist.models';

/**
 * Client for the Dispatch Assist API ("inform and assemble the right driver and truck"). Read-only
 * against Alvys — recommendations are assembled from live Alvys reads; `assemble` records an internal
 * decision only (`alvysWriteback = "NotPerformed"`) and fires the flag-gated notify step. Never an
 * Alvys write.
 */
@Injectable({ providedIn: 'root' })
export class DispatchAssistService {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(RUNTIME_CONFIG).apiBaseUrl}/ltl/dispatch`;

  /** Ranked, explainable candidates for a load (by `loadId`) or an ad-hoc origin/destination lane. */
  recommendations(query: DispatchRecommendationsQuery): Observable<DispatchRecommendationsResponse> {
    let params = new HttpParams();
    if (query.loadId) params = params.set('loadId', query.loadId);
    if (query.originCity) params = params.set('originCity', query.originCity);
    if (query.originState) params = params.set('originState', query.originState);
    if (query.destinationCity) params = params.set('destinationCity', query.destinationCity);
    if (query.destinationState) params = params.set('destinationState', query.destinationState);
    if (query.top && query.top > 0) params = params.set('top', String(query.top));
    return this.http.get<DispatchRecommendationsResponse>(`${this.base}/recommendations`, { params });
  }

  /** Records the chosen driver+truck+trailer app-side and fires the notify step. Never writes Alvys. */
  assemble(request: DispatchAssembleRequest): Observable<DispatchAssembly> {
    return this.http.post<DispatchAssembly>(`${this.base}/assemble`, request);
  }

  /** Recent app-side assembly records (newest first), optionally filtered to one load. */
  assemblies(loadId?: string, max = 50): Observable<readonly DispatchAssembly[]> {
    let params = new HttpParams().set('max', String(max));
    if (loadId) params = params.set('loadId', loadId);
    return this.http.get<readonly DispatchAssembly[]>(`${this.base}/assemblies`, { params });
  }
}
