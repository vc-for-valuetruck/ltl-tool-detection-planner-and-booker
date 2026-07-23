import { CommonModule, DatePipe } from '@angular/common';
import { Component, OnDestroy, OnInit, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MsalService } from '@azure/msal-angular';
import { AuthSessionStore } from '../../core/auth/auth-session.store';
import { RUNTIME_CONFIG, isAuthConfigured } from '../../runtime-config';
import { LaredoArrival, LaredoArrivalsBoard } from './arrivals.models';
import {
  ConsolidationCandidate,
  ConsolidationCandidateResponse,
  ConsolidationPlanResponse,
  WarehouseSummary,
} from './consolidation.models';
import { ConsolidationService } from './consolidation.service';
import { DispatchPreferenceView } from './dispatch-planner.models';
import { DispatchPlannerService } from './dispatch-planner.service';
import {
  DockCombineResponse,
  DockNotificationResult,
  DockPresenceResponse,
  YardOpportunityView,
} from './dock.models';
import { DockService } from './dock.service';
import { LtlParentChildBadge } from './ltl-parent-child-badge';
import { LtlStatusChip } from './ltl-status-chip';

/** The dock-worker flow is a linear step machine; each value is one screen in the wizard. */
type DockStep = 'warehouse' | 'arrivals' | 'siblings' | 'review' | 'result';

/** Which print-ready artifact is currently armed for printing (drives the print-only host class). */
type PrintMode = 'none' | 'bol' | 'clickcard';

/**
 * Client-side sort applied to the already-loaded sibling suggestions. `best` keeps the server's own
 * fit ranking; `revenue`/`earliest` reorder over fields Alvys actually supplies on a candidate.
 * (There is no per-candidate driver-RPM in the candidate contract, so revenue is the honest
 * money proxy here — RPM is computed only once at the combined-plan level.)
 */
type DockCandidateSort = 'best' | 'revenue' | 'earliest';

/**
 * Dock mode (Phase 2.5): the tablet-first "easy match loads" flow a dock worker walks when a truck
 * lands at a yard. Pick a yard → tap the parent (BOL-controlling) truck/load → add auto-suggested or
 * manually-searched siblings → review the combined driver-RPM economics → combine. A combine records
 * an internal audit only and produces two print-ready outputs: the combined BOL packet / dock
 * manifest and the Alvys click card the dispatcher executes manually.
 *
 * Read-only against Alvys: arrivals, candidates and the combined plan are all live Alvys reads or
 * static config, and the combine's audit carries `AlvysWriteback = NotPerformed`. Missing data
 * (weight, windows, driver, equipment) renders as "—" — never fabricated.
 */
@Component({
  selector: 'app-dock',
  standalone: true,
  imports: [CommonModule, FormsModule, DatePipe, LtlParentChildBadge, LtlStatusChip],
  templateUrl: './dock.html',
  styleUrls: ['./dock.css'],
  host: { '[class.printing-bol]': "printMode() === 'bol'", '[class.printing-card]': "printMode() === 'clickcard'" },
})
export class Dock implements OnInit, OnDestroy {
  private readonly dock = inject(DockService);
  private readonly consolidation = inject(ConsolidationService);
  private readonly dispatchPlanner = inject(DispatchPlannerService);
  private readonly authSession = inject(AuthSessionStore);
  private readonly msal = inject(MsalService);
  private readonly runtimeConfig = inject(RUNTIME_CONFIG);

  /**
   * True once any HTTP call from this SPA has returned 401 for the current session. When set,
   * the dock replaces the indefinite "Working…" loader with a re-auth prompt. See issue #164 and
   * the {@link AuthSessionStore} / {@link sessionExpiredInterceptor} pair for the plumbing.
   */
  protected readonly sessionExpired = this.authSession.sessionExpired;

  /** Whether the Sign-in button should actually attempt an MSAL redirect (auth configured). */
  protected readonly canSignIn = computed(() => isAuthConfigured(this.runtimeConfig));

  /**
   * Handler for the "Sign in again" button on the re-auth panel. Triggers MSAL's redirect flow
   * using the same scope set the rest of the SPA uses; clears the session-expired signal so the
   * normal loading states can render once the user returns.
   */
  protected signInAgain(): void {
    const scopes = this.runtimeConfig.apiScope ? [this.runtimeConfig.apiScope] : [];
    this.authSession.clear();
    if (isAuthConfigured(this.runtimeConfig)) {
      this.msal.loginRedirect({ scopes });
    }
  }

