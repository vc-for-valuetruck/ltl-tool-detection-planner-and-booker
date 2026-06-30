import { Component, computed, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { LtlService } from './ltl.service';
import { AlvysOpsPanel } from './alvys-ops-panel';
import {
  AssignmentAudit,
  AssignmentIssue,
  AssignmentValidationResult,
  BillingBadge,
  LtlLoadSummary,
  LtlSearchQuery,
  LtlSearchResponse,
  LtlSortField,
  MatchResult,
  SavedView,
  VisibilityEventView,
  WorkflowStage,
} from './ltl.models';
import {
  EMPTY_FILTERS,
  FilterState,
  filtersToSnapshot,
  snapshotToFilterState,
} from './saved-views';

type ConsoleTab = 'search' | 'billing' | 'exceptions';

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
  { field: 'Distance', label: 'Miles' },
  { field: 'Revenue', label: 'Revenue' },
  { field: 'RevenuePerMile', label: 'RPM' },
  { field: 'BillingReadiness', label: 'Billing' },
];

/** Ordered Search → Match → Assign → Bill steps for the workflow stepper. */
const WORKFLOW_STEPS: { index: number; label: string }[] = [
  { index: 1, label: 'Search' },
  { index: 2, label: 'Match' },
  { index: 3, label: 'Assign' },
  { index: 4, label: 'Bill' },
];

const WORKFLOW_STAGES: WorkflowStage[] = ['Match', 'Assign', 'Bill', 'Billed'];

const BILLING_BADGES: BillingBadge[] = [
  'ReadyToBill',
  'MissingRate',
  'MissingPod',
  'MissingAccessorialReview',
  'MissingWeight',
  'CustomerReviewNeeded',
  'ExceptionBlockingBilling',
  'AlreadyInvoiced',
];

interface AppliedFilter {
  key: keyof FilterState;
  label: string;
}

@Component({
  selector: 'app-ltl-search',
  standalone: true,
  imports: [FormsModule, DatePipe, DecimalPipe, AlvysOpsPanel],
  templateUrl: './ltl-search.html',
  styleUrls: ['./ltl-search.css', './ltl-saved-views.css'],
})
export class LtlSearch {
  private readonly ltl = inject(LtlService);

  // Saved views: shared built-in presets and the dispatcher's own views, loaded server-side.
  protected readonly presets = signal<SavedView[]>([]);
  protected readonly userViews = signal<SavedView[]>([]);
  protected readonly savedViewsLoading = signal(false);
  protected readonly savedViewsError = signal<string | null>(null);

  // Save / rename dialog state. When `editingViewId` is set the form updates that view in place;
  // otherwise it creates a new one from the current filters.
  protected readonly viewFormOpen = signal(false);
  protected readonly editingViewId = signal<string | null>(null);
  protected readonly viewName = signal('');
  protected readonly viewDescription = signal('');
  protected readonly viewFormError = signal<string | null>(null);
  protected readonly viewSaving = signal(false);

  protected readonly columns = COLUMNS;
  protected readonly billingBadges = BILLING_BADGES;
  protected readonly workflowStages = WORKFLOW_STAGES;
  protected readonly workflowSteps = WORKFLOW_STEPS;

  protected readonly tab = signal<ConsoleTab>('search');

  protected readonly filters = signal<FilterState>({ ...EMPTY_FILTERS });
  protected readonly activeView = signal<string | null>(null);
  protected readonly sort = signal<LtlSortField>('PickupDate');
  protected readonly sortDescending = signal(false);
  protected readonly page = signal(1);
  protected readonly pageSize = signal(25);

  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly response = signal<LtlSearchResponse | null>(null);

  // Worklist tab.
  protected readonly worklist = signal<LtlLoadSummary[] | null>(null);
  protected readonly worklistBadge = signal<BillingBadge | ''>('');
  protected readonly worklistLoading = signal(false);
  protected readonly worklistError = signal<string | null>(null);

