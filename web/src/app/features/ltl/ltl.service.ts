import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { RUNTIME_CONFIG } from '../../runtime-config';
import {
  AccessorialReviewContext,
  AccessorialReviewResult,
  AssignmentAudit,
  AssignmentAuditQuery,
  AssignmentBatchValidateRequest,
  AssignmentBatchValidateResponse,
  AssignmentRequest,
  AssignmentValidationResult,
  BillingBadge,
  BillingReadinessResult,
  CapacitySnapshot,
  LaneRateContext,
  LtlLoadSummary,
  LtlSearchQuery,
  LtlSearchResponse,
  MarginRollupResponse,
  MatchResult,
  RollupGroupBy,
  SavedView,
  SavedViewCollection,
  SavedViewRequest,
} from './ltl.models';
import { NotificationFeedResponse } from './notifications.models';
import { LaredoArrivalsBoard } from './arrivals.models';
import { YardArtifactQuery, YardArtifactView } from './yard-artifacts.models';
import {
  SignalExtractorStatus,
  SignalIngestRequest,
  SignalIngestResponse,
  SignalStatus,
  SignalView,
} from './signals.models';
import {
  AlvysLoadDocument,
  BolExtractorStatus,
  BolFieldSuggestionView,
  BolReadResponse,
  BolSuggestionStatus,
} from './bol.models';

/**
 * Client for the read-only LTL decision-support API. Bearer tokens are attached by the MSAL
 * interceptor when auth is configured; this service only shapes requests/queries.
 */
@Injectable({ providedIn: 'root' })
export class LtlService {
  private readonly http = inject(HttpClient);
  private readonly apiBaseUrl = inject(RUNTIME_CONFIG).apiBaseUrl;
  private readonly base = `${this.apiBaseUrl}/ltl`;

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

  /**
   * Accessorial-signal review for a single load: keyword-extracted (and optionally
   * AI-supplemented) evidence of unbilled accessorials from Alvys notes and document metadata.
   * Returns `evaluated: false` when the load has no notes or documents
   * (not evaluated ≠ clean — never treat an empty signal list as "nothing to bill").
   * Read-only: nothing is written back to Alvys.
   */
  getAccessorialSignals(idOrNumber: string): Observable<AccessorialReviewContext> {
    return this.http.get<AccessorialReviewContext>(
      `${this.base}/loads/${encodeURIComponent(idOrNumber)}/accessorial-signals`,
    );
  }

