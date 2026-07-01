import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { RUNTIME_CONFIG } from '../../runtime-config';
import { AlvysTendersResponse, TenderSearchRequest } from './alvys-tenders.models';

/**
 * Client for the read-only Alvys tender boundary (`/api/alvys/tenders/*`). This is a
 * pass-through read of inbound EDI/tender offers, not a decision-support projection — the
 * Tenders board is the one screen in this tool intentionally close to the raw Alvys shape,
 * because accept/reject is a fast operational decision, not something that benefits from
 * additional normalization.
 */
@Injectable({ providedIn: 'root' })
export class AlvysTendersService {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(RUNTIME_CONFIG).apiBaseUrl}/alvys/tenders`;

  search(request: TenderSearchRequest): Observable<AlvysTendersResponse> {
    return this.http.post<AlvysTendersResponse>(`${this.base}/search`, request);
  }
}