  // Exceptions tab.
  protected readonly exceptions = signal<LtlLoadSummary[] | null>(null);
  protected readonly exceptionsLoading = signal(false);
  protected readonly exceptionsError = signal<string | null>(null);

  // Detail drawer / assignment panel.
  protected readonly selected = signal<LtlLoadSummary | null>(null);
  protected readonly detailLoading = signal(false);
  protected readonly matches = signal<MatchResult[] | null>(null);
  protected readonly matchesLoading = signal(false);
  protected readonly expandedMatch = signal<string | null>(null);

  protected readonly assignDriverId = signal('');
  protected readonly assignTruckId = signal('');
  protected readonly assignTrailerId = signal('');
  protected readonly assignMatchScore = signal<number | null>(null);
  protected readonly assignMatchLabel = signal<string | null>(null);
  protected readonly assignOverrideReason = signal('');
  protected readonly assignNotes = signal('');
  protected readonly validation = signal<AssignmentValidationResult | null>(null);
  protected readonly validating = signal(false);
  protected readonly assigning = signal(false);
  protected readonly assignError = signal<string | null>(null);
  protected readonly history = signal<AssignmentAudit[]>([]);

  protected readonly items = computed(() => this.response()?.items ?? []);
  protected readonly total = computed(() => this.response()?.total ?? 0);
  protected readonly truncated = computed(() => this.response()?.truncated ?? false);
  protected readonly totalPages = computed(() =>
    Math.max(1, Math.ceil(this.total() / this.pageSize())),
  );
  protected readonly hasResults = computed(() => this.items().length > 0);

  protected readonly appliedFilters = computed<AppliedFilter[]>(() => {
    const f = this.filters();
    const out: AppliedFilter[] = [];
    const push = (key: keyof FilterState, label: string) => out.push({ key, label });
    if (f.keyword) push('keyword', `“${f.keyword}”`);
    if (f.customer) push('customer', `Customer: ${f.customer}`);
    if (f.originState) push('originState', `Origin ST: ${f.originState.toUpperCase()}`);
    if (f.originCity) push('originCity', `Origin city: ${f.originCity}`);
    if (f.destinationState) push('destinationState', `Dest ST: ${f.destinationState.toUpperCase()}`);
    if (f.destinationCity) push('destinationCity', `Dest city: ${f.destinationCity}`);
    if (f.equipmentType) push('equipmentType', `Equip: ${f.equipmentType}`);
    if (f.assignment) push('assignment', f.assignment);
    if (f.pickupFrom) push('pickupFrom', `Pickup ≥ ${f.pickupFrom}`);
    if (f.pickupTo) push('pickupTo', `Pickup ≤ ${f.pickupTo}`);
    if (f.deliveryFrom) push('deliveryFrom', `Delivery ≥ ${f.deliveryFrom}`);
    if (f.deliveryTo) push('deliveryTo', `Delivery ≤ ${f.deliveryTo}`);
    if (f.billingBadge) push('billingBadge', this.badgeText(f.billingBadge));
    if (f.stage) push('stage', `Stage: ${f.stage}`);
    if (f.ltlOnly) push('ltlOnly', 'LTL only');
    if (f.readyToBill) push('readyToBill', 'Ready to bill');
    if (f.missingBillingData) push('missingBillingData', 'Missing billing');
    if (f.exceptionsOnly) push('exceptionsOnly', 'Exceptions');
    if (f.blockedOnly) push('blockedOnly', 'Blocked only');
    return out;
  });

  constructor() {
    this.runSearch();
    this.loadSavedViews();
  }

  protected loadSavedViews(): void {
    this.savedViewsLoading.set(true);
    this.savedViewsError.set(null);
    this.ltl.listSavedViews().subscribe({
      next: (collection) => {
        this.presets.set(collection.presets);
        this.userViews.set(collection.views);
        this.savedViewsLoading.set(false);
      },
      error: (err) => {
        this.savedViewsError.set(this.describe('Saved views', err));
        this.savedViewsLoading.set(false);
      },
    });
  }