  /**
   * Deterministic accessorial-review candidates for a single load (Phase 3.5): stop-timing
   * detention / layover / weekend / reconsignment signals plus note/document keyword signals,
   * each citing its Alvys source id. Returns `evaluated: false` when the load has no trip stops
   * and no notes/documents (not evaluated ≠ clean). Read-only; no dollar value is computed.
   */
  getAccessorialReview(idOrNumber: string): Observable<AccessorialReviewResult> {
    return this.http.get<AccessorialReviewResult>(
      `${this.base}/loads/${encodeURIComponent(idOrNumber)}/accessorial-review`,
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

  /**
   * Read-only margin/exception rollup grouped by customer, rep, or lane. Same normalized load set
   * as the billing worklist, aggregated. No external BI connection.
   */
  marginRollup(groupBy: RollupGroupBy): Observable<MarginRollupResponse> {
    return this.http.get<MarginRollupResponse>(`${this.base}/reporting/margin-rollup`, {
      params: new HttpParams().set('groupBy', groupBy),
    });
  }

  /**
   * Absolute URL for the CSV rendering of the margin rollup — for a direct browser download, or
   * for an external reporting tool (e.g. Power BI's Text/CSV connector) to pull Alvys-derived
   * data straight from this tool. Same auth and same read-only data as `marginRollup`.
   */
  marginRollupExportUrl(groupBy: RollupGroupBy): string {
    return `${this.base}/reporting/margin-rollup/export?groupBy=${encodeURIComponent(groupBy)}`;
  }

  /**
   * Live "Capacity today" snapshot: active trucks, trailer pool by equipment type, and in-transit
   * trips. Read-only; every count is a live Alvys read, truncation reported honestly.
   */
  capacityToday(): Observable<CapacitySnapshot> {
    return this.http.get<CapacitySnapshot>(`${this.base}/capacity/today`);
  }

  /**
   * Laredo Arrivals Board (Phase 8.1): every truck/trailer scheduled to arrive at the Laredo yard
   * on the given day (default today), Dallas-bound first. Read-only; every value is a live Alvys
   * trip/stop read, truncation reported honestly.
   */
  arrivals(date?: string): Observable<LaredoArrivalsBoard> {
    let params = new HttpParams();
    if (date) params = params.set('date', date);
    return this.http.get<LaredoArrivalsBoard>(`${this.base}/arrivals`, { params });
  }

  /**
   * Recent lane rate context: revenue-per-mile spread across recently delivered loads on the same
   * origin→destination state pair. Recent tenant history, not a market rate. Read-only.
   */
  laneRate(originState: string, destinationState: string): Observable<LaneRateContext> {
    return this.http.get<LaneRateContext>(`${this.base}/lane-rate`, {
      params: new HttpParams().set('originState', originState).set('destinationState', destinationState),
    });
  }

  /**
   * Recent workflow notifications (newest first) plus lifetime count and per-channel config
   * state. Read-only; fired by the server-side trigger engine, never written to Alvys.
   */
  notifications(max = 50): Observable<NotificationFeedResponse> {
    return this.http.get<NotificationFeedResponse>(`${this.base}/notifications`, {
      params: new HttpParams().set('max', max),
    });
  }

  /**
   * Yard artifacts (Phase 8.2) matching any of load number / truck unit / trailer unit / yard.
   * Our internal dock-inspection data (SQL + file store) — never read from or written to Alvys.
   */
  yardArtifacts(query: YardArtifactQuery): Observable<YardArtifactView[]> {
    let params = new HttpParams();
    if (query.loadNumber) params = params.set('loadNumber', query.loadNumber);
    if (query.truckUnit) params = params.set('truckUnit', query.truckUnit);
    if (query.trailerUnit) params = params.set('trailerUnit', query.trailerUnit);
    if (query.yard) params = params.set('yard', query.yard);
    if (query.max) params = params.set('max', query.max);
    return this.http.get<YardArtifactView[]>(`${this.base}/yard-artifacts`, { params });
  }

  /** Absolute URL for streaming a stored artifact photo/PDF (inline gallery / PDF download). */
  yardArtifactFileUrl(artifactId: string, fileId: string): string {
    return `${this.base}/yard-artifacts/${encodeURIComponent(artifactId)}/files/${encodeURIComponent(fileId)}`;
  }

  /**
   * Phase 6 inbound signals: extract typed, evidence-backed signals from a note/email/transcript.
   * Fails closed server-side (HTTP 422 with a legible error) if extraction fails or a signal lacks a
   * verbatim evidence quote — nothing is recorded. Internal data; never writes to Alvys.
   */
  ingestSignals(request: SignalIngestRequest): Observable<SignalIngestResponse> {
    return this.http.post<SignalIngestResponse>(`${this.base}/signals/ingest`, request);
  }

  /** Recorded signals, newest first, filterable by status / source type / load number. */
  signals(opts: { status?: SignalStatus; sourceType?: string; loadNumber?: string; max?: number } = {}):
    Observable<SignalView[]> {
    let params = new HttpParams();
    if (opts.status) params = params.set('status', opts.status);
    if (opts.sourceType) params = params.set('sourceType', opts.sourceType);
    if (opts.loadNumber) params = params.set('loadNumber', opts.loadNumber);
    if (opts.max) params = params.set('max', opts.max);
    return this.http.get<SignalView[]>(`${this.base}/signals`, { params });
  }

  /** Honest snapshot of which extractor is active (deterministic keyword vs. an LLM). */
  signalExtractor(): Observable<SignalExtractorStatus> {
    return this.http.get<SignalExtractorStatus>(`${this.base}/signals/extractor`);
  }

  /** Accept a signal — annotates the suggested internal surface. Never writes to Alvys. */
  acceptSignal(id: string): Observable<SignalView> {
    return this.http.post<SignalView>(`${this.base}/signals/${encodeURIComponent(id)}/accept`, {});
  }

  /** Reject a signal — kept for audit, annotates nothing. */
  rejectSignal(id: string): Observable<SignalView> {
    return this.http.post<SignalView>(`${this.base}/signals/${encodeURIComponent(id)}/reject`, {});
  }

  validateAssignment(
    idOrNumber: string,
    request: AssignmentRequest,
  ): Observable<AssignmentValidationResult> {
    return this.http.post<AssignmentValidationResult>(
      `${this.base}/loads/${encodeURIComponent(idOrNumber)}/assign/validate`,
      request,
    );
  }

  /** Records an internal (non-Alvys) assignment decision. 422 carries the blocking validation. */
  assign(idOrNumber: string, request: AssignmentRequest): Observable<AssignmentAudit> {
    return this.http.post<AssignmentAudit>(
      `${this.base}/loads/${encodeURIComponent(idOrNumber)}/assign`,
      request,
    );
  }

  assignments(idOrNumber: string): Observable<AssignmentAudit[]> {
    return this.http.get<AssignmentAudit[]>(
      `${this.base}/loads/${encodeURIComponent(idOrNumber)}/assignments`,
    );
  }

  /**
   * Cross-load assignment-decision history for the /ltl/assignments page, filtered by recording
   * user, UTC day and/or typed override reason. Read-only; every row stays `NotPerformed` against
   * Alvys.
   */
  assignmentHistory(query: AssignmentAuditQuery = {}): Observable<AssignmentAudit[]> {
    let params = new HttpParams();
    if (query.user) params = params.set('user', query.user);
    if (query.day) params = params.set('day', query.day);
    if (query.reasonType) params = params.set('reasonType', query.reasonType);
    if (query.max) params = params.set('max', String(query.max));
    return this.http.get<AssignmentAudit[]>(`${this.base}/assignments`, { params });
  }

  /** Preflight batch validate: dry-runs validation across many proposed assignments. Records nothing. */
  validateAssignmentBatch(
    request: AssignmentBatchValidateRequest,
  ): Observable<AssignmentBatchValidateResponse> {
    return this.http.post<AssignmentBatchValidateResponse>(
      `${this.base}/assign/validate-batch`,
      request,
    );
  }

  /**
   * Read-only listing of the Alvys documents attached to a load (BOL / rate confirmation / POD).
   * Powers the "Read BOL" picker on the Documents tab. Server-side proxy — Alvys credentials never
   * reach the browser.
   */
  loadDocuments(loadNumber: string): Observable<AlvysLoadDocument[]> {
    return this.http.get<AlvysLoadDocument[]>(
      `${this.apiBaseUrl}/alvys/loads/${encodeURIComponent(loadNumber)}/documents`,
    );
  }

  /**
   * Reads one BOL document and returns suggested fields (pallet/piece/weight/class/commodity/hazmat),
   * each with a verbatim evidence quote. Suggest-only: nothing is applied, nothing is written to
   * Alvys. Fails closed server-side (HTTP 422 with a legible error) when the document can't be
   * fetched, no text can be extracted, or a field lacks evidence — nothing is stored in that case.
   */
  readBol(loadNumber: string, documentId: string): Observable<BolReadResponse> {
    return this.http.post<BolReadResponse>(
      `${this.base}/loads/${encodeURIComponent(loadNumber)}/bol/documents/${encodeURIComponent(documentId)}/read`,
      {},
    );
  }

  /** Stored BOL suggestions, newest first, filterable by load number / status. */
  bolSuggestions(opts: { loadNumber?: string; status?: BolSuggestionStatus; max?: number } = {}):
    Observable<BolFieldSuggestionView[]> {
    let params = new HttpParams();
    if (opts.loadNumber) params = params.set('loadNumber', opts.loadNumber);
    if (opts.status) params = params.set('status', opts.status);
    if (opts.max) params = params.set('max', String(opts.max));
    return this.http.get<BolFieldSuggestionView[]>(`${this.base}/bol/suggestions`, { params });
  }

  /** Honest snapshot of which text + field extractor is active (deterministic vs. cloud OCR stub). */
  bolExtractor(): Observable<BolExtractorStatus> {
    return this.http.get<BolExtractorStatus>(`${this.base}/bol/extractor`);
  }

  /** Accept a BOL suggestion — annotates internal surfaces only, audited. Never writes to Alvys. */
  acceptBolSuggestion(id: string): Observable<BolFieldSuggestionView> {
    return this.http.post<BolFieldSuggestionView>(
      `${this.base}/bol/suggestions/${encodeURIComponent(id)}/accept`, {},
    );
  }

  /** Reject a BOL suggestion — kept for audit, annotates nothing. */
  rejectBolSuggestion(id: string): Observable<BolFieldSuggestionView> {
    return this.http.post<BolFieldSuggestionView>(
      `${this.base}/bol/suggestions/${encodeURIComponent(id)}/reject`, {},
    );
  }

  /** Built-in presets + the dispatcher's own saved views. Tool-local; never touches Alvys. */
  listSavedViews(): Observable<SavedViewCollection> {
    return this.http.get<SavedViewCollection>(`${this.base}/saved-views`);
  }

  createSavedView(request: SavedViewRequest): Observable<SavedView> {
    return this.http.post<SavedView>(`${this.base}/saved-views`, request);
  }

  updateSavedView(id: string, request: SavedViewRequest): Observable<SavedView> {
    return this.http.put<SavedView>(
      `${this.base}/saved-views/${encodeURIComponent(id)}`,
      request,
    );
  }

  deleteSavedView(id: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/saved-views/${encodeURIComponent(id)}`);
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
