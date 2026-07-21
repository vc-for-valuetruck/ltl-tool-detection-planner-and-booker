import { CommonModule, DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { LaredoArrival, LaredoArrivalsBoard } from './arrivals.models';
import {
  ConsolidationCandidate,
  ConsolidationCandidateResponse,
  WarehouseSummary,
} from './consolidation.models';
import { DockCombineResponse } from './dock.models';
import { DockService } from './dock.service';
import { LtlNav } from './ltl-nav';

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
  imports: [CommonModule, FormsModule, DatePipe, LtlNav],
  templateUrl: './dock.html',
  styleUrls: ['./dock.css'],
  host: { '[class.printing-bol]': "printMode() === 'bol'", '[class.printing-card]': "printMode() === 'clickcard'" },
})
export class Dock implements OnInit {
  private readonly dock = inject(DockService);

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

  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly copyMessage = signal<string | null>(null);

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
  }

  protected addManualSibling(): void {
    const id = this.manualSiblingId().trim();
    if (!id) return;
    if (!this.selectedSiblingIds().includes(id)) {
      this.selectedSiblingIds.set([...this.selectedSiblingIds(), id]);
    }
    this.manualSiblingId.set('');
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

  protected combine(): void {
    const parent = this.parent();
    const siblings = this.selectedSiblingIds();
    if (!parent?.loadNumber || siblings.length === 0) return;

    this.loading.set(true);
    this.error.set(null);
    this.dock
      .combine({
        parentLoadId: parent.loadNumber,
        siblingLoadIds: siblings,
        corridorCode: this.candidates()?.corridorCode,
      })
      .subscribe({
        next: (res) => {
          this.combineResult.set(res);
          this.loading.set(false);
          this.step.set('result');
        },
        error: (err) => {
          this.error.set(this.messageOf(err, 'Combine failed — nothing was recorded.'));
          this.loading.set(false);
        },
      });
  }

  protected goToReview(): void {
    if (this.hasSelection()) this.step.set('review');
  }

  protected backToSiblings(): void {
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
