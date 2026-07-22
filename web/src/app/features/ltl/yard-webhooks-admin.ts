import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { YardWebhooksService } from './yard-webhooks.service';
import { YardWebhookAdminView } from './yard-webhooks.models';

/**
 * Read-only admin panel for the inbound Yard webhook receiver (`/admin/yard/webhooks`), mirroring the
 * Alvys webhook admin listing. It shows recent Yard deliveries (TruckArrived / LoadReleased /
 * LtlDraftCreated) and their background-processing state, plus an honest snapshot of receiver
 * configuration (enabled flag, whether a signing secret is present, tolerance window). It never posts
 * to the receiver — that endpoint is HMAC-signed machine-to-machine — and surfaces no secret values.
 */
@Component({
  selector: 'app-yard-webhooks-admin',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './yard-webhooks-admin.html',
  styleUrls: ['./yard-webhooks-admin.css'],
})
export class YardWebhooksAdmin {
  private readonly service = inject(YardWebhooksService);

  protected readonly webhooks = signal<YardWebhookAdminView | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    this.load();
  }

  /** Loads the read-only webhook admin snapshot (recent received events + receiver config). */
  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.service.webhookEvents(50).subscribe({
      next: (view) => {
        this.webhooks.set(view);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(this.describe('Webhook events', err));
        this.loading.set(false);
      },
    });
  }

  protected stateClass(state: string): string {
    switch (state) {
      case 'Processed':
        return 'badge badge-muted';
      case 'Failed':
        return 'badge badge-block';
      default:
        return 'badge badge-unsupported';
    }
  }

  private describe(what: string, err: { status?: number; statusText?: string }): string {
    return `${what} failed: ${err.status ?? ''} ${err.statusText ?? ''}`.trim();
  }
}
