import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { LtlService } from './ltl.service';
import { LtlStatusChip } from './ltl-status-chip';
import { LtlLoadSummary } from './ltl.models';

/**
 * Exceptions tab (issue #79). Read-only view over `GET /api/ltl/exceptions` — loads carrying one
 * or more exception flags, with the flags that block billing called out. Nothing here writes to
 * Alvys; the list reflects live Alvys reads exactly.
 */
@Component({
  selector: 'app-ltl-exceptions',
  standalone: true,
  imports: [DatePipe, RouterLink, LtlStatusChip],
  templateUrl: './ltl-exceptions.html',
  styleUrls: ['./ltl-worklist.css'],
})
export class LtlExceptions implements OnInit {
  private readonly ltl = inject(LtlService);

  protected readonly loads = signal<LtlLoadSummary[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  /** Active type filter chip. 'all' shows every exception-bearing load. */
  protected readonly typeFilter = signal<'all' | 'LateDelivery' | 'StuckAtStop'>('all');

  private readonly hasCode = (l: LtlLoadSummary, code: string) =>
    l.exceptions.some((e) => e.code === code);

  protected readonly lateDeliveryCount = computed(
    () => this.loads().filter((l) => this.hasCode(l, 'LateDelivery')).length,
  );
  protected readonly stuckStopCount = computed(
    () => this.loads().filter((l) => this.hasCode(l, 'StuckAtStop')).length,
  );

  /** Loads after applying the active type filter chip. */
  protected readonly filteredLoads = computed(() => {
    const filter = this.typeFilter();
    if (filter === 'all') return this.loads();
    return this.loads().filter((l) => this.hasCode(l, filter));
  });

  protected readonly hasLoads = computed(() => this.filteredLoads().length > 0);
  protected readonly blockingCount = computed(
    () => this.filteredLoads().filter((l) => l.exceptions.some((e) => e.blocksBilling)).length,
  );

  protected setFilter(filter: 'all' | 'LateDelivery' | 'StuckAtStop'): void {
    this.typeFilter.set(filter);
  }

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.ltl.exceptions().subscribe({
      next: (loads) => {
        this.loads.set(loads ?? []);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err?.error?.error ?? err?.message ?? "Couldn't reach Alvys.");
        this.loading.set(false);
      },
    });
  }
}