  /** How long the one-tap Undo stays offered after a combine, in seconds (a few minutes). */
  private static readonly UndoWindowSeconds = 180;

  protected readonly step = signal<DockStep>('warehouse');

  protected readonly warehouses = signal<WarehouseSummary[]>([]);
  protected readonly selectedWarehouse = signal<WarehouseSummary | null>(null);

  protected readonly board = signal<LaredoArrivalsBoard | null>(null);
  protected readonly parent = signal<LaredoArrival | null>(null);

  /**
   * Yard-originated LTL consolidation opportunities (from `LtlDraftCreated` webhooks), newest first
   * (issue #166). Inbound suggestions only — the dock acts on them inside this same Alvys-backed flow.
   * Empty when the webhook boundary is dormant or nothing has arrived; the SPA refreshes over REST.
   */
  protected readonly opportunities = signal<YardOpportunityView[]>([]);

  protected readonly candidates = signal<ConsolidationCandidateResponse | null>(null);
  /** Selected sibling load ids, insertion-ordered. */
  protected readonly selectedSiblingIds = signal<string[]>([]);
  protected readonly manualSiblingId = signal('');

  protected readonly combineResult = signal<DockCombineResponse | null>(null);
  protected readonly printMode = signal<PrintMode>('none');

  /**
   * Read-only plan preview fetched at the Review step so the dock worker sees the plan's blockers
   * (parent off-corridor, a Never-consolidate sibling, an unresolved load) BEFORE combining. A plan
   * with blockers must not combine — the Combine button is disabled and the backend fails closed (422).
   */
  protected readonly previewPlan = signal<ConsolidationPlanResponse | null>(null);
  protected readonly previewLoading = signal(false);
  protected readonly previewBlockers = computed(() => this.previewPlan()?.blockers ?? []);
  protected readonly hasBlockers = computed(() => this.previewBlockers().length > 0);

  /**
   * Preferred driver/truck/trailer pairing for the parent's equipment, read from the Alvys Public
   * API dispatch planner (read-only). Shown as "preferred …" chips on the review card so the dock
   * worker sees the planner's intended assignment; honestly null when Alvys carries no preference or
   * the read degraded. Never fabricated — an unresolved view renders "—".
   */
  protected readonly preferredPairing = signal<DispatchPreferenceView | null>(null);
  protected readonly hasPreferredPairing = computed(() => this.preferredPairing()?.resolved === true);

  /**
   * Yard presence for the parent equipment at the Review step (issue #166). A peer signal folded in
   * next to the preferred-pairing chip — never operational truth. Null until a lookup completes; the
   * chip then reads {@link presenceChip}. A security hold ({@link presenceBlocksCombine}) is the only
   * presence state that can stop a combine; every other state is informational and honest.
   */
  protected readonly yardPresence = signal<DockPresenceResponse | null>(null);
  protected readonly presenceLoading = signal(false);

  /** True when the yard has placed a security hold on release — disables Combine (red chip). */
  protected readonly presenceBlocksCombine = computed(
    () => this.yardPresence()?.securityHold === true,
  );

  /**
   * The Review-step presence chip descriptor. Green = at yard (with release time when known); amber =
   * configured + reachable but not at yard; red = security hold (blocks combine); grey = unavailable
   * (integration off or yard unreachable). Null while a lookup is in flight or before one runs.
   */
  protected readonly presenceChip = computed<{
    state: 'green' | 'amber' | 'red' | 'grey';
    text: string;
  } | null>(() => {
    const p = this.yardPresence();
    if (!p) return null;
    if (!p.configured || !p.available) {
      return { state: 'grey', text: 'Presence: unavailable' };
    }
    if (p.securityHold) {
      return { state: 'red', text: 'Security hold on release' };
    }
    if (!p.atYard) {
      return {
        state: 'amber',
        text: p.onRecord ? 'Not at yard' : 'Not at yard (no yard record)',
      };
    }
    const released = p.releasedAt ? this.formatTimeHm(p.releasedAt) : null;
    return { state: 'green', text: released ? `At yard · released ${released}` : 'At yard' };
  });

  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly copyMessage = signal<string | null>(null);

  /** True while the server-side BOL packet PDF is being generated + downloaded. */
  protected readonly pdfDownloading = signal(false);

