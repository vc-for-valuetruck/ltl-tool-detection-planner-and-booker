import { DatePipe } from '@angular/common';
import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { interval } from 'rxjs';
import { LtlService } from './ltl.service';
import { ArrivalStatus, LaredoArrival, LaredoArrivalsBoard } from './arrivals.models';

/**
 * Phase 8.1 Laredo Arrivals Board — the FIRST surface Ben Beddes / Jordan Baumgart see on `/ltl`.
 * Every truck/trailer scheduled to arrive at the Laredo yard today, read live and read-only from
 * Alvys trips. Dallas-bound freight is highlighted and sorted first because that is the pilot
 * Laredo → Dallas LTL opportunity. A row click opens the trip's load; the "LTL opportunity" action
 * lands on the pre-filtered Laredo → Dallas Consolidate corridor.
 *
 * Nothing here is fabricated: an arrival window, driver, equipment or ownership Alvys does not
 * carry renders as "—" / Unknown. Refreshes every 30s so status chips stay live without a reload.
 */
@Component({
  selector: 'app-ltl-arrivals-board',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './ltl-arrivals-board.html',
  styleUrls: ['./ltl-arrivals-board.css'],
})
export class LtlArrivalsBoard implements OnInit {
  private readonly ltl = inject(LtlService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  private static readonly RefreshMs = 30_000;

  protected readonly board = signal<LaredoArrivalsBoard | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected readonly arrivals = computed(() => this.board()?.arrivals ?? []);
  protected readonly dallasBoundCount = computed(
    () => this.arrivals().filter((a) => a.dallasBound).length,
  );
  protected readonly hasArrivals = computed(() => this.arrivals().length > 0);

  ngOnInit(): void {
    this.load(true);
    // Poll so the Scheduled → Arrived → Departed chips stay live without a manual reload. Silent
    // refreshes never flip the skeleton back on, so the board doesn't flicker under the dispatcher.
    interval(LtlArrivalsBoard.RefreshMs)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.load(false));
  }

  protected load(showSkeleton: boolean): void {
    if (showSkeleton) this.loading.set(true);
    this.ltl.arrivals().subscribe({
      next: (board) => {
        this.board.set(board);
        this.error.set(null);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err?.error?.error ?? err?.message ?? `Couldn't reach Alvys.`);
        this.loading.set(false);
      },
    });
  }

  /** Opens the trip's load detail when Alvys carries a load number; no-op otherwise. */
  protected openLoad(arrival: LaredoArrival): void {
    if (!arrival.loadNumber) return;
    this.router.navigate(['/ltl/loads', arrival.loadNumber]);
  }

  /**
   * Lands on the pilot Laredo → Dallas Consolidate corridor (its default selection), pre-filtered
   * — the sanctioned LTL opportunity for a Dallas-bound arrival.
   */
  protected openConsolidate(event: Event): void {
    event.stopPropagation();
    this.router.navigate(['/ltl/consolidate']);
  }

  protected statusClass(status: ArrivalStatus): string {
    switch (status) {
      case 'Arrived':
        return 'chip chip-arrived';
      case 'Departed':
        return 'chip chip-departed';
      default:
        return 'chip chip-scheduled';
    }
  }

  protected ownershipLabel(ownership: string): string {
    switch (ownership) {
      case 'Fleet':
        return 'Fleet';
      case 'ThirdPartyLeased':
        return '3P-leased';
      default:
        return 'Unknown';
    }
  }

  protected onwardLabel(arrival: LaredoArrival): string {
    const labels = arrival.onwardStops.map((s) => s.label).filter((l): l is string => !!l);
    return labels.length > 0 ? labels.join(' → ') : '—';
  }
}
