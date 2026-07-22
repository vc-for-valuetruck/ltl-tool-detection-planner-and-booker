import { CommonModule, DatePipe } from '@angular/common';
import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
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
import { DockCombineResponse, DockNotificationResult } from './dock.models';
import { DockService } from './dock.service';
import { LtlParentChildBadge } from './ltl-parent-child-badge';

/** The dock-worker flow is a linear step machine; each value is one screen in the wizard. */
type DockStep = 'warehouse' | 'arrivals' | 'siblings' | 'review' | 'result';

/** Which print-ready artifact is currently armed for printing (drives the print-only host class). */
type PrintMode = 'none' | 'bol' | 'clickcard';

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
  imports: [CommonModule, FormsModule, DatePipe, LtlParentChildBadge],
  templateUrl: './dock.html',
  styleUrls: ['./dock.css'],
  host: { '[class.printing-bol]': "printMode() === 'bol'", '[class.printing-card]': "printMode() === 'clickcard'" },
})
export class Dock implements OnInit, OnDestroy {
  private readonly dock = inject(DockService);
  private readonly consolidation = inject(ConsolidationService);
  private readonly dispatchPlanner = inject(DispatchPlannerService);

  /** How long the one-tap Undo stays offered after a combine, in seconds (a few minutes). */
  private static readonly UndoWindowSeconds = 180;

  protected readonly step = signal<DockStep>('warehouse');

  protected readonly warehouses = signal<WarehouseSummary[]>([]);
  protected readonly selectedWarehouse = signal<WarehouseSummary | null>(null);

  protected readonly board = signal<LaredoArrivalsBoard | null>(null);
  protected readonly parent = signal<LaredoArrival | null>(null);

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

  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly copyMessage = signal<string | null>(null);

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

  private static readonly stepOrder: DockStep[] = ['warehouse', 'arrivals', 'siblings', 'review', 'result'];
  protected readonly stepIndex = computed(() => Dock.stepOrder.indexOf(this.step()));

  protected readonly arrivals = computed(() => this.board()?.arrivals ?? []);
  protected readonly candidateList = computed(() => this.candidates()?.candidates ?? []);
  protected readonly selectableCandidates = computed(() =>
    this.candidateList().filter((c) => !c.isBlocked),
  );
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
        },
        error: (err) => {
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

  protected backToSiblings(): void {
    this.previewPlan.set(null);
    this.preferredPairing.set(null);
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
    this.notification.set(null);
    this.undone.set(false);
    this.stopUndoWindow();
    this.tapCount.set(0);
    this.parentTappedAt = null;
    this.copyMessage.set(null);
    this.error.set(null);
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
