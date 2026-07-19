import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ConsolidationService } from './consolidation.service';
import { ConsolidationAuditRecord, ConsolidationPlanResponse } from './consolidation.models';

/**
 * Plan Detail (mockup screen 2): trailer plan visualization, economics, assumptions/honest
 * gaps, and audit trail for a single Laredo → Dallas consolidation preview.
 *
 * There is no server-side "get plan by id" endpoint — a preview is a stateless computation
 * over (parentLoadId, siblingLoadIds). This route re-runs that same live computation via
 * `ConsolidationService.buildPlan()` using the parent/sibling ids carried in the query string,
 * so the numbers on screen are always freshly derived from Alvys, never cached/stubbed.
 * If the query params are missing (e.g. a bookmarked link with no context), that is an honest
 * gap shown to the user rather than a fabricated plan.
 */
@Component({
  selector: 'app-plan-detail',
  standalone: true,
  imports: [CommonModule, RouterLink, DatePipe],
  templateUrl: './plan-detail.html',
  styleUrls: ['./plan-detail.css'],
})
export class PlanDetail implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly consolidation = inject(ConsolidationService);

  readonly planId = signal<string | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly plan = signal<ConsolidationPlanResponse | null>(null);
  readonly missingContext = signal(false);

  readonly recordingAudit = signal(false);
  readonly auditError = signal<string | null>(null);
  readonly auditRecord = signal<ConsolidationAuditRecord | null>(null);

  readonly canRecordAudit = computed(
    () => !!this.plan() && (this.plan()?.blockers.length ?? 0) === 0 && !this.recordingAudit(),
  );

  ngOnInit(): void {
    const planId = this.route.snapshot.paramMap.get('planId');
    this.planId.set(planId);

    const qp = this.route.snapshot.queryParamMap;
    const parentLoadId = qp.get('parent');
    const siblingsParam = qp.get('siblings');
    const corridor = qp.get('corridor') ?? undefined;

    if (!parentLoadId || !siblingsParam) {
      this.missingContext.set(true);
      this.loading.set(false);
      return;
    }

    const siblingLoadIds = siblingsParam.split(',').filter(Boolean);

    this.consolidation.buildPlan({ parentLoadId, siblingLoadIds, corridorCode: corridor }).subscribe({
      next: (response) => {
        this.plan.set(response);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err?.error?.error ?? err?.message ?? 'Failed to load plan detail from Alvys.');
        this.loading.set(false);
      },
    });
  }

  openClickCard(): void {
    const p = this.plan();
    const qp = this.route.snapshot.queryParamMap;
    if (!p) return;
    this.router.navigate(['/ltl/consolidate/plan', p.previewId, 'click-card'], {
      queryParams: {
        parent: qp.get('parent'),
        siblings: qp.get('siblings'),
        corridor: qp.get('corridor') ?? undefined,
      },
    });
  }

  recordAuditOnly(): void {
    const p = this.plan();
    const qp = this.route.snapshot.queryParamMap;
    const parentLoadId = qp.get('parent');
    if (!p || !parentLoadId) return;

    this.recordingAudit.set(true);
    this.auditError.set(null);
    this.auditRecord.set(null);

    this.consolidation
      .recordPlanAudit({ parentLoadId, siblingLoadIds: p.siblings.map((s) => s.loadId) })
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

  totalWeight(plan: ConsolidationPlanResponse): number | null {
    const parentWeight = plan.parent.weightLbs ?? null;
    if (parentWeight === null) return null;
    let total = parentWeight;
    for (const s of plan.siblings) {
      if (s.weightLbs === null || s.weightLbs === undefined) return null;
      total += s.weightLbs;
    }
    return total;
  }
}
