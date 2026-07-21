import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { LtlService } from './ltl.service';
import {
  AssignmentState,
  BillingBadge,
  CapacitySnapshot,
  LtlLoadSummary,
  LtlSearchQuery,
  LtlSortField,
  SavedView,
  WorkflowStage,
} from './ltl.models';
import {
  EMPTY_FILTERS,
  FilterState,
  filtersToSnapshot,
  snapshotToFilterState,
} from './saved-views';
import { LtlNav } from './ltl-nav';

type BoolFilterKey = 'readyToBill' | 'missingBillingData' | 'exceptionsOnly' | 'blockedOnly' | 'ltlOnly';

interface QuickToggle {
  key: 'unassigned' | BoolFilterKey;
  label: string;
}

interface Column {
  field: LtlSortField;
  label: string;
}

/**
 * LTL Operating Console — the dispatch search grid (issue #79) wired to `GET /api/ltl/search`
 * (LtlController.Search → LtlLoadService.SearchAsync). Lives on `/ltl/loads`, separate from the
 * `/ltl` consolidation queue. Filters, quick toggles, sortable columns, and dispatcher saved views
 * (built-in presets + own views via `/api/ltl/saved-views`). Read-only: nothing writes to Alvys,
 * and every missing value renders as "—", never coerced to zero/good.
 */
@Component({
  selector: 'app-ltl-console',
  standalone: true,
  imports: [DatePipe, DecimalPipe, FormsModule, RouterLink, LtlNav],
  templateUrl: './ltl-console.html',
  styleUrls: ['./ltl-worklist.css', './ltl-console.css'],
})
export class LtlConsole implements OnInit {
  private readonly ltl = inject(LtlService);

  protected filters: FilterState = { ...EMPTY_FILTERS };
  protected sort: LtlSortField = 'PickupDate';
  protected sortDescending = false;

  protected readonly loads = signal<LtlLoadSummary[]>([]);
  protected readonly total = signal(0);
  protected readonly truncated = signal(false);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected readonly presets = signal<SavedView[]>([]);
  protected readonly views = signal<SavedView[]>([]);
  protected readonly activeViewId = signal<string | null>(null);

  protected readonly hasLoads = computed(() => this.loads().length > 0);

  protected readonly capacity = signal<CapacitySnapshot | null>(null);

  protected readonly quickToggles: QuickToggle[] = [
    { key: 'unassigned', label: 'Unassigned' },
    { key: 'readyToBill', label: 'Ready to Bill' },
    { key: 'missingBillingData', label: 'Missing Billing Data' },
    { key: 'exceptionsOnly', label: 'Exceptions' },
    { key: 'blockedOnly', label: 'Blocked' },
    { key: 'ltlOnly', label: 'LTL Only' },
  ];

  protected readonly columns: Column[] = [
    { field: 'Customer', label: 'Load / Customer' },
    { field: 'PickupDate', label: 'Pickup' },
    { field: 'DeliveryDate', label: 'Delivery' },
    { field: 'Weight', label: 'Weight' },
    { field: 'Revenue', label: 'Revenue' },
    { field: 'RevenuePerMile', label: 'RPM' },
    { field: 'Status', label: 'Status' },
    { field: 'BillingReadiness', label: 'Billing' },
    { field: 'UrgencyScore', label: 'Urgency' },
  ];

  protected readonly assignmentOptions: { value: AssignmentState | ''; label: string }[] = [
    { value: '', label: 'Any assignment' },
    { value: 'Unassigned', label: 'Unassigned' },
    { value: 'Assigned', label: 'Assigned' },
    { value: 'Unknown', label: 'Unknown' },
  ];

  protected readonly stageOptions: { value: WorkflowStage | ''; label: string }[] = [
    { value: '', label: 'Any stage' },
    { value: 'Match', label: 'Match' },
    { value: 'Assign', label: 'Assign' },
    { value: 'Bill', label: 'Bill' },
    { value: 'Billed', label: 'Billed' },
  ];

  ngOnInit(): void {
    this.loadSavedViews();
    this.loadCapacity();
    this.search();
  }

  /**
   * Fetches the live "Capacity today" snapshot. A failure just hides the widget — capacity context
   * is a convenience header, never the grid's data, so a degraded read must not blank the console.
   */
  protected loadCapacity(): void {
    this.ltl.capacityToday().subscribe({
      next: (snapshot) => this.capacity.set(snapshot),
      error: () => this.capacity.set(null),
    });
  }

  protected topTrailerTypes(snapshot: CapacitySnapshot): CapacitySnapshot['trailersByType'] {
    return snapshot.trailersByType.slice(0, 4);
  }

  /** Toggling a filter clears the "which saved view is applied" highlight — the state now differs. */
  protected toggleQuick(key: QuickToggle['key']): void {
    if (key === 'unassigned') {
      this.filters.assignment = this.filters.assignment === 'Unassigned' ? '' : 'Unassigned';
    } else {
      this.filters[key] = !this.filters[key];
    }
    this.activeViewId.set(null);
    this.search();
  }

  /** Whether a quick toggle is currently on; "unassigned" reads off the assignment select. */
  protected isQuickActive(key: QuickToggle['key']): boolean {
    if (key === 'unassigned') return this.filters.assignment === 'Unassigned';
    return this.filters[key];
  }

  protected onFilterChange(): void {
    this.activeViewId.set(null);
  }

  protected clearFilters(): void {
    this.filters = { ...EMPTY_FILTERS };
    this.sort = 'PickupDate';
    this.sortDescending = false;
    this.activeViewId.set(null);
    this.search();
  }

  protected sortBy(field: LtlSortField): void {
    if (this.sort === field) {
      this.sortDescending = !this.sortDescending;
    } else {
      this.sort = field;
      this.sortDescending = false;
    }
    this.search();
  }

