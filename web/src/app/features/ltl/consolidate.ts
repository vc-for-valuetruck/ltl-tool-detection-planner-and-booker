import { CommonModule } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { ConsolidationService } from './consolidation.service';
import {
  ConsolidationAuditRecord,
  ConsolidationCandidate,
  ConsolidationCandidateResponse,
  ConsolidationFactor,
  ConsolidationFit,
  ConsolidationPlanResponse,
  CorridorHealth,
  CorridorSummary,
} from './consolidation.models';

/** Merged view of a corridor + its live open-load count, ready to render in the picker. */
export interface CorridorPickerRow {
  code: string;
  originName: string;
  destinationName: string;
  /**
   * `null` when the corridors-health call degraded or hasn't completed yet. Distinct from
   * `0` (which means "we asked Alvys and there are no open loads right now").
   */
  openLoadCount: number | null;
  loadedCleanly: boolean;
}

/**
 * Phase 1 pilot: Laredo → Dallas consolidation planner. Three screens on one route:
 *   1. Enter a seed load id → see corridor-matching sibling candidates with per-factor chips
 *   2. Select siblings → build a plan preview with projected combined RPM
 *   3. Copy the sanctioned Alvys click card + record the plan as an audit entry
 *
 * Read-only end-to-end. Nothing on this screen writes to Alvys — the click card is text the
 * dispatcher pastes into Alvys manually, exactly following Poornima's yard walkthrough with
 * Holly. Every value derives from live Alvys reads or from static config.
 */
@Component({
  selector: 'app-consolidate',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './consolidate.html',
  styleUrls: ['./consolidate.css'],
})
export class Consolidate implements OnInit {
  private readonly consolidation = inject(ConsolidationService);
  private readonly router = inject(Router);

  // Corridor picker (loaded once on init).
  readonly loadingCorridors = signal(true);
  readonly corridors = signal<CorridorPickerRow[]>([]);
  readonly selectedCorridor = signal<string>('LAREDO_TO_DALLAS');

  // Screen 1: candidate search
  readonly seedInput = signal('');
  readonly loadingCandidates = signal(false);
  readonly candidateError = signal<string | null>(null);
  readonly candidateResponse = signal<ConsolidationCandidateResponse | null>(null);
  readonly selectedSiblingIds = signal<Set<string>>(new Set<string>());

  // Screen 2: plan preview
  readonly loadingPlan = signal(false);
  readonly planError = signal<string | null>(null);
  readonly plan = signal<ConsolidationPlanResponse | null>(null);

  // Screen 3: audit
  readonly recordingAudit = signal(false);
  readonly auditError = signal<string | null>(null);
  readonly auditRecord = signal<ConsolidationAuditRecord | null>(null);
  readonly copyMessage = signal<string | null>(null);

  readonly hasSelection = computed(() => this.selectedSiblingIds().size > 0);
  readonly canBuildPlan = computed(
    () => !!this.candidateResponse()?.seed && this.hasSelection() && !this.loadingPlan(),
  );
  readonly canRecordAudit = computed(
    () => !!this.plan() && (this.plan()?.blockers.length ?? 0) === 0 && !this.recordingAudit(),
  );

  ngOnInit(): void {
    // Fire both calls in parallel. /corridors is static config and cheap; /corridors/health
    // hits Alvys per corridor. Merged into a single picker row so we can display counts
    // alongside labels the moment both land. If /corridors returns first and /health is
    // still in flight we render with openLoadCount=null ("…") and let it fill in.
    forkJoin({
      corridors: this.consolidation.getCorridors(),
      health: this.consolidation.getCorridorHealth(),
    }).subscribe({
      next: ({ corridors, health }) => {
        const healthByCode = new Map<string, CorridorHealth>(health.map(h => [h.code, h]));
        this.corridors.set(
          corridors.map(c => ({
            code: c.code,
            originName: c.origin.name,
            destinationName: c.destination.name,
            openLoadCount: healthByCode.get(c.code)?.openLoadCount ?? null,
            loadedCleanly: healthByCode.has(c.code) && healthByCode.get(c.code)?.openLoadCount !== null,
          })),
        );
        this.loadingCorridors.set(false);
      },
      error: () => {
        // Corridors are non-essential for the seed-based workflow — the picker's absence is
        // not fatal; the dispatcher can still type a seed and hit Find candidates. Degrade
        // silently and let the rest of the tab work.
        this.corridors.set([]);
        this.loadingCorridors.set(false);
      },
    });
  }

