import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { LtlService } from './ltl.service';
import { LtlNav } from './ltl-nav';
import {
  NotificationChannelStatus,
  NotificationDeliveryState,
  NotificationEvent,
  NotificationStage,
} from './notifications.models';

/**
 * Notifications feed (Phase 6). Read-only view over `GET /api/ltl/notifications` — the fired
 * workflow triggers that align "multiple people at once" on a trip. Delivery states are shown
 * honestly (Delivered / Pending / Not configured / Failed); a not-configured external channel is
 * never dressed up as delivered. Nothing here writes to Alvys.
 */
@Component({
  selector: 'app-ltl-notifications',
  standalone: true,
  imports: [DatePipe, RouterLink, LtlNav],
  templateUrl: './ltl-notifications.html',
  styleUrls: ['./ltl-notifications.css'],
})
export class LtlNotifications implements OnInit {
  private readonly ltl = inject(LtlService);

  protected readonly items = signal<NotificationEvent[]>([]);
  protected readonly channels = signal<NotificationChannelStatus[]>([]);
  protected readonly total = signal(0);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected readonly hasItems = computed(() => this.items().length > 0);

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.ltl.notifications(100).subscribe({
      next: (feed) => {
        this.items.set(feed?.items ?? []);
        this.channels.set(feed?.channels ?? []);
        this.total.set(feed?.total ?? 0);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err?.error?.error ?? err?.message ?? "Couldn't reach the notifications feed.");
        this.loading.set(false);
      },
    });
  }

  /** Human label for a workflow stage. */
  protected stageLabel(stage: NotificationStage): string {
    switch (stage) {
      case 'ConsolidationPlanCreated':
        return 'Consolidation plan';
      case 'ClickCardGenerated':
        return 'Click card';
      case 'AssignmentConfirmed':
        return 'Assignment';
      case 'PickupEvent':
        return 'Pickup';
      case 'DeliveryEvent':
        return 'Delivery';
      case 'BillingReady':
        return 'Billing ready';
      case 'Invoiced':
        return 'Invoiced';
      case 'ExceptionRaised':
        return 'Exception';
      default:
        return stage;
    }
  }

  /** Short label for a channel config status pill. */
  protected channelLabel(channel: string): string {
    return channel === 'InApp' ? 'In-app' : channel;
  }

  protected deliveryClass(state: NotificationDeliveryState): string {
    switch (state) {
      case 'Delivered':
        return 'pill pill-ok';
      case 'Pending':
        return 'pill pill-warn';
      case 'Failed':
        return 'pill pill-danger';
      case 'NotConfigured':
      default:
        return 'pill pill-muted';
    }
  }

  protected deliveryLabel(state: NotificationDeliveryState): string {
    return state === 'NotConfigured' ? 'Not configured' : state;
  }
}
