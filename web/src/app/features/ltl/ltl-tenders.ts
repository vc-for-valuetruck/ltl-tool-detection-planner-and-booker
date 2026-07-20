import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { AlvysTendersService } from './alvys-tenders.service';
import { AlvysTender } from './alvys-tenders.models';
import { LtlNav } from './ltl-nav';

/** A tender projected for the board: raw Alvys tender + the fields the row needs, derived once. */
interface TenderRow {
  tender: AlvysTender;
  reference: string;
  customer: string;
  origin: string;
  destination: string;
  equipment: string;
  weight: number | null;
  rate: number | null;
  expiresAt: number | null;
}

/**
 * Tenders board (issue #79). Read-only pass-through of inbound EDI offers from
 * `POST /api/alvys/tenders/search` (Status=New). Ordered by expiration soonest-first because
 * accept/reject is time-critical — many tenders expire within ~an hour of import. Numeric wire
 * fields (weight/rate) arrive as EDI strings from Alvys and are normalized to numbers server-side;
 * anything missing renders as "—", never coerced. Nothing here writes to Alvys.
 */
@Component({
  selector: 'app-ltl-tenders',
  standalone: true,
  imports: [LtlNav],
  templateUrl: './ltl-tenders.html',
  styleUrls: ['./ltl-worklist.css'],
})
export class LtlTenders implements OnInit {
  private readonly tenders = inject(AlvysTendersService);

  protected readonly rows = signal<TenderRow[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly now = signal(Date.now());

  protected readonly hasRows = computed(() => this.rows().length > 0);
  protected readonly expiringSoonCount = computed(
    () => this.rows().filter((r) => this.urgency(r) === 'danger').length,
  );

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.now.set(Date.now());
    this.tenders.search({ Page: 0, PageSize: 100, Filter: { Status: ['New'] } }).subscribe({
      next: (res) => {
        const rows = (res?.Items ?? []).map((t) => this.toRow(t));
        rows.sort(this.byExpiration);
        this.rows.set(rows);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err?.error?.error ?? err?.message ?? "Couldn't reach Alvys.");
        this.loading.set(false);
      },
    });
  }

  /** Soonest expiry first; tenders with no expiration sort last (can't prioritize the unknown). */
  private byExpiration = (a: TenderRow, b: TenderRow): number => {
    if (a.expiresAt === null && b.expiresAt === null) return 0;
    if (a.expiresAt === null) return 1;
    if (b.expiresAt === null) return -1;
    return a.expiresAt - b.expiresAt;
  };

  private toRow(t: AlvysTender): TenderRow {
    const pickup = this.stopByType(t, 'Pickup') ?? this.entityByQualifier(t, 'SF');
    const delivery = this.stopByType(t, 'Delivery') ?? this.entityByQualifier(t, 'ST');
    const expiry = t.ExpirationDate?.DateTime ? Date.parse(t.ExpirationDate.DateTime) : NaN;
    return {
      tender: t,
      reference: t.LoadNumber || t.ShipmentId || t.Id,
      customer: this.entityByQualifier(t, 'BT') ?? '—',
      origin: pickup ?? '—',
      destination: delivery ?? '—',
      equipment: this.equipmentLabel(t),
      weight: t.Weight ?? null,
      rate: t.Rate ?? null,
      expiresAt: Number.isNaN(expiry) ? null : expiry,
    };
  }

  private stopByType(t: AlvysTender, type: string): string | undefined {
    const stop = (t.Stops ?? []).find((s) => (s.Type ?? '').toLowerCase() === type.toLowerCase());
    return stop?.Entity ? this.place(stop.Entity.City, stop.Entity.State) : undefined;
  }

  private entityByQualifier(t: AlvysTender, qualifier: string): string | undefined {
    const e = (t.Entities ?? []).find((x) => x.N1Qualifier === qualifier);
    if (!e) return undefined;
    if (qualifier === 'BT') return e.Name || e.CompanyName || undefined;
    return this.place(e.City, e.State);
  }

  private place(city?: string | null, state?: string | null): string | undefined {
    const parts = [city?.trim(), state?.trim()].filter((p): p is string => !!p);
    return parts.length ? parts.join(', ') : undefined;
  }

  private equipmentLabel(t: AlvysTender): string {
    const type = t.Equipment?.Type?.trim();
    const len = t.Equipment?.Length;
    if (type && len) return `${type} · ${len}'`;
    if (type) return type;
    if (len) return `${len}'`;
    return '—';
  }

  protected urgency(r: TenderRow): 'danger' | 'warn' | 'neutral' {
    if (r.expiresAt === null) return 'neutral';
    const minutes = (r.expiresAt - this.now()) / 60000;
    if (minutes <= 60) return 'danger';
    if (minutes <= 240) return 'warn';
    return 'neutral';
  }

  protected expiryLabel(r: TenderRow): string {
    if (r.expiresAt === null) return 'No expiry';
    const minutes = Math.round((r.expiresAt - this.now()) / 60000);
    if (minutes < 0) return 'Expired';
    if (minutes < 60) return `${minutes}m left`;
    const hours = Math.floor(minutes / 60);
    const rem = minutes % 60;
    return rem ? `${hours}h ${rem}m left` : `${hours}h left`;
  }

  protected formatWeight(value: number | null): string {
    if (value === null || value === undefined) return '—';
    return `${value.toLocaleString(undefined, { maximumFractionDigits: 0 })} lb`;
  }

  protected formatRate(value: number | null): string {
    if (value === null || value === undefined) return '—';
    return `$${value.toLocaleString(undefined, { maximumFractionDigits: 0 })}`;
  }
}