  // --- Feature 3: minimal-tap instrumentation + one-tap Undo ----------------
  /** Taps on the happy path since the parent was chosen (parent tap → combine). Powers the metric. */
  protected readonly tapCount = signal(0);
  /** Epoch ms when the parent was tapped; the start of the time-to-combine measurement. */
  private parentTappedAt: number | null = null;

  /** Notification outcome for the committed combine; updated in place by the retry chip. */
  protected readonly notification = signal<DockNotificationResult | null>(null);
  protected readonly notifyRetrying = signal(false);

  /** One-tap Undo state. Available for a few minutes after combine, until used. */
  protected readonly undoSecondsLeft = signal(0);
  protected readonly undone = signal(false);
  protected readonly undoing = signal(false);
  protected readonly undoAvailable = computed(() => this.undoSecondsLeft() > 0 && !this.undone());
  private undoTimer: ReturnType<typeof setInterval> | null = null;

  // --- Auto-combine mode (opt-in one-tap flow) -----------------------------
  /**
   * Opt-in "one-tap combine" mode (default OFF). When ON, an effect (see the constructor) walks the
   * flow for the worker after they pick a yard: auto-pick the parent, auto-add the top-ranked
   * siblings, and auto-load the plan preview — then surfaces a single "One-tap combine" button.
   *
   * Auto-mode is strictly additive and honest: it never combines silently, never fabricates a value,
   * and ejects to the normal manual flow (via {@link ejectAuto}) the moment any step's data is
   * missing (Alvys read failed, no load number, no candidates) or the plan preview has blockers. The
   * manual yard → parent → siblings → review → combine path is untouched when the toggle is OFF.
   */
  protected readonly autoMode = signal(false);
  /** Max siblings auto-mode will add, configurable by the worker. Default 3. */
  protected readonly autoSiblingCap = signal(3);
  /** Set to a human reason when auto-mode ejected to manual; drives the eject banner. */
  protected readonly autoEjectReason = signal<string | null>(null);
  /** True once a clean plan preview is loaded in auto-mode — arms the one-tap combine button. */
  protected readonly autoReady = signal(false);
  /** True once auto-mode has taken at least one step this run — gates the tap-count chip. */
  protected readonly autoUsed = signal(false);
  /** Set for the single combine round-trip fired by the one-tap button, so it also prints + opens the card. */
  private autoFinishRequested = false;

  protected setAutoSiblingCap(value: number): void {
    if (!Number.isFinite(value)) return;
    this.autoSiblingCap.set(Math.min(9, Math.max(1, Math.trunc(value))));
  }

  protected toggleAutoMode(on: boolean): void {
    this.autoEjectReason.set(null);
    this.autoReady.set(false);
    this.autoMode.set(on);
  }

  constructor() {
    // Effect-driven step progression: reacts to auto-mode + the current step + the data each step
    // needs. Every branch is idempotent (it advances the step, so the next run takes the next
    // branch) and honest (missing data ejects to manual instead of guessing or combining silently).
    effect(() => {
      if (!this.autoMode()) return;
      if (this.loading()) return; // an Alvys read is in flight — wait for it to settle
      if (this.error()) {
        // A read failed and the error card is already showing; stop auto rather than stall silently.
        this.ejectAuto('Auto-suggest unavailable — Alvys read failed. Continue manually.');
        return;
      }
      switch (this.step()) {
        case 'arrivals':
          this.autoPickParent();
          break;
        case 'siblings':
          this.autoPickSiblings();
          break;
        case 'review':
          this.autoHandleReview();
          break;
      }
    });
  }

  /**
   * Auto-pick the parent. Arrivals carry no uplift/revenue field (that lives on the consolidation
   * opportunity, a different contract), so we rank honestly by what the board actually supplies:
   * a corridor-bound truck first, then board order. Only arrivals with a load number can control a
   * BOL; if none qualify, eject to manual rather than guess.
   */
  private autoPickParent(): void {
    const eligible = this.arrivals().filter((a) => this.canBeParent(a));
    if (eligible.length === 0) {
      this.ejectAuto('Auto-suggest unavailable — no inbound load has a load number. Pick the parent manually.');
      return;
    }
    const best = eligible.find((a) => a.dallasBound) ?? eligible[0];
    this.autoUsed.set(true);
    this.pickParent(best);
  }