  protected applyView(view: SavedView): void {
    if (this.activeView() === view.id) {
      this.clearFilters();
      return;
    }
    this.filters.set(snapshotToFilterState(view.filters));
    this.sort.set(view.filters.sort);
    this.sortDescending.set(view.filters.sortDescending);
    this.activeView.set(view.id);
    this.page.set(1);
    this.runSearch();
  }

  /** Opens the dialog to save the current filters as a new view. */
  protected startSaveView(): void {
    this.editingViewId.set(null);
    this.viewName.set('');
    this.viewDescription.set('');
    this.viewFormError.set(null);
    this.viewFormOpen.set(true);
  }

  /** Opens the dialog pre-filled to rename/update an existing user view. */
  protected startEditView(view: SavedView): void {
    this.editingViewId.set(view.id);
    this.viewName.set(view.name);
    this.viewDescription.set(view.description ?? '');
    this.viewFormError.set(null);
    this.viewFormOpen.set(true);
  }

  protected cancelViewForm(): void {
    this.viewFormOpen.set(false);
    this.viewFormError.set(null);
  }

  /**
   * Persists the current filters as a new view, or updates the one being edited. On success the
   * saved view becomes the active view so the dispatcher sees it selected.
   */
  protected saveView(): void {
    const name = this.viewName().trim();
    if (!name) {
      this.viewFormError.set('Enter a name for this view.');
      return;
    }

    const request = {
      name,
      description: this.viewDescription().trim() || null,
      filters: filtersToSnapshot(this.filters(), this.sort(), this.sortDescending()),
    };
    const editingId = this.editingViewId();
    const save$ = editingId
      ? this.ltl.updateSavedView(editingId, request)
      : this.ltl.createSavedView(request);

    this.viewSaving.set(true);
    this.viewFormError.set(null);
    save$.subscribe({
      next: (saved) => {
        this.userViews.update((views) =>
          editingId
            ? views.map((v) => (v.id === saved.id ? saved : v))
            : [...views, saved],
        );
        this.viewSaving.set(false);
        this.viewFormOpen.set(false);
        this.activeView.set(saved.id);
      },
      error: (err) => {
        this.viewFormError.set(this.describe('Save view', err));
        this.viewSaving.set(false);
      },
    });
  }

  protected deleteView(view: SavedView): void {
    this.ltl.deleteSavedView(view.id).subscribe({
      next: () => {
        this.userViews.update((views) => views.filter((v) => v.id !== view.id));
        if (this.activeView() === view.id) this.activeView.set(null);
        if (this.editingViewId() === view.id) this.viewFormOpen.set(false);
      },
      error: (err) => this.savedViewsError.set(this.describe('Delete view', err)),
    });
  }

  protected setTab(tab: ConsoleTab): void {
    if (this.tab() === tab) return;
    this.tab.set(tab);
    this.closeDrawer();
    if (tab === 'billing' && this.worklist() === null) this.loadWorklist();
    if (tab === 'exceptions' && this.exceptions() === null) this.loadExceptions();
  }

  protected clearFilters(): void {
    this.filters.set({ ...EMPTY_FILTERS });
    this.activeView.set(null);
    this.sort.set('PickupDate');
    this.sortDescending.set(false);
    this.page.set(1);
    this.runSearch();
  }