  protected sortIndicator(field: LtlSortField): string {
    if (this.sort !== field) return '';
    return this.sortDescending ? '↓' : '↑';
  }

  protected search(): void {
    this.loading.set(true);
    this.error.set(null);
    this.ltl.search(this.buildQuery()).subscribe({
      next: (response) => {
        this.loads.set(response.items ?? []);
        this.total.set(response.total ?? 0);
        this.truncated.set(response.truncated ?? false);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err?.error?.error ?? err?.message ?? "Couldn't reach Alvys.");
        this.loading.set(false);
      },
    });
  }

  protected loadSavedViews(): void {
    this.ltl.listSavedViews().subscribe({
      next: (collection) => {
        this.presets.set(collection.presets ?? []);
        this.views.set(collection.views ?? []);
      },
      // A saved-view fetch failure must not blank the grid — presets are a convenience, not the data.
      error: () => {
        this.presets.set([]);
        this.views.set([]);
      },
    });
  }

  protected applyView(view: SavedView): void {
    this.filters = snapshotToFilterState(view.filters);
    this.sort = view.filters.sort;
    this.sortDescending = view.filters.sortDescending;
    this.activeViewId.set(view.id);
    this.search();
  }

  /** Saves the current filter/sort state as a dispatcher-owned view. Prompt is intentionally simple. */
  protected saveCurrentView(): void {
    const name = (typeof window !== 'undefined' ? window.prompt('Save this view as:') : '')?.trim();
    if (!name) return;
    const snapshot = filtersToSnapshot(this.filters, this.sort, this.sortDescending);
    this.ltl.createSavedView({ name, filters: snapshot }).subscribe({
      next: (view) => {
        this.views.update((vs) => [...vs, view]);
        this.activeViewId.set(view.id);
      },
      error: (err) => this.error.set(err?.error?.error ?? err?.message ?? "Couldn't save the view."),
    });
  }

  protected deleteView(view: SavedView, event: Event): void {
    event.stopPropagation();
    this.ltl.deleteSavedView(view.id).subscribe({
      next: () => {
        this.views.update((vs) => vs.filter((v) => v.id !== view.id));
        if (this.activeViewId() === view.id) this.activeViewId.set(null);
      },
      error: (err) => this.error.set(err?.error?.error ?? err?.message ?? "Couldn't delete the view."),
    });
  }

  protected place(p: LtlLoadSummary['origin']): string {
    if (!p) return '—';
    return p.label ?? ([p.city, p.state].filter(Boolean).join(', ') || '—');
  }

  protected formatCurrency(value: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    return `$${value.toLocaleString(undefined, { maximumFractionDigits: 0 })}`;
  }

  protected weightLabel(load: LtlLoadSummary): string {
    return load.weightLbs === null || load.weightLbs === undefined
      ? '—'
      : `${load.weightLbs.toLocaleString(undefined, { maximumFractionDigits: 0 })} lb`;
  }

  protected rpmLabel(load: LtlLoadSummary): string {
    return load.revenuePerMile === null || load.revenuePerMile === undefined
      ? '—'
      : `$${load.revenuePerMile.toFixed(2)}`;
  }

  protected assignmentClass(state: AssignmentState): string {
    if (state === 'Assigned') return 'chip chip-good';
    if (state === 'Unassigned') return 'chip chip-warn';
    return 'chip chip-neutral';
  }

  /** Maps the editable filter state (+ quick toggles + sort) into the API query object. */
  protected buildQuery(): LtlSearchQuery {
    const f = this.filters;
    const text = (v: string): string | undefined => (v.trim() ? v.trim() : undefined);
    return {
      keyword: text(f.keyword),
      customer: text(f.customer),
      originState: text(f.originState),
      originCity: text(f.originCity),
      destinationState: text(f.destinationState),
      destinationCity: text(f.destinationCity),
      equipmentType: text(f.equipmentType),
      assignment: f.assignment || undefined,
      pickupFrom: text(f.pickupFrom),
      pickupTo: text(f.pickupTo),
      deliveryFrom: text(f.deliveryFrom),
      deliveryTo: text(f.deliveryTo),
      billingBadge: f.billingBadge || undefined,
      stage: f.stage || undefined,
      ltlOnly: f.ltlOnly || undefined,
      readyToBill: f.readyToBill || undefined,
      missingBillingData: f.missingBillingData || undefined,
      exceptionsOnly: f.exceptionsOnly || undefined,
      blockedOnly: f.blockedOnly || undefined,
      sort: this.sort,
      sortDescending: this.sortDescending,
    };
  }

  protected readonly billingBadgeOptions: { value: BillingBadge | ''; label: string }[] = [
    { value: '', label: 'Any billing badge' },
    { value: 'ReadyToBill', label: 'Ready to Bill' },
    { value: 'MissingRate', label: 'Missing Rate' },
    { value: 'MissingPod', label: 'Missing POD' },
    { value: 'MissingWeight', label: 'Missing Weight' },
    { value: 'MissingAccessorialReview', label: 'Missing Accessorial Review' },
    { value: 'PossibleUnbilledAccessorial', label: 'Possible Unbilled Accessorial' },
    { value: 'CarrierAccessorialMismatch', label: 'Carrier Accessorial Mismatch' },
    { value: 'InvoiceAmountDrift', label: 'Invoice Amount Drift' },
    { value: 'CustomerReviewNeeded', label: 'Customer Review Needed' },
    { value: 'ExceptionBlockingBilling', label: 'Exception Blocking Billing' },
    { value: 'AlreadyInvoiced', label: 'Already Invoiced' },
  ];
}
