import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { LtlService } from './ltl.service';
import { LtlLoadSummary } from './ltl.models';

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
  imports: [CommonModule, RouterLink],
  templateUrl: './plan-detail.html',
  styleUrls: ['./plan-detail.css'],
})
export class PlanDetail implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly ltl = inject(LtlService);

  readonly planId = signal<string | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly missingContext = signal(false);
  readonly parent = signal<LtlLoadSummary | null>(null);
  readonly siblings = signal<LtlLoadSummary[]>([]);

  readonly allLoads = computed(() => {
    const parent = this.parent();
    return parent ? [parent, ...this.siblings()] : [];
  });

  readonly combinedRevenue = computed(() =>
    this.allLoads().reduce((sum, load) => sum + (load.revenue ?? 0), 0),
  );

  readonly parentLinehaulMiles = computed(() => this.parent()?.mileage ?? null);

  readonly combinedRpm = computed(() => {
    const miles = this.parentLinehaulMiles();
    return miles && miles > 0 ? this.combinedRevenue() / miles : null;
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
      },
      error: (err) => {
        this.error.set(err?.error?.error ?? err?.message ?? 'Failed to load plan detail from Alvys.');
        this.loading.set(false);
      },
    });
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

  private hasNonUsState(load: LtlLoadSummary): boolean {
    const states = [load.origin?.state, load.destination?.state]
      .map((state) => state?.trim().toUpperCase())
      .filter(Boolean) as string[];
    return states.some((state) => !US_STATES.has(state));
  }
}
