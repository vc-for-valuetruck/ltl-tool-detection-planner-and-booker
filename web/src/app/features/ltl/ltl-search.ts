import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Router, RouterLink } from '@angular/router';
import { RUNTIME_CONFIG } from '../../runtime-config';
import { LtlArrivalsBoard } from './ltl-arrivals-board';

interface ConsolidationOpportunitiesResponse {
  opportunities: ConsolidationOpportunity[];
  totalScanned: number;
  totalPairsFound: number;
  generatedAt: string;
  dataSource: string;
}

interface ConsolidationOpportunity {
  rank: number;
  originState: string;
  destinationState: string;
  originCity: string;
  destinationCity: string;
  pickupDate: string;
  customerName: string;
  combinedRevenue: number;
  parentLinehaulMiles: number;
  combinedRpm: number;
  projectedUplift: number;
  parent: ConsolidationOpportunityLoad;
  siblings: ConsolidationOpportunityLoad[];
}

interface ConsolidationOpportunityLoad {
  loadNumber: string;
  loadId: string;
  customerName: string;
  originCity: string;
  originState: string;
  destinationCity: string;
  destinationState: string;
  linehaulAmount: number;
  miles: number;
  rpm: number;
  weightPounds: number | null;
}

@Component({
  selector: 'app-ltl-search',
  standalone: true,
  imports: [DatePipe, RouterLink, LtlArrivalsBoard],
  templateUrl: './ltl-search.html',
  styleUrls: ['./ltl-search.css'],
})
export class LtlSearch implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly base = `${inject(RUNTIME_CONFIG).apiBaseUrl}/ltl`;

  protected readonly opportunities = signal<ConsolidationOpportunity[]>([]);
  protected readonly totalScanned = signal(0);
  protected readonly totalPairsFound = signal(0);
  protected readonly generatedAt = signal<string | null>(null);
  protected readonly dataSource = signal('Alvys va336 (live)');
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly expanded = signal(false);

  protected readonly visibleOpportunities = computed(() =>
    this.expanded() ? this.opportunities() : this.opportunities().slice(0, 3),
  );
  protected readonly additionalCount = computed(() =>
    Math.max(0, this.totalPairsFound() - this.visibleOpportunities().length),
  );

  ngOnInit(): void {
    this.loadOpportunities();
  }

  protected loadOpportunities(): void {
    this.loading.set(true);
    this.error.set(null);

    const params = new HttpParams().set('limit', this.expanded() ? 10 : 3);
    this.http
      .get<ConsolidationOpportunitiesResponse>(`${this.base}/consolidation/opportunities`, { params })
      .subscribe({
        next: (response) => {
          this.opportunities.set(response.opportunities ?? []);
          this.totalScanned.set(response.totalScanned ?? 0);
          this.totalPairsFound.set(response.totalPairsFound ?? 0);
          this.generatedAt.set(response.generatedAt ?? null);
          this.dataSource.set(response.dataSource || 'Alvys va336 (live)');
          this.loading.set(false);
        },
        error: (err) => {
          this.error.set(err?.error?.error ?? err?.message ?? `Couldn't reach Alvys.`);
          this.loading.set(false);
        },
      });
  }

  protected showAll(): void {
    this.expanded.set(true);
    this.loadOpportunities();
  }

  protected reviewPlan(opportunity: ConsolidationOpportunity): void {
    this.router.navigate(['/ltl/consolidate/plan', 'live'], {
      queryParams: {
        parent: opportunity.parent.loadNumber,
        siblings: opportunity.siblings.map((s) => s.loadNumber).join(','),
      },
      // Carry the queue card's projected uplift across the route so the plan detail can show
      // the same figure the dispatcher clicked on (issue #77). Router state survives the
      // navigation but not a refresh — plan detail derives it from live loads as a fallback.
      state: { projectedUplift: opportunity.projectedUplift },
    });
  }

  protected formatMoney(value: number): string {
    return value.toLocaleString(undefined, { maximumFractionDigits: 0 });
  }

  protected formatRpm(value: number): string {
    return value.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  }
}