  /**
   * Auto-add the top-ranked siblings, capped at {@link autoSiblingCap}. Uses the server's fit
   * ranking untouched ({@link selectableCandidates} already drops blocked candidates) and sets the
   * selection directly so the tap count is not inflated. No candidates → eject to manual.
   */
  private autoPickSiblings(): void {
    const picks = this.selectableCandidates().slice(0, this.autoSiblingCap());
    if (picks.length === 0) {
      this.ejectAuto('Auto-suggest unavailable — no eligible siblings suggested. Add them manually.');
      return;
    }
    this.selectedSiblingIds.set(picks.map((c) => c.loadId));
    this.goToReview();
  }

  /**
   * At the review step in auto-mode: wait for the preview, then either eject (blockers must be
   * resolved by a human — never auto-combined) or arm the one-tap combine button on a clean plan.
   */
  private autoHandleReview(): void {
    if (this.previewLoading()) return;
    if (!this.previewPlan()) return; // preview not back yet (a failure sets error(), handled above)
    if (this.hasBlockers()) {
      this.ejectAuto('Auto-suggest paused — the combined plan has blockers. Review and resolve before combining.');
      return;
    }
    this.autoReady.set(true);
  }

  /** Stops auto-mode and drops the worker into the normal manual flow with an honest banner. */
  private ejectAuto(reason: string): void {
    this.autoMode.set(false);
    this.autoReady.set(false);
    this.autoEjectReason.set(reason);
  }

  /**
   * The single auto-mode action: combine, then (on success) download the BOL packet and open the
   * click card in a new tab. The yard notification is already fired server-side by combine, and a
   * plan with blockers is refused by {@link combine} — so this stays honest and read-only.
   */
  protected oneTapCombine(): void {
    if (!this.autoReady() || this.hasBlockers()) return;
    this.autoFinishRequested = true;
    this.combine();
  }

  private static readonly stepOrder: DockStep[] = ['warehouse', 'arrivals', 'siblings', 'review', 'result'];
  protected readonly stepIndex = computed(() => Dock.stepOrder.indexOf(this.step()));

  protected readonly arrivals = computed(() => this.board()?.arrivals ?? []);
  protected readonly candidateList = computed(() => this.candidates()?.candidates ?? []);
  protected readonly selectableCandidates = computed(() =>
    this.candidateList().filter((c) => !c.isBlocked),
  );

  /** Active sibling sort chip. Defaults to the server's fit ranking; reset when candidates reload. */
  protected readonly candidateSort = signal<DockCandidateSort>('best');
  protected readonly candidateSorts: readonly { readonly id: DockCandidateSort; readonly label: string }[] = [
    { id: 'best', label: 'Best match' },
    { id: 'revenue', label: 'Highest revenue' },
    { id: 'earliest', label: 'Earliest pickup' },
  ];

  /**
   * The sibling list reordered by the active chip. `best` is the server order untouched. Candidates
   * missing the sort field (no revenue / no pickup time) always sort to the bottom — never coerced
   * to zero or "now" — so an honest "—" card never jumps above a real value.
   */
  protected readonly sortedCandidates = computed<ConsolidationCandidate[]>(() => {
    const list = this.candidateList();
    const sort = this.candidateSort();
    if (sort === 'best') return list;
    const copy = [...list];
    if (sort === 'revenue') {
      copy.sort((a, b) => this.compareNullableDesc(this.revenueOf(a), this.revenueOf(b)));
    } else {
      copy.sort((a, b) => this.compareNullableAsc(this.pickupMsOf(a), this.pickupMsOf(b)));
    }
    return copy;
  });

  protected setCandidateSort(sort: DockCandidateSort): void {
    this.candidateSort.set(sort);
  }

  private revenueOf(c: ConsolidationCandidate): number | null {
    return typeof c.revenue === 'number' && !Number.isNaN(c.revenue) ? c.revenue : null;
  }

  private pickupMsOf(c: ConsolidationCandidate): number | null {
    if (!c.scheduledPickupAt) return null;
    const ms = Date.parse(c.scheduledPickupAt);
    return Number.isNaN(ms) ? null : ms;
  }

  /** Descending compare with nulls last (stable-friendly: equal → 0). */
  private compareNullableDesc(a: number | null, b: number | null): number {
    if (a === null && b === null) return 0;
    if (a === null) return 1;
    if (b === null) return -1;
    return b - a;
  }

