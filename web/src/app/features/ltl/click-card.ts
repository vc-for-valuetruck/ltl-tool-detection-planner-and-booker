import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ConsolidationService } from './consolidation.service';
import { ConsolidationAuditRecord, ConsolidationPlanResponse } from './consolidation.models';

/**
 * Alvys Click Card (mockup screen 3): the sanctioned copy-pasteable text a dispatcher uses to
 * key the consolidation into Alvys by hand. Nothing on this screen writes to Alvys — the plan
 * is re-derived live via `ConsolidationService.buildPlan()` (same as Plan Detail) and, once
 * shown, is recorded as an audit entry via `recordPlanAudit()` so the uplift claim is backed by
 * a durable record even though Alvys writeback is NotPerformed in this phase.
 */
@Component({
  selector: 'app-click-card',
  standalone: true,
  imports: [CommonModule, RouterLink, DatePipe],
  templateUrl: './click-card.html',
  styleUrls: ['./click-card.css'],
})
export class ClickCard implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly consolidation = inject(ConsolidationService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly missingContext = signal(false);
  readonly plan = signal<ConsolidationPlanResponse | null>(null);
  readonly copyMessage = signal<string | null>(null);
  /** Carried through so the breadcrumb can link back to the exact same plan context. */
  readonly planDetailQueryParams = signal<Record<string, string>>({});

  readonly recordingAudit = signal(false);
  readonly auditError = signal<string | null>(null);
  readonly auditRecord = signal<ConsolidationAuditRecord | null>(null);

  ngOnInit(): void {
    const qp = this.route.snapshot.queryParamMap;
    const parentLoadId = qp.get('parent');
    const siblingsParam = qp.get('siblings');
    const corridor = qp.get('corridor') ?? undefined;

    if (!parentLoadId || !siblingsParam) {
      this.missingContext.set(true);
      this.loading.set(false);
      return;
    }

    this.planDetailQueryParams.set({
      parent: parentLoadId,
      siblings: siblingsParam,
      ...(corridor ? { corridor } : {}),
    });

    const siblingLoadIds = siblingsParam.split(',').filter(Boolean);

    this.consolidation.buildPlan({ parentLoadId, siblingLoadIds, corridorCode: corridor }).subscribe({
      next: (response) => {
        this.plan.set(response);
        this.loading.set(false);
        // The click card is only meaningful once it's actually surfaced to the dispatcher —
        // record the audit entry now so the uplift claim carries a timestamped record, matching
        // "the tool has recorded the plan as an audit entry" copy on this screen.
        this.recordAudit(parentLoadId, response);
      },
      error: (err) => {
        this.error.set(err?.error?.error ?? err?.message ?? 'Failed to load plan from Alvys.');
        this.loading.set(false);
      },
    });
  }

  private recordAudit(parentLoadId: string, plan: ConsolidationPlanResponse): void {
    if (plan.blockers.length > 0) return;
    this.recordingAudit.set(true);
    this.auditError.set(null);

    this.consolidation
      .recordPlanAudit({ parentLoadId, siblingLoadIds: plan.siblings.map((s) => s.loadId) })
      .subscribe({
        next: (record) => {
          this.auditRecord.set(record);
          this.recordingAudit.set(false);
        },
        error: (err) => {
          this.auditError.set(err?.error?.error ?? err?.message ?? 'Failed to record audit.');
          this.recordingAudit.set(false);
        },
      });
  }

  async copyToClipboard(): Promise<void> {
    const text = this.plan()?.clickCard.plainText;
    if (!text) return;
    try {
      if (navigator?.clipboard?.writeText) {
        await navigator.clipboard.writeText(text);
        this.copyMessage.set('Copied to clipboard.');
      } else {
        this.copyMessage.set('Clipboard not available — select the card and copy manually.');
      }
    } catch {
      this.copyMessage.set('Copy failed — select the card and copy manually.');
    }
  }

  formatCurrency(value: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    return `$${value.toLocaleString(undefined, { minimumFractionDigits: 0, maximumFractionDigits: 0 })}`;
  }

  formatRpm(value: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    return `$${value.toFixed(2)} / mi`;
  }

  formatNumber(value: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    return value.toLocaleString();
  }
}
