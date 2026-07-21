import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { LtlService } from './ltl.service';
import { LtlNav } from './ltl-nav';
import {
  LtlSurface,
  SignalSourceType,
  SignalStatus,
  SignalType,
  SignalView,
} from './signals.models';

/**
 * Signals panel (Phase 6 inbound). Paste a note / email / transcript, extract typed evidence-backed
 * signals, and accept or reject each one. Ingestion fails closed server-side: if the extractor fails
 * or a signal lacks a verbatim evidence quote, nothing is recorded and the error is shown here.
 *
 * Alvys posture: read-only. Accepting a signal annotates internal LTL surfaces only — it never writes
 * to Alvys. Every row states this explicitly so an operator is never misled.
 */
@Component({
  selector: 'app-ltl-signals',
  standalone: true,
  imports: [DatePipe, FormsModule, LtlNav],
  templateUrl: './ltl-signals.html',
  styleUrls: ['./ltl-signals.css'],
})
export class LtlSignals implements OnInit {
  private readonly ltl = inject(LtlService);

  protected readonly sourceTypes: SignalSourceType[] = ['note', 'email', 'transcript', 'call'];

  // Ingest form state.
  protected readonly sourceType = signal<SignalSourceType>('email');
  protected readonly sourceId = signal('');
  protected readonly loadNumber = signal('');
  protected readonly text = signal('');
  protected readonly ingesting = signal(false);
  protected readonly ingestError = signal<string | null>(null);
  protected readonly lastIngestCount = signal<number | null>(null);

  // Review-queue state.
  protected readonly items = signal<SignalView[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly statusFilter = signal<SignalStatus | ''>('');
  protected readonly extractorName = signal<string | null>(null);

  protected readonly hasItems = computed(() => this.items().length > 0);
  protected readonly canIngest = computed(
    () => this.text().trim().length > 0 && this.sourceId().trim().length > 0 && !this.ingesting(),
  );

  ngOnInit(): void {
    this.ltl.signalExtractor().subscribe({
      next: (s) => this.extractorName.set(s?.name ?? null),
      error: () => this.extractorName.set(null),
    });
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    const status = this.statusFilter();
    this.ltl.signals({ status: status || undefined, max: 100 }).subscribe({
      next: (rows) => {
        this.items.set(rows ?? []);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err?.error?.error ?? err?.message ?? "Couldn't reach the signals queue.");
        this.loading.set(false);
      },
    });
  }

  protected onStatusFilterChange(value: string): void {
    this.statusFilter.set((value as SignalStatus) || '');
    this.load();
  }

  protected ingest(): void {
    if (!this.canIngest()) return;
    this.ingesting.set(true);
    this.ingestError.set(null);
    this.lastIngestCount.set(null);
    this.ltl
      .ingestSignals({
        sourceType: this.sourceType(),
        sourceId: this.sourceId().trim(),
        text: this.text(),
        loadNumber: this.loadNumber().trim() || null,
      })
      .subscribe({
        next: (res) => {
          this.lastIngestCount.set(res?.count ?? 0);
          this.text.set('');
          this.ingesting.set(false);
          this.load();
        },
        error: (err) => {
          // Fail-closed: server recorded nothing. Surface the legible reason verbatim.
          this.ingestError.set(
            err?.error?.error ?? err?.message ?? 'Ingestion failed; nothing was recorded.',
          );
          this.ingesting.set(false);
        },
      });
  }

  protected accept(row: SignalView): void {
    this.ltl.acceptSignal(row.id).subscribe({ next: (u) => this.replace(u), error: () => this.load() });
  }

  protected reject(row: SignalView): void {
    this.ltl.rejectSignal(row.id).subscribe({ next: (u) => this.replace(u), error: () => this.load() });
  }

  private replace(updated: SignalView): void {
    this.items.update((rows) => rows.map((r) => (r.id === updated.id ? updated : r)));
  }

  protected typeLabel(type: SignalType): string {
    switch (type) {
      case 'AccessorialEvidence':
        return 'Accessorial evidence';
      case 'ConsolidationOpportunity':
        return 'Consolidation opportunity';
      case 'CustomerVisibilityPosture':
        return 'Customer posture';
      case 'BillingRisk':
        return 'Billing risk';
      case 'DelayedLoad':
        return 'Delayed load';
      case 'MissingDocs':
        return 'Missing docs';
      case 'NewLane':
        return 'New lane';
      case 'NewSite':
        return 'New site';
      case 'EquipmentNeed':
        return 'Equipment need';
      case 'ContractSignal':
        return 'Contract signal';
      case 'CompetitiveIntel':
        return 'Competitive intel';
      case 'ServiceIssue':
        return 'Service issue';
      case 'ContactSuggestion':
        return 'Contact suggestion';
      default:
        return 'Other';
    }
  }

  protected surfaceLabel(surface: LtlSurface): string {
    switch (surface) {
      case 'SearchFilter':
        return 'Search filter';
      case 'BillingWorklistBadge':
        return 'Billing badge';
      case 'Exception':
        return 'Exception';
      case 'MatchWarning':
        return 'Match warning';
      case 'SavedView':
        return 'Saved view';
      case 'AuditNote':
        return 'Audit note';
      case 'NextBestAction':
        return 'Next-best-action';
      default:
        return surface;
    }
  }

  protected statusClass(status: SignalStatus): string {
    switch (status) {
      case 'Accepted':
        return 'pill pill-ok';
      case 'Rejected':
        return 'pill pill-muted';
      case 'Pending':
      default:
        return 'pill pill-warn';
    }
  }
}