  /** Ascending compare with nulls last. */
  private compareNullableAsc(a: number | null, b: number | null): number {
    if (a === null && b === null) return 0;
    if (a === null) return 1;
    if (b === null) return -1;
    return a - b;
  }
  protected readonly hasSelection = computed(() => this.selectedSiblingIds().length > 0);
  protected readonly plan = computed(() => this.combineResult()?.plan ?? null);
  protected readonly audit = computed(() => this.combineResult()?.audit ?? null);
  /** Parent load number/id for naming the parent on child badges; null when unknown. */
  protected readonly parentLabel = computed(
    () => this.plan()?.parent.loadNumber ?? this.parent()?.loadNumber ?? null,
  );

  ngOnInit(): void {
    this.loading.set(true);
    this.dock.getWarehouses().subscribe({
      next: (res) => {
        this.warehouses.set(res.warehouses);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(this.messageOf(err, 'Could not load warehouses.'));
        this.loading.set(false);
      },
    });
    this.loadOpportunities();
  }

  /**
   * Loads yard-originated incoming opportunities (issue #166). Best-effort and non-blocking: a failure
   * (or a dormant webhook boundary) simply leaves the list empty — it never blocks the dock flow.
   */
  protected loadOpportunities(): void {
    this.dock.getOpportunities().subscribe({
      next: (res) => this.opportunities.set(res.opportunities),
      error: () => this.opportunities.set([]),
    });
  }

  // --- Step 1: warehouse ---------------------------------------------------

  protected pickWarehouse(warehouse: WarehouseSummary): void {
    this.selectedWarehouse.set(warehouse);
    this.step.set('arrivals');
    this.loadArrivals();
  }

  private loadArrivals(): void {
    const warehouse = this.selectedWarehouse();
    if (!warehouse) return;
    this.loading.set(true);
    this.error.set(null);
    this.dock.getArrivals(warehouse.code).subscribe({
      next: (board) => {
        this.board.set(board);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(this.messageOf(err, 'Could not reach Alvys for arrivals.'));
        this.loading.set(false);
      },
    });
  }

  // --- Step 2: pick the parent (BOL-controlling) load ----------------------

  protected canBeParent(arrival: LaredoArrival): boolean {
    return !!arrival.loadNumber;
  }

  protected pickParent(arrival: LaredoArrival): void {
    if (!arrival.loadNumber) return;
    this.parent.set(arrival);
    this.selectedSiblingIds.set([]);
    // Start the time-to-combine clock and the tap budget at the parent tap (tap #1). Candidates are
    // loaded immediately here so they are pre-ranked and ready before the worker looks up.
    this.parentTappedAt = Date.now();
    this.tapCount.set(1);
    this.step.set('siblings');
    this.loadCandidates(arrival.loadNumber);
  }

  private loadCandidates(parentLoadId: string): void {
    this.loading.set(true);
    this.error.set(null);
    this.candidates.set(null);
    this.candidateSort.set('best');
    this.dock.getCandidates(parentLoadId).subscribe({
      next: (res) => {
        this.candidates.set(res);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(this.messageOf(err, 'Could not load sibling suggestions.'));
        this.loading.set(false);
      },
    });
  }

  // --- Step 3: build the sibling set --------------------------------------

  protected isSelected(candidate: ConsolidationCandidate): boolean {
    return this.selectedSiblingIds().includes(candidate.loadId);
  }

  protected toggleSibling(candidate: ConsolidationCandidate): void {
    if (candidate.isBlocked) return;
    const current = this.selectedSiblingIds();
    this.selectedSiblingIds.set(
      current.includes(candidate.loadId)
        ? current.filter((id) => id !== candidate.loadId)
        : [...current, candidate.loadId],
    );
    this.tapCount.update((n) => n + 1);
  }

  protected addManualSibling(): void {
    const id = this.manualSiblingId().trim();
    if (!id) return;
    if (!this.selectedSiblingIds().includes(id)) {
      this.selectedSiblingIds.set([...this.selectedSiblingIds(), id]);
    }
    this.manualSiblingId.set('');
    this.tapCount.update((n) => n + 1);
  }

  protected removeSibling(id: string): void {
    this.selectedSiblingIds.set(this.selectedSiblingIds().filter((s) => s !== id));
  }

  /** Human label for a selected sibling id — the candidate's load number when known, else the id. */
  protected siblingLabel(id: string): string {
    const match = this.candidateList().find((c) => c.loadId === id);
    return match?.loadNumber ?? id;
  }

  // --- Step 4/5: combine ---------------------------------------------------

