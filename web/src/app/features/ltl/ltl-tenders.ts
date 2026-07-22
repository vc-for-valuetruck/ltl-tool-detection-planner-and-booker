import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { AlvysTendersService } from './alvys-tenders.service';
import { AlvysTender, AlvysTenderOrderDetail } from './alvys-tenders.models';

/** A tender projected for the board: raw Alvys tender + the fields the row needs, derived once. */
interface TenderRow {
  tender: AlvysTender;
  reference: string;
  customer: string;
  origin: string;
  destination: string;
  equipment: string;
  weight: number | null;
  /** Total pieces from the EDI order lines (Orders[].Quantity), or null when none is on file. */
  pieces: number | null;
  /** Shipment volume in cubic feet — tender aggregate, else summed from the order lines. */
  volumeCuFt: number | null;
  /**
   * Pallet positions on the tender. `count` is the counted EDI figure (QtyPallets) when present,
   * otherwise a rough estimate derived from volume. `estimated` says which — the UI badges an
   * estimate as "est." and never presents a volume-derived figure as a counted pallet. Null count
   * means neither a counted nor an estimable value is on file.
   */
  pallets: { count: number; estimated: boolean } | null;
  rate: number | null;
  expiresAt: number | null;
}

/**
 * Assumed cube of one loaded 48"×40" pallet, in cubic feet, used only to turn a shipment's EDI
 * volume into a rough pallet-position estimate. Deliberately generous (a floor-to-ceiling stack)
 * so the estimate does not understate deck usage. Every pallet figure derived from it is labelled
 * "est." in the UI — it is never presented as a counted pallet.
 */
const CUBIC_FEET_PER_PALLET = 96;

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
  imports: [],
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
    const volumeCuFt = this.volumeCuFt(t);
    return {
      tender: t,
      reference: t.LoadNumber || t.ShipmentId || t.Id,
      customer: this.entityByQualifier(t, 'BT') ?? '—',
      origin: pickup ?? '—',
      destination: delivery ?? '—',
      equipment: this.equipmentLabel(t),
      weight: t.Weight ?? null,
      pieces: this.pieces(t),
      volumeCuFt,
      pallets: this.pallets(t, volumeCuFt),
      rate: t.Rate ?? null,
      expiresAt: Number.isNaN(expiry) ? null : expiry,
    };
  }

  /** Every EDI order line across the tender's stops. */
  private orderLines(t: AlvysTender): AlvysTenderOrderDetail[] {
    return (t.Stops ?? []).flatMap((s) => s.Orders ?? []);
  }

  /** Total pieces = sum of EDI order-line quantities; null when no line reports a quantity. */
  private pieces(t: AlvysTender): number | null {
    const qtys = this.orderLines(t)
      .map((o) => o.Quantity)
      .filter((q): q is number => typeof q === 'number');
    return qtys.length ? qtys.reduce((a, b) => a + b, 0) : null;
  }

  /** Volume in cubic feet: tender aggregate first, else summed from the order lines. */
  private volumeCuFt(t: AlvysTender): number | null {
    if (typeof t.Volume === 'number') return t.Volume;
    const vols = this.orderLines(t)
      .map((o) => o.Volume)
      .filter((v): v is number => typeof v === 'number');
    return vols.length ? vols.reduce((a, b) => a + b, 0) : null;
  }

  /**
   * Pallet positions: the counted EDI QtyPallets when present (estimated=false); otherwise a rough
   * volume-derived estimate (estimated=true). Null when neither is available.
   */
  private pallets(
    t: AlvysTender,
    volumeCuFt: number | null,
  ): { count: number; estimated: boolean } | null {
    if (typeof t.QtyPallets === 'number' && t.QtyPallets > 0) {
      return { count: t.QtyPallets, estimated: false };
    }
    if (volumeCuFt !== null && volumeCuFt > 0) {
      return { count: Math.ceil(volumeCuFt / CUBIC_FEET_PER_PALLET), estimated: true };
    }
    return null;
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

  protected formatPieces(value: number | null): string {
    if (value === null || value === undefined) return '—';
    return value.toLocaleString(undefined, { maximumFractionDigits: 0 });
  }

  protected formatVolume(value: number | null): string {
    if (value === null || value === undefined) return '—';
    return `${value.toLocaleString(undefined, { maximumFractionDigits: 0 })} ft³`;
  }

  protected formatPallets(pallets: TenderRow['pallets']): string {
    if (!pallets) return '—';
    const n = pallets.count.toLocaleString(undefined, { maximumFractionDigits: 0 });
    return pallets.estimated ? `${n} est.` : n;
  }

  /** Tooltip explaining where a pallet figure came from, so "est." is never mistaken for a count. */
  protected palletTitle(row: TenderRow): string {
    if (!row.pallets) return 'No pallet count or volume on the EDI tender.';
    if (!row.pallets.estimated) return 'Counted pallet positions from the EDI tender (QtyPallets).';
    return (
      `Estimated from EDI volume: ${this.formatVolume(row.volumeCuFt)} ÷ ` +
      `${CUBIC_FEET_PER_PALLET} ft³/pallet, rounded up. Not a counted pallet — verify at dock.`
    );
  }
}