  protected removeFilter(key: keyof FilterState): void {
    const next = { ...this.filters() };
    const empty = EMPTY_FILTERS[key];
    (next[key] as FilterState[keyof FilterState]) = empty;
    this.filters.set(next);
    this.activeView.set(null);
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

  protected runSearch(): void {
    this.loading.set(true);
    this.error.set(null);
    this.ltl.search(this.toQuery()).subscribe({
      next: (res) => {
        this.response.set(res);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(this.describe('Search', err));
        this.loading.set(false);
      },
    });
  }

  protected loadWorklist(): void {
    this.worklistLoading.set(true);
    this.worklistError.set(null);
    const badge = this.worklistBadge() || undefined;
    this.ltl.billingWorklist(badge).subscribe({
      next: (rows) => {
        this.worklist.set(rows);
        this.worklistLoading.set(false);
      },
      error: (err) => {
        this.worklistError.set(this.describe('Worklist', err));
        this.worklistLoading.set(false);
      },
    });
  }

  protected loadExceptions(): void {
    this.exceptionsLoading.set(true);
    this.exceptionsError.set(null);
    this.ltl.exceptions().subscribe({
      next: (rows) => {
        this.exceptions.set(rows);
        this.exceptionsLoading.set(false);
      },
      error: (err) => {
        this.exceptionsError.set(this.describe('Exceptions', err));
        this.exceptionsLoading.set(false);
      },
    });
  }

  protected select(load: LtlLoadSummary): void {
    this.selected.set(load);
    this.resetAssignmentForm();

    // List rows carry no per-load visibility/invoice context (those are detail-path fetches).
    // Pull the full detail so the drawer shows invoice-aware billing and visibility failures.
    this.detailLoading.set(true);
    this.ltl.getLoad(load.id).subscribe({
      next: (detail) => {
        if (this.selected()?.id === load.id) this.selected.set(detail);
        this.detailLoading.set(false);
      },
      error: () => this.detailLoading.set(false),
    });

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
    this.ltl.assignments(load.id).subscribe({
      next: (h) => this.history.set(h),
      error: () => this.history.set([]),
    });
  }

  protected closeDrawer(): void {
    this.selected.set(null);
    this.detailLoading.set(false);
    this.matches.set(null);
    this.expandedMatch.set(null);
    this.resetAssignmentForm();
  }

  /** Failed/errored visibility shares on the selected load (detail-path only). */
  protected visibilityFailures(load: LtlLoadSummary): VisibilityEventView[] {
    return load.visibility.events.filter((e) => e.isFailure);
  }

  /** Noteworthy, non-failed visibility milestones for the timeline. */
  protected visibilityMilestones(load: LtlLoadSummary): VisibilityEventView[] {
    return load.visibility.events.filter((e) => !e.isFailure);
  }

  protected toggleFactors(match: MatchResult): void {
    const key = match.driverId ?? match.driverName ?? '';
    this.expandedMatch.update((cur) => (cur === key ? null : key));
  }

  protected isExpanded(match: MatchResult): boolean {
    return this.expandedMatch() === (match.driverId ?? match.driverName ?? '');
  }

  /** Prefill the assignment form from a recommended match and validate it immediately. */
  protected chooseMatch(match: MatchResult): void {
    this.assignDriverId.set(match.driverId ?? '');
    this.assignTruckId.set(match.truckId ?? '');
    this.assignTrailerId.set(match.trailerId ?? '');
    this.assignMatchScore.set(match.score);
    this.assignMatchLabel.set(match.labelText);
    this.assignError.set(null);
    this.runValidation();
  }

  protected runValidation(): void {
    const load = this.selected();
    if (!load || !this.assignDriverId()) {
      this.validation.set(null);
      return;
    }
    this.validating.set(true);
    this.ltl.validateAssignment(load.id, this.buildRequest()).subscribe({
      next: (res) => {
        this.validation.set(res);
        this.validating.set(false);
      },
      error: () => {
        this.validation.set(null);
        this.validating.set(false);
      },
    });
  }

  protected submitAssign(): void {
    const load = this.selected();
    if (!load) return;
    this.assigning.set(true);
    this.assignError.set(null);
    this.ltl.assign(load.id, this.buildRequest()).subscribe({
      next: (audit) => {
        this.history.update((h) => [audit, ...h]);
        this.assigning.set(false);
        this.assignOverrideReason.set('');
        this.assignNotes.set('');
      },
      error: (err) => {
        if (err.status === 422 && err.error?.issues) {
          this.validation.set(err.error as AssignmentValidationResult);
          this.assignError.set('Assignment blocked — resolve the issues below.');
        } else {
          this.assignError.set(this.describe('Assign', err));
        }
        this.assigning.set(false);
      },
    });
  }

  protected get canAssign(): boolean {
    return (
      !!this.assignDriverId() &&
      !this.assigning() &&
      !(this.validation()?.hasBlockers ?? false)
    );
  }

  private buildRequest() {
    return {
      driverId: this.assignDriverId() || undefined,
      truckId: this.assignTruckId() || undefined,
      trailerId: this.assignTrailerId() || undefined,
      matchScore: this.assignMatchScore() ?? undefined,
      matchLabel: this.assignMatchLabel() ?? undefined,
      overrideReason: this.assignOverrideReason() || undefined,
      notes: this.assignNotes() || undefined,
    };
  }

  private resetAssignmentForm(): void {
    this.assignDriverId.set('');
    this.assignTruckId.set('');
    this.assignTrailerId.set('');
    this.assignMatchScore.set(null);
    this.assignMatchLabel.set(null);
    this.assignOverrideReason.set('');
    this.assignNotes.set('');
    this.validation.set(null);
    this.assignError.set(null);
    this.history.set([]);
  }

  private toQuery(): LtlSearchQuery {
    const f = this.filters();
    return {
      keyword: f.keyword || undefined,
      customer: f.customer || undefined,
      originState: f.originState || undefined,
      originCity: f.originCity || undefined,
      destinationState: f.destinationState || undefined,
      destinationCity: f.destinationCity || undefined,
      equipmentType: f.equipmentType || undefined,
      assignment: f.assignment || undefined,
      pickupFrom: f.pickupFrom || undefined,
      pickupTo: f.pickupTo || undefined,
      deliveryFrom: f.deliveryFrom || undefined,
      deliveryTo: f.deliveryTo || undefined,
      billingBadge: f.billingBadge || undefined,
      stage: f.stage || undefined,
      ltlOnly: f.ltlOnly || undefined,
      readyToBill: f.readyToBill || undefined,
      missingBillingData: f.missingBillingData || undefined,
      exceptionsOnly: f.exceptionsOnly || undefined,
      blockedOnly: f.blockedOnly || undefined,
      sort: this.sort(),
      sortDescending: this.sortDescending(),
      page: this.page(),
      pageSize: this.pageSize(),
    };
  }

  private describe(what: string, err: { status?: number; statusText?: string }): string {
    return `${what} failed: ${err.status ?? ''} ${err.statusText ?? ''}`.trim();
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

  protected badgeText(badge: string): string {
    return badge.replace(/([a-z])([A-Z])/g, '$1 $2');
  }

  protected stageClass(stage: WorkflowStage, blocked: boolean): string {
    if (blocked) return 'stage stage-blocked';
    switch (stage) {
      case 'Match':
        return 'stage stage-match';
      case 'Assign':
        return 'stage stage-assign';
      case 'Bill':
        return 'stage stage-bill';
      default:
        return 'stage stage-billed';
    }
  }

  /** Stepper cell state for a given step index against the load's current step. */
  protected stepClass(stepIndex: number, current: number, blocked: boolean): string {
    if (stepIndex < current) return 'step step-done';
    if (stepIndex === current) return blocked ? 'step step-current step-blocked' : 'step step-current';
    return 'step step-todo';
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

  protected factorClass(status: string): string {
    switch (status) {
      case 'Strong':
        return 'factor factor-strong';
      case 'Weak':
        return 'factor factor-weak';
      case 'Unavailable':
        return 'factor factor-na';
      default:
        return 'factor factor-neutral';
    }
  }

  protected issueClass(issue: AssignmentIssue): string {
    return issue.severity === 'Block' ? 'issue issue-block' : 'issue issue-warn';
  }
}
