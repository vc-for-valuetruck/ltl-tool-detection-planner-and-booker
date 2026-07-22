import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams, HttpResponse } from '@angular/common/http';
import { Observable } from 'rxjs';
import { RUNTIME_CONFIG } from '../../../runtime-config';

/**
 * AI consolidation narrative for a plan (issue #151). Three short review prompts plus supporting
 * citations, generated server-side and gated by the `AI:NarrativeEnabled` flag. Read-only — this
 * is decision-support copy about a consolidation plan, never an Alvys read or write.
 */
export interface AiNarrative {
  whyReview: string;
  whatToVerify: string;
  nextAction: string;
  citations: string[];
}

/**
 * Client for the AI narrative endpoint. The endpoint itself respects the server-side feature flag
 * (returning 404 `disabled` when off) and its own availability (503 `ai-unavailable`), so the SPA
 * never needs its own flag lookup — it just consumes the response and collapses to nothing on any
 * non-200. Bearer tokens are attached by the MSAL interceptor when auth is configured.
 */
@Injectable({ providedIn: 'root' })
export class AiNarrativeService {
  private readonly http = inject(HttpClient);
  private readonly apiBaseUrl = inject(RUNTIME_CONFIG).apiBaseUrl;

  /**
   * Full response (so the caller can read the `X-Ai-Source` / `X-Ai-Cached` provenance headers on
   * a 200). Errors — 404 disabled / plan-not-found, 503 ai-unavailable, network, timeout — surface
   * as the observable's error channel for the component to swallow.
   */
  narrative(planId: string): Observable<HttpResponse<AiNarrative>> {
    return this.http.get<AiNarrative>(`${this.apiBaseUrl}/ai/consolidation/narrative`, {
      params: new HttpParams().set('planId', planId),
      observe: 'response',
    });
  }
}
