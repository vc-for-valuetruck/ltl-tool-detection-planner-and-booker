import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { LtlService } from './ltl.service';
import { BillingBadge, LtlLoadSummary, MissingDataFlag } from './ltl.models';
import { LtlNav } from './ltl-nav';

interface BadgeFilter {
  value: BillingBadge | null;
  label: string;
}

/**
 * Billing worklist tab (issue #79). Read-only view over `GET /api/ltl/billing/worklist` — the
 * readiness-first, risk-visible list of loads needing billing attention, filterable by badge.
 * Every value is rendered exactly as Alvys supplied it; missing money/weight shows as "—", never
 * coerced to zero. Nothing here writes to Alvys.
 */
@Component({
  selector: 'app-ltl-billing',
  standalone: true,
  imports: [DatePipe, LtlNav],
  templateUrl: './ltl-billing.html',
  styleUrls: ['./ltl-worklist.css'],
})
export class LtlBilling implements OnInit {
  private readonly ltl = inject(LtlService);

  protected readonly loads = signal<LtlLoadSummary[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly activeBadge = signal<BillingBadge | null>(null);

  protected readonly hasLoads = computed(() => this.loads().length > 0);

  protected readonly badgeFilters: BadgeFilter[] = [
    { value: null, label: 'All' },
    { value: 'ReadyToBill', label: 'Ready to Bill' },
    { value: 'MissingRate', label: 'Missing Rate' },
    { value: 'MissingPod', label: 'Missing POD' },
    { value: 'MissingWeight', label: 'Missing Weight' },
    { value: 'MissingAccessorialReview', label: 'Missing Accessorial Review' },
    { value: 'PossibleUnbilledAccessorial', label: 'Possible Unbilled Accessorial' },
    { value: 'CarrierAccessorialMismatch', label: 'Carrier Accessorial Mismatch' },
    { value: 'InvoiceAmountDrift', label: 'Invoice Amount Drift' },
    { value: 'CustomerReviewNeeded', label: 'Customer Review Needed' },
    { value: 'ExceptionBlockingBilling', label: 'Exception Blocking Billing' },
    { value: 'AlreadyInvoiced', label: 'Already Invoiced' },
  ];

  ngOnInit(): void {
    this.load();
  }

  protected selectBadge(badge: BillingBadge | null): void {
    if (this.activeBadge() === badge) return;
    this.activeBadge.set(badge);
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.ltl.billingWorklist(this.activeBadge() ?? undefined).subscribe({
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

  protected badgeLabel(badge: BillingBadge): string {
    return LtlBilling.BADGE_LABELS[badge] ?? badge;
  }

  /** Ready-to-bill reads positive; everything else is a risk/attention chip. */
  protected badgeClass(badge: BillingBadge): string {
    if (badge === 'ReadyToBill') return 'chip chip-good';
    if (badge === 'AlreadyInvoiced') return 'chip chip-neutral';
    if (badge === 'ExceptionBlockingBilling') return 'chip chip-danger';
    return 'chip chip-warn';
  }

  protected missingLabel(flag: MissingDataFlag): string {
    return LtlBilling.MISSING_LABELS[flag] ?? flag;
  }

  protected formatCurrency(value: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    return `$${value.toLocaleString(undefined, { maximumFractionDigits: 0 })}`;
  }

  private static readonly BADGE_LABELS: Record<BillingBadge, string> = {
    ReadyToBill: 'Ready to Bill',
    MissingRate: 'Missing Rate',
    MissingPod: 'Missing POD',
    MissingAccessorialReview: 'Missing Accessorial Review',
    MissingWeight: 'Missing Weight',
    CustomerReviewNeeded: 'Customer Review Needed',
    ExceptionBlockingBilling: 'Exception Blocking Billing',
    AlreadyInvoiced: 'Already Invoiced',
    PossibleUnbilledAccessorial: 'Possible Unbilled Accessorial',
    CarrierAccessorialMismatch: 'Carrier Accessorial Mismatch',
    InvoiceAmountDrift: 'Invoice Amount Drift',
  };

  private static readonly MISSING_LABELS: Record<MissingDataFlag, string> = {
    Customer: 'Customer',
    Rate: 'Rate',
    Pod: 'POD',
    Weight: 'Weight',
    AccessorialReview: 'Accessorial Review',
    Mileage: 'Mileage',
    Origin: 'Origin',
    Destination: 'Destination',
    PickupDate: 'Pickup Date',
    DeliveryDate: 'Delivery Date',
    Equipment: 'Equipment',
    Commodity: 'Commodity',
    InvoiceStatus: 'Invoice Status',
    Dimensions: 'Dimensions',
  };
}
