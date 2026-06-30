import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { RUNTIME_CONFIG } from '../../runtime-config';
import {
  BillingBadge,
  BillingReadinessResult,
  LtlLoadSummary,
  LtlSearchQuery,
  LtlSearchResponse,
  MatchResult,
} from './ltl.models';

/**
 * Client for the read-only LTL decision-support API. Bearer tokens are attached by the MSAL
 * interceptor when auth is configured; this service only shapes requests/queries.
 */
@Injectable({ providedIn: 'root' })
export class LtlService {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(RUNTIME_CONFIG).apiBaseUrl}/ltl`;

  search(query: LtlSearchQuery): Observable<LtlSearchResponse> {
    return this.http.get<LtlSearchResponse>(`${this.base}/search`, { params: toParams(query) });
  }

  getLoad(idOrNumber: string): Observable<LtlLoadSummary> {
    return this.http.get<LtlLoadSummary>(`${this.base}/loads/${encodeURIComponent(idOrNumber)}`);
  }

  getMatches(idOrNumber: string, top = 5): Observable<MatchResult[]> {
    return this.http.get<MatchResult[]>(
      `${this.base}/loads/${encodeURIComponent(idOrNumber)}/matches`,
      { params: new HttpParams().set('top', top) },
    );
  }

  getBillingReadiness(idOrNumber: string): Observable<BillingReadinessResult> {
    return this.http.get<BillingReadinessResult>(
      `${this.base}/loads/${encodeURIComponent(idOrNumber)}/billing-readiness`,
    );
  }

  billingWorklist(badge?: BillingBadge): Observable<LtlLoadSummary[]> {
    let params = new HttpParams();
    if (badge) params = params.set('badge', badge);
    return this.http.get<LtlLoadSummary[]>(`${this.base}/billing/worklist`, { params });
  }

  exceptions(): Observable<LtlLoadSummary[]> {
    return this.http.get<LtlLoadSummary[]>(`${this.base}/exceptions`);
  }
}

/** Flattens the query object into HttpParams, skipping empty values and repeating arrays. */
function toParams(query: LtlSearchQuery): HttpParams {
  let params = new HttpParams();
  for (const [key, value] of Object.entries(query)) {
    if (value === undefined || value === null || value === '') continue;
    if (Array.isArray(value)) {
      for (const item of value) params = params.append(key, String(item));
    } else {
      params = params.set(key, String(value));
    }
  }
  return params;
}
