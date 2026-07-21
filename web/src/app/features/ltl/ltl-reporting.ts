import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { LtlService } from './ltl.service';
import { MarginRollupRow, RollupGroupBy } from './ltl.models';
import { LtlNav } from './ltl-nav';

interface GroupByOption {
  value: RollupGroupBy;
  label: string;
}

/**
 * Margin/exception rollup reporting tab: per-customer / per-rep / per-lane profitability
 * visibility over the same normalized load set the billing worklist uses. Read-only; entirely
 * Alvys-derived; no external BI/reporting connection. Rep grouping is labeled honestly as an
 * opaque Alvys id (no rep-name field exists) — never rendered as if it were a real name.
 */
@Component({
  selector: 'app-ltl-reporting',
  standalone: true,
  imports: [LtlNav],
  templateUrl: './ltl-reporting.html',
  styleUrls: ['./ltl-worklist.css'],
})
export class LtlReporting implements OnInit {
  private readonly ltl = inject(LtlService);

  protected readonly rows = signal<MarginRollupRow[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly truncated = signal(false);
  protected readonly groupBy = signal<RollupGroupBy>('Customer');

  protected readonly hasRows = computed(() => this.rows().length > 0);

  protected readonly groupByOptions: GroupByOption[] = [
    { value: 'Customer', label: 'By Customer' },
    { value: 'Rep', label: 'By Rep' },
    { value: 'Lane', label: 'By Lane' },
  ];

  ngOnInit(): void {
    this.load();
  }

  protected selectGroupBy(groupBy: RollupGroupBy): void {
    if (this.groupBy() === groupBy) return;
    this.groupBy.set(groupBy);
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.ltl.marginRollup(this.groupBy()).subscribe({
      next: (response) => {
        this.rows.set(response.rows ?? []);
        this.truncated.set(response.truncated);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err?.error?.error ?? err?.message ?? "Couldn't reach Alvys.");
        this.loading.set(false);
      },
    });
  }

  protected formatCurrency(value: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    return `$${value.toLocaleString(undefined, { maximumFractionDigits: 0 })}`;
  }

  protected formatPercent(value: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    return `${value}%`;
  }

  /** Thin/negative margin reads as risk; healthy margin reads positive; unknown stays neutral. */
  protected marginClass(row: MarginRollupRow): string {
    if (row.grossMarginPercent === null) return 'chip chip-neutral';
    if (row.grossMarginPercent < 0) return 'chip chip-danger';
    if (row.grossMarginPercent <= 10) return 'chip chip-warn';
    return 'chip chip-good';
  }
}
