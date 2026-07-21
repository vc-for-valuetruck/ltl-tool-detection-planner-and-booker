import { Component, computed, inject, input, signal } from '@angular/core';
import { DatePipe, JsonPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { AlvysOpsService } from './alvys-ops.service';
import {
  AlvysOperationEligibility,
  AlvysOperationOutcome,
  AlvysOperationRecordView,
  AlvysReadinessStatus,
  AlvysWebhookAdminView,
} from './alvys-ops.models';

/**
 * Operational-readiness panel for the Assign/Bill drawer. It surfaces the Alvys writeback posture
 * (audit-only / simulation-only / sandbox-eligible), the explicit blockers preventing live sandbox
 * execution, and a dry-run payload preview for the load-note operation. It never performs a live
 * Alvys mutation: dry-run and execute both return an audit/simulation/unsupported outcome.
 */
@Component({
  selector: 'app-alvys-ops-panel',
  standalone: true,
  imports: [FormsModule, DatePipe, JsonPipe],
  templateUrl: './alvys-ops-panel.html',
  styleUrls: ['./alvys-ops-panel.css'],
})
export class AlvysOpsPanel {
  private readonly ops = inject(AlvysOpsService);

  /** The load number the note dry-run targets (the selected load in the drawer). */
  readonly loadNumber = input<string | null>(null);

  protected readonly status = signal<AlvysReadinessStatus | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly probing = signal(false);

  protected readonly noteText = signal('');
  protected readonly dryRunning = signal(false);
  protected readonly outcome = signal<AlvysOperationOutcome | null>(null);
  protected readonly outcomeError = signal<string | null>(null);

  protected readonly history = signal<AlvysOperationRecordView[]>([]);
  protected readonly historyLoading = signal(false);
  protected readonly historyError = signal<string | null>(null);

  protected readonly webhooks = signal<AlvysWebhookAdminView | null>(null);
  protected readonly webhooksLoading = signal(false);
  protected readonly webhooksError = signal<string | null>(null);

  /** Headline posture for the panel banner. */
  protected readonly posture = computed<'audit' | 'simulation' | 'sandbox' | 'unknown'>(() => {
    const s = this.status();
    if (!s) return 'unknown';
    if (s.sandboxExecutionConfigured) return 'sandbox';
    if (s.writebackMode === 'Simulation') return 'simulation';
    return 'audit';
  });

  constructor() {
    this.load();
    this.loadHistory();
    this.loadWebhooks();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.ops.status().subscribe({
      next: (s) => {
        this.status.set(s);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(this.describe('Readiness', err));
        this.loading.set(false);
      },
    });
  }

  /** Issues the opt-in bounded read probe and refreshes the readiness snapshot. */
  protected runProbe(): void {
    this.probing.set(true);
    this.ops.probe().subscribe({
      next: (s) => {
        this.status.set(s);
        this.probing.set(false);
      },
      error: (err) => {
        this.error.set(this.describe('Probe', err));
        this.probing.set(false);
      },
    });
  }

  /** Previews the create-load-note payload for the selected load. Never sends to Alvys. */
  protected dryRunNote(): void {
    const load = this.loadNumber();
    if (!load || !this.noteText().trim()) return;
    this.dryRunning.set(true);
    this.outcomeError.set(null);
    this.ops
      .dryRun('create-load-note', { loadNumber: load, noteText: this.noteText().trim() })
      .subscribe({
        next: (r) => {
          this.outcome.set(r.outcome);
          this.dryRunning.set(false);
          this.loadHistory();
        },
        error: (err) => {
          this.outcome.set(null);
          this.outcomeError.set(this.describe('Dry run', err));
          this.dryRunning.set(false);
        },
      });
  }

  /** Loads the current owner's recent operation history (audit/outbox), newest first. */
  protected loadHistory(): void {
    this.historyLoading.set(true);
    this.historyError.set(null);
    this.ops.history(25).subscribe({
      next: (records) => {
        this.history.set(records);
        this.historyLoading.set(false);
      },
      error: (err) => {
        this.historyError.set(this.describe('History', err));
        this.historyLoading.set(false);
      },
    });
  }

  /** Loads the read-only webhook admin snapshot (recent received events + receiver config). */
  protected loadWebhooks(): void {
    this.webhooksLoading.set(true);
    this.webhooksError.set(null);
    this.ops.webhookEvents(25).subscribe({
      next: (view) => {
        this.webhooks.set(view);
        this.webhooksLoading.set(false);
      },
      error: (err) => {
        this.webhooksError.set(this.describe('Webhook events', err));
        this.webhooksLoading.set(false);
      },
    });
  }

  protected webhookStateClass(state: string): string {
    switch (state) {
      case 'Processed':
        return 'badge badge-muted';
      case 'Failed':
        return 'badge badge-block';
      default:
        return 'badge badge-unsupported';
    }
  }

  protected statusClass(status: AlvysOperationRecordView['status']): string {
    switch (status) {
      case 'Blocked':
        return 'badge badge-block';
      case 'Unsupported':
        return 'badge badge-unsupported';
      default:
        return 'badge badge-muted';
    }
  }

  protected eligibilityClass(eligibility: AlvysOperationEligibility): string {
    switch (eligibility) {
      case 'SandboxEligible':
        return 'elig elig-ok';
      case 'SimulationOnly':
        return 'elig elig-sim';
      case 'Unsupported':
        return 'elig elig-unsupported';
      default:
        return 'elig elig-audit';
    }
  }

  protected eligibilityText(eligibility: AlvysOperationEligibility): string {
    switch (eligibility) {
      case 'SandboxEligible':
        return 'Sandbox eligible';
      case 'SimulationOnly':
        return 'Simulation only';
      case 'Unsupported':
        return 'Unsupported';
      default:
        return 'Audit only';
    }
  }

  private describe(what: string, err: { status?: number; statusText?: string }): string {
    return `${what} failed: ${err.status ?? ''} ${err.statusText ?? ''}`.trim();
  }
}
