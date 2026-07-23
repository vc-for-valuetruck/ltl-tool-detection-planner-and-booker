import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { RUNTIME_CONFIG } from '../../runtime-config';
import { YardWebhookAdminView } from './yard-webhooks.models';

/**
 * Client for the read-only Yard webhook admin listing (`GET /api/yard/webhooks/events`). Mirrors the
 * Alvys webhook admin snapshot: recent received deliveries + honest receiver configuration state. It
 * never posts to the receiver — that endpoint is machine-to-machine (HMAC-signed), never called here.
 */
@Injectable({ providedIn: 'root' })
export class YardWebhooksService {
  private readonly http = inject(HttpClient);
  private readonly apiBaseUrl = inject(RUNTIME_CONFIG).apiBaseUrl;

  /** The read-only webhook admin snapshot: recent received events + receiver configuration state. */
  webhookEvents(max?: number): Observable<YardWebhookAdminView> {
    const url =
      max != null
        ? `${this.apiBaseUrl}/yard/webhooks/events?max=${max}`
        : `${this.apiBaseUrl}/yard/webhooks/events`;
    return this.http.get<YardWebhookAdminView>(url);
  }
}