  /**
   * The single happy-path action: records the audit, builds the BOL packet + click card, and fires
   * the yard notification — all in one round-trip, with no confirmation dialog. On success it lands
   * on the result step (docs rendered), starts the one-tap Undo window, and records the
   * time-to-combine + tap-count effectiveness metric.
   */
  protected combine(): void {
    const parent = this.parent();
    const siblings = this.selectedSiblingIds();
    if (!parent?.loadNumber || siblings.length === 0) return;
    // Fail closed on a known-blocked plan — the backend also guards (422), this is the UI's guard.
    if (this.hasBlockers()) return;
    // A yard security hold on release blocks the combine (issue #166); the button is also disabled.
    if (this.presenceBlocksCombine()) return;

    this.tapCount.update((n) => n + 1);
    this.loading.set(true);
    this.error.set(null);
    this.dock
      .combine({
        parentLoadId: parent.loadNumber,
        siblingLoadIds: siblings,
        corridorCode: this.candidates()?.corridorCode,
        warehouseCode: this.selectedWarehouse()?.code,
      })
      .subscribe({
        next: (res) => {
          this.combineResult.set(res);
          this.notification.set(res.notification);
          this.undone.set(false);
          this.loading.set(false);
          this.step.set('result');
          this.startUndoWindow();
          this.recordCombineMetric(siblings.length);
          // One-tap auto-finish: print the packet + open the click card. The notification was
          // already fired by combine, so this only adds the two client-side outputs.
          if (this.autoFinishRequested) {
            this.autoFinishRequested = false;
            this.downloadPdf();
            this.openClickCardTab();
          }
        },
        error: (err) => {
          this.autoFinishRequested = false;
          // A 422 carries the blocked plan itself (not an {error} envelope): surface its blockers
          // at the review step so the worker sees exactly why the combine was refused.
          const blocked = this.blockedPlanOf(err);
          if (blocked) {
            this.previewPlan.set(blocked);
            this.step.set('review');
            this.error.set('This plan has blockers and cannot be combined.');
          } else {
            this.error.set(this.messageOf(err, 'Combine failed — nothing was recorded.'));
          }
          this.loading.set(false);
        },
      });
  }

  /** Extracts a blocked plan from a 422 combine response, or null for any other error shape. */
  private blockedPlanOf(err: unknown): ConsolidationPlanResponse | null {
    const e = err as { status?: number; error?: ConsolidationPlanResponse };
    return e?.status === 422 && Array.isArray(e.error?.blockers) ? e.error! : null;
  }

  /** One-tap Undo: records a retraction audit. Nothing was written to Alvys, so this reverses nothing there. */
  protected undoCombine(): void {
    const parent = this.parent();
    const siblings = this.selectedSiblingIds();
    if (!parent?.loadNumber || siblings.length === 0 || this.undone()) return;

    this.undoing.set(true);
    this.dock
      .undo({
        parentLoadId: parent.loadNumber,
        siblingLoadIds: siblings,
        corridorCode: this.candidates()?.corridorCode,
      })
      .subscribe({
        next: () => {
          this.undone.set(true);
          this.undoing.set(false);
          this.stopUndoWindow();
        },
        error: (err) => {
          this.error.set(this.messageOf(err, 'Undo failed — the combine audit still stands.'));
          this.undoing.set(false);
        },
      });
  }

  /** Retry chip: re-sends the yard notification without recording another audit. */
  protected retryNotify(): void {
    const parent = this.parent();
    const siblings = this.selectedSiblingIds();
    if (!parent?.loadNumber || siblings.length === 0) return;

    this.notifyRetrying.set(true);
    this.dock
      .renotify({
        parentLoadId: parent.loadNumber,
        siblingLoadIds: siblings,
        corridorCode: this.candidates()?.corridorCode,
        warehouseCode: this.selectedWarehouse()?.code,
      })
      .subscribe({
        next: (result) => {
          this.notification.set(result);
          this.notifyRetrying.set(false);
        },
        error: () => {
          // Non-blocking: leave the prior Failed state so the chip stays retryable.
          this.notifyRetrying.set(false);
        },
      });
  }

  private recordCombineMetric(siblingCount: number): void {
    const timeToCombineMs =
      this.parentTappedAt !== null ? Date.now() - this.parentTappedAt : undefined;
    // Fire-and-forget: a metric failure must never affect the dock worker.
    this.dock
      .recordCombineMetric({
        warehouseCode: this.selectedWarehouse()?.code,
        siblingCount,
        tapCount: this.tapCount(),
        timeToCombineMs,
      })
      .subscribe({ next: () => {}, error: () => {} });
  }

