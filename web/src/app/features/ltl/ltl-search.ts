import { Component, computed, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { LtlService } from './ltl.service';
import {
  AssignmentState,
  BillingBadge,
  LtlLoadSummary,
  LtlSearchQuery,
  LtlSearchResponse,
  LtlSortField,
  MatchResult,
} from './ltl.models';

interface SavedView {
  id: string;
  label: string;
  description: string;
  filters: Partial<FilterState>;
  sort?: LtlSortField;
  sortDescending?: boolean;
}

/** Filter shape held in the component (a subset of LtlSearchQuery the UI edits directly). */
interface FilterState {
  keyword: string;
  customer: string;
  originState: string;
  destinationState: string;
  equipmentType: string;
  assignment: AssignmentState | '';
  ltlOnly: boolean;
  readyToBill: boolean;
  missingBillingData: boolean;
  exceptionsOnly: boolean;
}

const EMPTY_FILTERS: FilterState = {
  keyword: '',
  customer: '',
  originState: '',
  destinationState: '',
  equipmentType: '',
  assignment: '',
  ltlOnly: false,
  readyToBill: false,
  missingBillingData: false,
  exceptionsOnly: false,
};

const SAVED_VIEWS: SavedView[] = [
  {
    id: 'unassigned',
    label: 'Unassigned LTL',
    description: 'Open LTL opportunities not yet covered',
    filters: { ltlOnly: true, assignment: 'Unassigned' },
  },
  {
    id: 'high-rev',
    label: 'High Revenue / Low Complexity',
    description: 'Sorted by revenue per mile, highest first',
    filters: {},
    sort: 'RevenuePerMile',
    sortDescending: true,
  },
  {
    id: 'missing-billing',
    label: 'Missing Billing Data',
    description: 'Loads with billing data gaps',
    filters: { missingBillingData: true },
  },
  {
    id: 'ready-to-bill',
    label: 'Ready to Bill',
    description: 'Delivered loads cleared for invoicing',
    filters: { readyToBill: true },
  },
  {
    id: 'exceptions',
    label: 'Exceptions',
    description: 'Loads carrying operational/billing exceptions',
    filters: { exceptionsOnly: true },
  },
];

interface SortableColumn {
  field: LtlSortField;
  label: string;
}

const COLUMNS: SortableColumn[] = [
  { field: 'Customer', label: 'Customer' },
  { field: 'Status', label: 'Status' },
  { field: 'PickupDate', label: 'Pickup' },
  { field: 'DeliveryDate', label: 'Delivery' },
  { field: 'Weight', label: 'Weight' },
  { field: 'Revenue', label: 'Revenue' },
  { field: 'RevenuePerMile', label: 'RPM' },
  { field: 'BillingReadiness', label: 'Billing' },
];

@Component({
  selector: 'app-ltl-search',
  standalone: true,
  imports: [FormsModule, DatePipe, DecimalPipe],
  templateUrl: './ltl-search.html',
  styleUrl: './ltl-search.css',
})
export class LtlSearch {
  private readonly ltl = inject(LtlService);

  protected readonly savedViews = SAVED_VIEWS;
  protected readonly columns = COLUMNS;

  protected readonly filters = signal<FilterState>({ ...EMPTY_FILTERS });
  protected readonly activeView = signal<string | null>(null);
  protected readonly sort = signal<LtlSortField>('PickupDate');
  protected readonly sortDescending = signal(false);
  protected readonly page = signal(1);
  protected readonly pageSize = signal(25);

  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly response = signal<LtlSearchResponse | null>(null);

  protected readonly selected = signal<LtlLoadSummary | null>(null);
  protected readonly matches = signal<MatchResult[] | null>(null);
  protected readonly matchesLoading = signal(false);

  protected readonly items = computed(() => this.response()?.items ?? []);
  protected readonly total = computed(() => this.response()?.total ?? 0);
  protected readonly truncated = computed(() => this.response()?.truncated ?? false);
  protected readonly totalPages = computed(() =>
    Math.max(1, Math.ceil(this.total() / this.pageSize())),
  );
  protected readonly hasResults = computed(() => this.items().length > 0);

  constructor() {
    this.runSearch();
  }

  protected applyView(view: SavedView): void {
    if (this.activeView() === view.id) {
      this.clearFilters();
      return;
    }
    this.filters.set({ ...EMPTY_FILTERS, ...view.filters });
    this.sort.set(view.sort ?? 'PickupDate');
    this.sortDescending.set(view.sortDescending ?? false);
    this.activeView.set(view.id);
    this.page.set(1);
    this.runSearch();
  }

  protected clearFilters(): void {
    this.filters.set({ ...EMPTY_FILTERS });
    this.activeView.set(null);
    this.sort.set('PickupDate');
    this.sortDescending.set(false);
    this.page.set(1);
    this.runSearch();
  }

  protected onSubmit(): void {
    this.activeView.set(null);
    this.page.set(1);
    this.runSearch();
  }

  protected toggleSort(field: LtlSortField): void {
    if (this.sort() === field) {
      this.sortDescending.update((d) => !d);
    } else {
      this.sort.set(field);
      this.sortDescending.set(false);
    }
    this.page.set(1);
    this.runSearch();
  }

  protected goToPage(page: number): void {
    if (page < 1 || page > this.totalPages() || page === this.page()) return;
    this.page.set(page);
    this.runSearch();
  }

  protected select(load: LtlLoadSummary): void {
    this.selected.set(load);
    this.matches.set(null);
    this.matchesLoading.set(true);
    this.ltl.getMatches(load.id, 5).subscribe({
      next: (m) => {
        this.matches.set(m);
        this.matchesLoading.set(false);
      },
      error: () => {
        this.matches.set([]);
        this.matchesLoading.set(false);
      },
    });
  }

  protected closeDrawer(): void {
    this.selected.set(null);
    this.matches.set(null);
  }

  protected runSearch(): void {
    this.loading.set(true);
    this.error.set(null);
    this.ltl.search(this.toQuery()).subscribe({
      next: (res) => {
        this.response.set(res);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(`Search failed: ${err.status} ${err.statusText || ''}`.trim());
        this.loading.set(false);
      },
    });
  }

  private toQuery(): LtlSearchQuery {
    const f = this.filters();
    return {
      keyword: f.keyword || undefined,
      customer: f.customer || undefined,
      originState: f.originState || undefined,
      destinationState: f.destinationState || undefined,
      equipmentType: f.equipmentType || undefined,
      assignment: f.assignment || undefined,
      ltlOnly: f.ltlOnly || undefined,
      readyToBill: f.readyToBill || undefined,
      missingBillingData: f.missingBillingData || undefined,
      exceptionsOnly: f.exceptionsOnly || undefined,
      sort: this.sort(),
      sortDescending: this.sortDescending(),
      page: this.page(),
      pageSize: this.pageSize(),
    };
  }

  protected sortIndicator(field: LtlSortField): string {
    if (this.sort() !== field) return '';
    return this.sortDescending() ? '▼' : '▲';
  }

  protected badgeClass(badge: BillingBadge): string {
    switch (badge) {
      case 'ReadyToBill':
        return 'badge badge-ok';
      case 'AlreadyInvoiced':
        return 'badge badge-muted';
      case 'ExceptionBlockingBilling':
        return 'badge badge-danger';
      default:
        return 'badge badge-warn';
    }
  }

  protected badgeText(badge: BillingBadge): string {
    return badge.replace(/([a-z])([A-Z])/g, '$1 $2');
  }

  protected matchClass(label: string): string {
    switch (label) {
      case 'Excellent':
      case 'Good':
        return 'match match-ok';
      case 'Possible':
        return 'match match-warn';
      default:
        return 'match match-danger';
    }
  }
}
