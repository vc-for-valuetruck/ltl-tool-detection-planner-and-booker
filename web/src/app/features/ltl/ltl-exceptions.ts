import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { LtlService } from './ltl.service';
import { LtlLoadSummary } from './ltl.models';
import { LtlNav } from './ltl-nav';

/**
 * Exceptions tab (issue #79). Read-only view over `GET /api/ltl/exceptions` — loads carrying one
 * or more exception flags, with the flags that block billing called out. Nothing here writes to
 * Alvys; the list reflects live Alvys reads exactly.
 */
@Component({
  selector: 'app-ltl-exceptions',
  standalone: true,
  imports: [DatePipe, RouterLink, LtlNav],
  templateUrl: './ltl-exceptions.html',
  styleUrls: ['./ltl-worklist.css'],
})
export class LtlExceptions implements OnInit {
  private readonly ltl = inject(LtlService);

  protected readonly loads = signal<LtlLoadSummary[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected readonly hasLoads = computed(() => this.loads().length > 0);
  protected readonly blockingCount = computed(
    () => this.loads().filter((l) => l.exceptions.some((e) => e.blocksBilling)).length,
  );

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