  private startUndoWindow(): void {
    this.stopUndoWindow();
    this.undoSecondsLeft.set(Dock.UndoWindowSeconds);
    if (typeof setInterval !== 'function') return;
    this.undoTimer = setInterval(() => {
      const left = this.undoSecondsLeft() - 1;
      this.undoSecondsLeft.set(Math.max(0, left));
      if (left <= 0) this.stopUndoWindow();
    }, 1000);
  }

  private stopUndoWindow(): void {
    if (this.undoTimer !== null) {
      clearInterval(this.undoTimer);
      this.undoTimer = null;
    }
    this.undoSecondsLeft.set(0);
  }

  ngOnDestroy(): void {
    this.stopUndoWindow();
  }

  protected goToReview(): void {
    if (!this.hasSelection()) return;
    this.step.set('review');
    this.loadPreview();
  }

  /**
   * Fetches the read-only consolidation plan preview so blockers surface at review time, before any
   * combine. Reuses the already-tested consolidation plan endpoint — no new consolidation logic and
   * no Alvys write.
   */
  private loadPreview(): void {
    const parent = this.parent();
    const siblings = this.selectedSiblingIds();
    if (!parent?.loadNumber || siblings.length === 0) return;
    this.loadPreferredPairing(parent);
    this.loadPresence(parent);
    this.previewLoading.set(true);
    this.previewPlan.set(null);
    this.error.set(null);
    this.consolidation
      .buildPlan({
        parentLoadId: parent.loadNumber,
        siblingLoadIds: siblings,
        corridorCode: this.candidates()?.corridorCode,
      })
      .subscribe({
        next: (plan) => {
          this.previewPlan.set(plan);
          this.previewLoading.set(false);
        },
        error: (err) => {
          this.error.set(this.messageOf(err, 'Could not build the plan preview.'));
          this.previewLoading.set(false);
        },
      });
  }

  /**
   * Fetches the parent equipment's preferred pairing from the read-only dispatch planner. Uses the
   * parent arrival's Alvys truck/trailer ids (driver is name-only on the board, so it is not sent).
   * Non-blocking and best-effort: a failure leaves the chips absent rather than erroring the review.
   */
  private loadPreferredPairing(parent: LaredoArrival): void {
    const truckId = parent.truck?.id ?? null;
    const trailerId = parent.trailer?.id ?? null;
    this.preferredPairing.set(null);
    if (!truckId && !trailerId) return;
    this.dispatchPlanner.getPreferredPairing({ truckId, trailerId }).subscribe({
      next: (view) => this.preferredPairing.set(view),
      error: () => this.preferredPairing.set(null),
    });
  }

  /**
   * Fetches yard presence for the parent equipment (issue #166). Best-effort and non-blocking: a
   * transport failure leaves an honest grey "unavailable" chip rather than erroring the review. The
   * backend already returns a grey shape when the Yard integration is off, so no client gate is needed.
   */
  private loadPresence(parent: LaredoArrival): void {
    const truckId = parent.truck?.id ?? undefined;
    const trailerId = parent.trailer?.id ?? undefined;
    this.yardPresence.set(null);
    this.presenceLoading.set(true);
    this.dock.getPresence(truckId, trailerId).subscribe({
      next: (view) => {
        this.yardPresence.set(view);
        this.presenceLoading.set(false);
      },
      error: () => {
        // Honest unavailable rather than a fabricated pass.
        this.yardPresence.set({
          configured: true,
          available: false,
          onRecord: false,
          atYard: false,
          driverPresent: false,
          securityHold: false,
        });
        this.presenceLoading.set(false);
      },
    });
  }

  protected backToSiblings(): void {
    this.previewPlan.set(null);
    this.preferredPairing.set(null);
    this.yardPresence.set(null);
    this.presenceLoading.set(false);
    this.step.set('siblings');
  }

  // --- Print / copy outputs ------------------------------------------------

  protected print(mode: Exclude<PrintMode, 'none'>): void {
    this.printMode.set(mode);
    // Let the print-only host class apply before the print dialog snapshots the page.
    setTimeout(() => {
      if (typeof window !== 'undefined' && typeof window.print === 'function') {
        window.print();
      }
      this.printMode.set('none');
    }, 0);
  }

