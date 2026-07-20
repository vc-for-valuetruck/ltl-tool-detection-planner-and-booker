import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { LtlService } from './ltl.service';
import { LtlLoadSummary } from './ltl.models';
import { ConsolidationService } from './consolidation.service';
import { ConsolidationAuditRecord, ConsolidationTrailerFit } from './consolidation.models';
import { LtlNav } from './ltl-nav';

type GapTone = 'amber' | 'blue' | 'green';
interface HonestGap {
  text: string;
  tone: GapTone;
}

const US_STATES = new Set([
  'AL', 'AK', 'AZ', 'AR', 'CA', 'CO', 'CT', 'DE', 'FL', 'GA', 'HI', 'ID', 'IL', 'IN', 'IA',
  'KS', 'KY', 'LA', 'ME', 'MD', 'MA', 'MI', 'MN', 'MS', 'MO', 'MT', 'NE', 'NV', 'NH', 'NJ',
  'NM', 'NY', 'NC', 'ND', 'OH', 'OK', 'OR', 'PA', 'RI', 'SC', 'SD', 'TN', 'TX', 'UT', 'VT',
  'VA', 'WA', 'WV', 'WI', 'WY', 'DC',
]);

@Component({
  selector: 'app-plan-detail',
  standalone: true,
  imports: [CommonModule, RouterLink, LtlNav],
  templateUrl: './plan-detail.html',
  styleUrls: ['./plan-detail.css'],
})
export class PlanDetail implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly ltl = inject(LtlService);
  private readonly consolidation = inject(ConsolidationService);

  constructor() {
    // Router state is only available on the in-flight navigation; read it in the constructor
    // (before the navigation settles) so a "Review plan" click carries its uplift figure (#77).
    const uplift = this.router.getCurrentNavigation()?.extras.state?.['projectedUplift'];
    if (typeof uplift === 'number' && Number.isFinite(uplift)) {
      this.upliftFromState.set(uplift);
    }
  }

  readonly planId = signal<string | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly missingContext = signal(false);
  readonly parent = signal<LtlLoadSummary | null>(null);
  readonly siblings = signal<LtlLoadSummary[]>([]);

  /**
   * Server-computed trailer-fit verdict (issue #76). Null until the plan preview resolves, or when
   * the trailer-fit engine is disabled server-side (NullTrailerFitService) — in which case the UI
   * falls back to the honest "verify at dock" copy rather than implying a fit was checked.
   */
  readonly trailerFit = signal<ConsolidationTrailerFit | null>(null);

  /**
   * Server-assigned preview id for this plan (from the plan-preview response). Shown as the
   * audit-trail "Plan id" before the plan is recorded; once recorded, the persisted audit id
   * takes over. Null until the preview resolves — never fabricated.
   */
  readonly previewPlanId = signal<string | null>(null);

  // Audit-trail record state. The plan-detail can record the plan as an internal audit entry
  // ("Save audit only") without generating a click card — read-only, nothing writes to Alvys.
  readonly recordingAudit = signal(false);
  readonly auditError = signal<string | null>(null);
  readonly auditRecord = signal<ConsolidationAuditRecord | null>(null);

  /** True when the fit verdict is a hard fail: packer rejected OR combined capacity exceeded. */
  readonly trailerFitFails = computed(() => this.trailerFit()?.verdict === 'DoesNotFit');

  /** True when the fit verdict came back clean. */
  readonly trailerFitOk = computed(() => this.trailerFit()?.verdict === 'Fits');

  readonly allLoads = computed(() => {
    const parent = this.parent();
    return parent ? [parent, ...this.siblings()] : [];
  });

  readonly combinedRevenue = computed(() =>
    this.allLoads().reduce((sum, load) => sum + (load.revenue ?? 0), 0),
  );

  /**
   * Projected uplift carried from the queue card via router state (issue #77). Null when the
   * page is opened directly / after a refresh, in which case {@link projectedUplift} derives it.
   */
  readonly upliftFromState = signal<number | null>(null);

  readonly parentLinehaulMiles = computed(() => this.parent()?.mileage ?? null);

  /**
   * Sum of the siblings' own loaded miles. These ride the parent's linehaul, so they are NOT
   * paid to the driver a second time — the click card zeroes each child's Loaded miles in Alvys.
   * Null when no sibling reports loaded miles (never coerced to 0).
   */
  readonly childLoadedMiles = computed<number | null>(() => {
    const siblings = this.siblings();
    let total = 0;
    let any = false;
    for (const s of siblings) {
      if (s.loadedMiles != null) {
        total += s.loadedMiles;
        any = true;
      }
    }
    return any ? total : null;
  });

  /**
   * Projected uplift = combined revenue minus the parent's own linehaul (i.e. the incremental
   * revenue the siblings add). Prefers the exact figure passed from the queue card; otherwise
   * derives it from the live parent/sibling revenues so the number never silently disappears.
   */
  readonly projectedUplift = computed<number | null>(() => {
    const fromState = this.upliftFromState();
    if (fromState !== null) return fromState;

    const parent = this.parent();
    if (!parent) return null;
    return this.combinedRevenue() - (parent.revenue ?? 0);
  });

  readonly combinedRpm = computed(() => {
    const miles = this.parentLinehaulMiles();
    return miles && miles > 0 ? this.combinedRevenue() / miles : null;
  });

  /**
   * "If sold individually" RPM = average of each load's own RPM (its revenue ÷ its own miles).
   * Loads missing revenue or miles are skipped, never zeroed; null when nothing is computable.
   */
  readonly individualRpm = computed<number | null>(() => {
    const rpms: number[] = [];
    for (const load of this.allLoads()) {
      const miles = load.mileage;
      if (load.revenue != null && miles != null && miles > 0) {
        rpms.push(load.revenue / miles);
      }
    }
    if (rpms.length === 0) return null;
    return rpms.reduce((a, b) => a + b, 0) / rpms.length;
  });

  /** RPM uplift vs individual dispatch as a whole-percent delta. Null when uncomputable. */
  readonly projectedUpliftRpmPct = computed<number | null>(() => {
    const combined = this.combinedRpm();
    const individual = this.individualRpm();
    if (combined == null || individual == null || individual <= 0) return null;
    return (combined / individual - 1) * 100;
  });

  /** One-line uplift summary for the audit trail. Only the parts we can compute are shown. */
  readonly upliftLine = computed<string>(() => {
    const dollars = this.projectedUplift();
    const pct = this.projectedUpliftRpmPct();
    const parts: string[] = [];
    if (dollars != null) parts.push(`+${this.formatCurrency(dollars)} combined revenue`);
    if (pct != null) parts.push(`+${Math.round(pct)}% RPM`);
    if (parts.length === 0) return '— vs individual dispatch';
    return `${parts.join(' · ')} vs individual dispatch`;
  });

  readonly totalWeight = computed(() => {
    let total = 0;
    for (const load of this.allLoads()) {
      if (load.weightLbs === null || load.weightLbs === undefined) return null;
      total += load.weightLbs;
    }
    return total;
  });

  readonly honestGaps = computed<HonestGap[]>(() => {
    const loads = this.allLoads();
    const gaps: HonestGap[] = [];

    for (const load of loads) {
      if (load.weightLbs !== null && load.weightLbs !== undefined) {
        gaps.push({
          tone: 'amber',
          text: `Load ${load.loadNumber ?? load.id} pallet count is missing — visual verify at dock`,
        });
      }
    }

    const destinations = loads
      .map((load) => load.destination?.city?.trim().toLowerCase())
      .filter(Boolean);
    if (destinations.length > 1 && destinations.every((city) => city === destinations[0])) {
      gaps.push({ tone: 'blue', text: 'Both loads to same receiver — verify no split required' });
    }

    if (loads.some((load) => this.hasNonUsState(load))) {
      gaps.push({ tone: 'amber', text: 'Cross-border segment detected — verify permits' });
    }

    const customerIds = loads.map((load) => load.customerId).filter(Boolean);
    if (customerIds.length === loads.length && customerIds.every((id) => id === customerIds[0])) {
      gaps.push({ tone: 'green', text: 'Same customer on both loads — allow-flag inferred: allowed' });
    }

    const totalWeight = this.totalWeight();
    if (totalWeight !== null && totalWeight <= 45_000) {
      gaps.push({
        tone: 'green',
        text: `Combined weight ${this.formatNumber(totalWeight)}lb — within 45,000 lb trailer limit`,
      });
    }

    return gaps;
  });

  ngOnInit(): void {
    const planId = this.route.snapshot.paramMap.get('planId');
    this.planId.set(planId);

    const qp = this.route.snapshot.queryParamMap;
    const parentLoadNumber = qp.get('parent');
    const siblingsParam = qp.get('siblings');

    if (planId !== 'live' || !parentLoadNumber || !siblingsParam) {
      this.missingContext.set(true);
      this.loading.set(false);
      return;
    }

    const siblingLoadNumbers = siblingsParam.split(',').map((s) => s.trim()).filter(Boolean);
    const requests = [parentLoadNumber, ...siblingLoadNumbers].map((loadNumber) =>
      this.ltl.getLoad(loadNumber),
    );

    forkJoin(requests).subscribe({
      next: ([parent, ...siblings]) => {
        this.parent.set(parent);
        this.siblings.set(siblings);
        this.loading.set(false);
        this.loadTrailerFit(parent, siblings);
      },
      error: (err) => {
        this.error.set(err?.error?.error ?? err?.message ?? 'Failed to load plan detail from Alvys.');
        this.loading.set(false);
      },
    });
  }

  /**
   * Best-effort fetch of the server-computed trailer-fit verdict (issue #76). The plan preview is
   * the only place the fit is evaluated — the SPA never computes it. Failures are swallowed on
   * purpose: the page already rendered from the live loads, so a plan-preview hiccup must not blank
   * the screen — it simply leaves {@link trailerFit} null and the "verify at dock" fallback stands.
   */
  private loadTrailerFit(parent: LtlLoadSummary, siblings: LtlLoadSummary[]): void {
    const parentLoadId = parent.id;
    const siblingLoadIds = siblings.map((s) => s.id).filter(Boolean);
    if (!parentLoadId || siblingLoadIds.length === 0) return;

    this.consolidation
      .buildPlan({ parentLoadId, siblingLoadIds })
      .subscribe({
        next: (plan) => {
          this.trailerFit.set(plan.trailerFit ?? null);
          this.previewPlanId.set(plan.previewId ?? null);
        },
        error: () => this.trailerFit.set(null),
      });
  }

  /**
   * Records this plan as an internal audit entry ("Save audit only"). Read-only against Alvys —
   * the audit store is internal-only; nothing is written upstream. Idempotent from the UI: the
   * button disables once a record exists so leadership sees a single row per deliberate save.
   */
  recordAudit(): void {
    const parent = this.parent();
    if (!parent || this.recordingAudit() || this.auditRecord()) return;
    const parentLoadId = parent.id;
    const siblingLoadIds = this.siblings().map((s) => s.id).filter(Boolean);
    if (!parentLoadId || siblingLoadIds.length === 0) return;

    this.recordingAudit.set(true);
    this.auditError.set(null);
    this.consolidation.recordPlanAudit({ parentLoadId, siblingLoadIds }).subscribe({
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

  /**
   * Suggested loading order for a sibling by its index. Parent takes the nose; siblings fill
   * mid then tail. A loading-sequence convention, not a dock-verified placement — the trailer
   * plan is explicitly "visual only, dims not verified".
   */
  loadPosition(index: number): string {
    return index === 0 ? 'mid' : index === 1 ? 'tail' : 'mid';
  }

  openClickCard(): void {
    const qp = this.route.snapshot.queryParamMap;
    this.router.navigate(['/ltl/consolidate/plan', 'live', 'click-card'], {
      queryParams: {
        parent: qp.get('parent'),
        siblings: qp.get('siblings'),
        combinedRevenue: this.combinedRevenue(),
        combinedRpm: this.combinedRpm(),
      },
    });
  }

  /**
   * Alvys deep-link for a load (issue #78). Opens the live load in the Alvys web app in a new
   * tab. Prefers the human-facing load number; returns null when neither number nor id is known
   * so the row degrades to non-clickable rather than linking to a broken URL.
   */
  alvysLoadUrl(load: LtlLoadSummary): string | null {
    const ref = load.loadNumber ?? load.id;
    if (!ref) return null;
    return `${PlanDetail.ALVYS_LOAD_BASE_URL}/${encodeURIComponent(ref)}`;
  }

  private static readonly ALVYS_LOAD_BASE_URL = 'https://va336.alvys.com/loads';

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
    return value.toLocaleString(undefined, { maximumFractionDigits: 0 });
  }

  /** 0–1 utilization ratio → whole-percent string; "—" when unknown (never coerced to 0%). */
  formatPercent(value: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    return `${Math.round(value * 100)}%`;
  }

  formatLinearFeet(value: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    return `${value.toLocaleString(undefined, { maximumFractionDigits: 1 })} ft`;
  }

  /**
   * Weight line for the fit panel. When any load lacks a weight the combined figure is a floor,
   * so it renders "≥ N lb" rather than implying a known total.
   */
  formatFitWeight(fit: ConsolidationTrailerFit): string {
    if (fit.totalWeightLbs === null || fit.totalWeightLbs === undefined) return '—';
    const prefix = fit.weightUnknown ? '≥ ' : '';
    return `${prefix}${this.formatNumber(fit.totalWeightLbs)} lb`;
  }

  private hasNonUsState(load: LtlLoadSummary): boolean {
    const states = [load.origin?.state, load.destination?.state]
      .map((state) => state?.trim().toUpperCase())
      .filter(Boolean) as string[];
    return states.some((state) => !US_STATES.has(state));
  }
}