  selectCorridor(code: string): void {
    this.selectedCorridor.set(code);
    // Clear any stale candidates/plan when the corridor changes.
    this.candidateResponse.set(null);
    this.selectedSiblingIds.set(new Set<string>());
    this.plan.set(null);
  }

  loadCandidates(): void {
    const seed = this.seedInput().trim();
    if (!seed) return;
    this.loadingCandidates.set(true);
    this.candidateError.set(null);
    this.candidateResponse.set(null);
    this.selectedSiblingIds.set(new Set<string>());
    this.plan.set(null);
    this.planError.set(null);
    this.auditRecord.set(null);
    this.copyMessage.set(null);

    this.consolidation.getCandidates(seed, this.selectedCorridor()).subscribe({
      next: (response) => {
        this.candidateResponse.set(response);
        this.loadingCandidates.set(false);
      },
      error: (err) => {
        this.candidateError.set(err?.error?.error ?? err?.message ?? 'Failed to load candidates.');
        this.loadingCandidates.set(false);
      },
    });
  }

  toggleSibling(candidate: ConsolidationCandidate): void {
    if (candidate.isBlocked) return;
    const next = new Set(this.selectedSiblingIds());
    if (next.has(candidate.loadId)) next.delete(candidate.loadId);
    else next.add(candidate.loadId);
    this.selectedSiblingIds.set(next);
    // Clear a stale plan when the selection changes.
    this.plan.set(null);
    this.planError.set(null);
    this.auditRecord.set(null);
  }

  isSelected(loadId: string): boolean {
    return this.selectedSiblingIds().has(loadId);
  }

  buildPlan(): void {
    const seed = this.candidateResponse()?.seed;
    if (!seed) return;
    if (this.selectedSiblingIds().size === 0) return;

    this.loadingPlan.set(true);
    this.planError.set(null);
    this.plan.set(null);
    this.auditRecord.set(null);

    this.consolidation
      .buildPlan({
        parentLoadId: seed.id,
        siblingLoadIds: Array.from(this.selectedSiblingIds()),
      })
      .subscribe({
        next: (response) => {
          this.plan.set(response);
          this.loadingPlan.set(false);
        },
        error: (err) => {
          this.planError.set(err?.error?.error ?? err?.message ?? 'Failed to build plan preview.');
          this.loadingPlan.set(false);
        },
      });
  }

  recordAudit(): void {
    const seed = this.candidateResponse()?.seed;
    const plan = this.plan();
    if (!seed || !plan) return;

    this.recordingAudit.set(true);
    this.auditError.set(null);
    this.auditRecord.set(null);

    this.consolidation
      .recordPlanAudit({
        parentLoadId: seed.id,
        siblingLoadIds: plan.siblings.map((s) => s.loadId),
      })
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

  async copyClickCard(): Promise<void> {
    const text = this.plan()?.clickCard.plainText;
    if (!text) return;
    try {
      if (navigator?.clipboard?.writeText) {
        await navigator.clipboard.writeText(text);
        this.copyMessage.set('Copied. Paste into a note or into Alvys directly.');
      } else {
        this.copyMessage.set('Clipboard not available — select the card and copy manually.');
      }
    } catch {
      this.copyMessage.set('Copy failed — select the card and copy manually.');
    }
  }

  /**
   * Navigates to the routed Plan Detail screen for the current preview. The plan is re-fetched
   * there from live Alvys data via parent/sibling ids carried as query params — nothing is
   * cached across the route boundary, so a direct link (or refresh) still shows live data.
   */
  openPlanDetail(): void {
    const seed = this.candidateResponse()?.seed;
    const p = this.plan();
    if (!seed || !p) return;
    this.router.navigate(['/ltl/consolidate/plan', p.previewId], {
      queryParams: {
        parent: seed.id,
        siblings: p.siblings.map((s) => s.loadId).join(','),
        corridor: this.selectedCorridor(),
      },
    });
  }

  chipClass(fit: ConsolidationFit): string {
    switch (fit) {
      case 'Good':
        return 'chip chip-good';
      case 'Tight':
        return 'chip chip-tight';
      case 'Blocked':
        return 'chip chip-blocked';
      default:
        return 'chip chip-unknown';
    }
  }

  factorFit(candidate: ConsolidationCandidate, name: string): ConsolidationFactor | undefined {
    return candidate.factors.find((f) => f.name === name);
  }

  formatCurrency(value: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    return `$${value.toLocaleString(undefined, {
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    })}`;
  }

  formatNumber(value: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    return value.toLocaleString();
  }

  formatDate(value: string | undefined | null): string {
    if (!value) return '—';
    try {
      const d = new Date(value);
      return d.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
    } catch {
      return value;
    }
  }
}