  /**
   * Downloads the combined BOL packet as a real server-side PDF (the "Download PDF" companion to
   * Print). Read-only against Alvys — the endpoint rebuilds the plan and renders it, recording
   * nothing. On any failure it falls back to a legible message; the print view stays available.
   */
  protected downloadPdf(): void {
    const parent = this.parent();
    const siblings = this.selectedSiblingIds();
    if (!parent?.loadNumber || siblings.length === 0 || this.pdfDownloading()) return;

    this.pdfDownloading.set(true);
    this.copyMessage.set(null);
    this.dock
      .downloadBolPacket({
        parentLoadId: parent.loadNumber,
        siblingLoadIds: siblings,
        corridorCode: this.candidates()?.corridorCode,
        warehouseCode: this.selectedWarehouse()?.code,
      })
      .subscribe({
        next: (blob) => {
          this.saveBlob(blob, `bol-packet-${parent.loadNumber}.pdf`);
          this.pdfDownloading.set(false);
        },
        error: () => {
          this.copyMessage.set('PDF generation failed — use Print BOL packet instead.');
          this.pdfDownloading.set(false);
        },
      });
  }

  private saveBlob(blob: Blob, fileName: string): void {
    if (typeof window === 'undefined' || typeof URL?.createObjectURL !== 'function') return;
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
  }

  /**
   * Opens the Alvys click card in a new tab so the dispatcher can execute it alongside Alvys. Honest
   * no-op when there is no card text, no window (SSR), or the browser blocked the pop-up — the
   * on-screen click card and Copy button remain the fallback. Writes nothing to Alvys.
   */
  private openClickCardTab(): void {
    const text = this.plan()?.clickCard.plainText;
    if (!text) return;
    if (typeof window === 'undefined' || typeof window.open !== 'function') return;
    const tab = window.open('', '_blank');
    if (!tab) return; // pop-up blocked — the on-screen click card stays available
    tab.document.title = 'Alvys Click Card';
    const pre = tab.document.createElement('pre');
    pre.textContent = text;
    tab.document.body.appendChild(pre);
  }

  protected async copyClickCard(): Promise<void> {
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

  protected startOver(): void {
    this.parent.set(null);
    this.candidates.set(null);
    this.selectedSiblingIds.set([]);
    this.combineResult.set(null);
    this.previewPlan.set(null);
    this.preferredPairing.set(null);
    this.yardPresence.set(null);
    this.presenceLoading.set(false);
    this.notification.set(null);
    this.undone.set(false);
    this.stopUndoWindow();
    this.tapCount.set(0);
    this.parentTappedAt = null;
    this.copyMessage.set(null);
    this.error.set(null);
    // Keep the worker's Auto toggle as-is, but clear this run's auto state so a fresh cascade can run.
    this.autoEjectReason.set(null);
    this.autoReady.set(false);
    this.autoUsed.set(false);
    this.autoFinishRequested = false;
    this.step.set('arrivals');
    this.loadArrivals();
  }

  // --- Formatting helpers --------------------------------------------------

  protected formatCurrency(value: number | null | undefined): string {
    if (value === null || value === undefined || Number.isNaN(value)) return '—';
    return `$${value.toLocaleString(undefined, { minimumFractionDigits: 0, maximumFractionDigits: 0 })}`;
  }

  protected formatRpm(value: number | null | undefined): string {
    if (value === null || value === undefined || Number.isNaN(value)) return '—';
    return `$${value.toFixed(2)} / mi`;
  }

  protected formatWeight(value: number | null | undefined): string {
    if (value === null || value === undefined || Number.isNaN(value)) return '—';
    return `${value.toLocaleString()} lb`;
  }

  /** HH:MM (local) for a yard release time; empty string when unparseable — never a fabricated time. */
  protected formatTimeHm(iso: string | null | undefined): string {
    if (!iso) return '';
    const ms = Date.parse(iso);
    if (Number.isNaN(ms)) return '';
    return new Date(ms).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
  }

  protected tierLabel(tier: string): string {
    switch (tier) {
      case 'Allowed':
        return 'Consolidation allowed';
      case 'NotifyRequired':
        return 'Notify customer';
      case 'Never':
        return 'Never consolidate';
      default:
        return 'Confirm with account owner';
    }
  }

  private messageOf(err: unknown, fallback: string): string {
    const e = err as { error?: { error?: string }; message?: string };
    return e?.error?.error ?? e?.message ?? fallback;
  }
}
