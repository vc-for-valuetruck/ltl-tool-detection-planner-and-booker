import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, retry, timer } from 'rxjs';
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
    return this.http
      .get<DispatchRecommendationsResponse>(`${this.base}/recommendations`, { params })
      // A cold Alvys read can hiccup transiently (gateway/5xx/network) while credentials are valid.
      // Retry twice with a short backoff so a demo/dispatcher isn't shown a false "no data" on the
      // first slow response. A 4xx (e.g. 404 unresolvable load) is deterministic — surface it now.
      .pipe(retry({ count: 2, delay: retryTransient }));
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

/**
 * Retry predicate for the recommendations read: back off ~400ms · attempt on a transient failure
 * (network error or 5xx), but re-throw a 4xx immediately (a client error like an unresolvable load
 * won't fix itself on retry, and retrying it only delays the honest message).
 */
function retryTransient(error: unknown, retryCount: number): Observable<number> {
  const status = error instanceof HttpErrorResponse ? error.status : 0;
  if (status >= 400 && status < 500) throw error;
  return timer(retryCount * 400);
}
