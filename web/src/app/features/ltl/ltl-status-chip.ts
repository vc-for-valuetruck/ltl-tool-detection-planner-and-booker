import { Component, Input, computed, signal } from '@angular/core';

/**
 * The colour family a load/trip status maps to. Deliberately small and rooted in common TMS
 * conventions (Alvys, McLeod, DAT) so a dispatcher reads the same colour for the same meaning
 * on every surface. `unknown` is the honest fallback — an unrecognised status is never guessed
 * into a green "good" or a red "bad"; it stays neutral grey.
 */
export type StatusChipCategory =
  | 'transit'
  | 'delivered'
  | 'open'
  | 'assigned'
  | 'risk'
  | 'exception'
  | 'billing'
  | 'unknown';

/**
 * Map a free-form Alvys load/trip status (or an internal workflow-stage / assignment-state label)
 * to a colour category. Alvys statuses are free text, so this is keyword-based and case-insensitive.
 *
 * Priority order matters and is chosen to avoid collisions:
 *  1. `unassigned` is checked before `assign` (so "Unassigned" reads Open, not Assigned).
 *  2. exception → risk → billing before the movement states, so "Cancelled", "At Risk" and
 *     "Ready to Bill" win over any incidental keyword.
 *  3. transit before delivered, so "Out for Delivery" reads In-Transit (blue) rather than green.
 *
 * Returns `unknown` for null/blank/unrecognised input — the fail-honest default.
 */
export function statusChipCategory(raw: string | null | undefined): StatusChipCategory {
  const s = (raw ?? '').trim().toLowerCase();
  if (!s) return 'unknown';

  // Open must beat Assigned for "Unassigned".
  if (s.includes('unassign')) return 'open';

  // Exception / terminal-bad states.
  if (
    s.includes('cancel') ||
    s.includes('exception') ||
    s.includes('block') ||
    s.includes('tonu') ||
    s.includes('reject') ||
    s.includes('declin') ||
    s.includes('void') ||
    s.includes('problem') ||
    s.includes('fail')
  ) {
    return 'exception';
  }

  // At-risk / warning states.
  if (
    s.includes('risk') ||
    s.includes('delay') ||
    s.includes('late') ||
    s.includes('hold') ||
    s.includes('detention') ||
    s.includes('warn')
  ) {
    return 'risk';
  }

  // Billing lifecycle ("Bill", "Billed", "Billing", "Invoice(d)", "Ready to Bill").
  if (s.includes('bill') || s.includes('invoic')) return 'billing';

  // In-transit / movement (checked before Delivered so "Out for Delivery" stays blue).
  if (
    s.includes('transit') ||
    s.includes('en route') ||
    s.includes('enroute') ||
    s.includes('rolling') ||
    s.includes('out for delivery') ||
    s.includes('pick') ||
    s.includes('at shipper') ||
    s.includes('at consignee') ||
    s.includes('loading') ||
    s.includes('loaded') ||
    s.includes('arriv') ||
    s.includes('depart')
  ) {
    return 'transit';
  }

  // Delivered / completed.
  if (
    s.includes('deliver') ||
    s.includes('complete') ||
    s.includes('pod') ||
    s.includes('empty') ||
    s.includes('unload')
  ) {
    return 'delivered';
  }

  // Assigned / covered / dispatched.
  if (
    s.includes('assign') ||
    s.includes('cover') ||
    s.includes('dispatch') ||
    s.includes('book') ||
    s.includes('accept')
  ) {
    return 'assigned';
  }

  // Open / available / pre-assignment.
  if (
    s.includes('open') ||
    s.includes('avail') ||
    s.includes('new') ||
    s.includes('plan') ||
    s.includes('quote') ||
    s.includes('tender') ||
    s.includes('pending')
  ) {
    return 'open';
  }

  return 'unknown';
}

/**
 * One shared, colour-coded status chip used everywhere a load/trip status renders (load-detail
 * header, planner grid, assignments, dock arrivals, consolidate candidates, billing/exceptions).
 * Soft-tinted background with darker text — the muted "pill" convention Alvys uses, not saturated
 * fills — and every colour pair meets WCAG AA. Renders the status text verbatim; never rewrites or
 * invents a value. Blank input renders a neutral "—".
 */
@Component({
  selector: 'app-ltl-status-chip',
  standalone: true,
  template: `
    <span
      class="status-chip"
      [class]="'status-chip status-chip--' + category()"
      [class.status-chip--dense]="dense"
      [attr.data-status-category]="category()"
      [title]="text()"
      role="status"
    >
      {{ text() }}
    </span>
  `,
  styles: [
    `
      .status-chip {
        display: inline-flex;
        align-items: center;
        font-size: 12px;
        font-weight: 600;
        line-height: 1;
        letter-spacing: 0.01em;
        padding: 5px 10px;
        border-radius: 999px;
        white-space: nowrap;
        border: 1px solid transparent;
      }

      .status-chip--dense {
        font-size: 11px;
        padding: 3px 8px;
      }

      .status-chip--transit {
        background: var(--info-bg, #dbeafe);
        color: var(--info-text, #1e40af);
        border-color: rgba(30, 64, 175, 0.25);
      }

      .status-chip--delivered {
        background: var(--success-bg, #d1fae5);
        color: var(--success-text, #065f46);
        border-color: rgba(6, 95, 70, 0.25);
      }

      .status-chip--risk {
        background: var(--warning-bg, #fef3c7);
        color: var(--warning-text, #92400e);
        border-color: rgba(146, 64, 14, 0.25);
      }

      .status-chip--exception {
        background: var(--danger-bg, #fee2e2);
        color: var(--danger-text, #991b1b);
        border-color: rgba(153, 27, 27, 0.25);
      }

      .status-chip--open {
        background: var(--status-open-bg, #f1f5f9);
        color: var(--status-open-text, #334155);
        border-color: var(--status-open-border, #cbd5e1);
      }

      .status-chip--assigned {
        background: var(--status-assigned-bg, #e0e7ff);
        color: var(--status-assigned-text, #3730a3);
        border-color: var(--status-assigned-border, #c7d2fe);
      }

      .status-chip--billing {
        background: var(--status-billing-bg, #f3e8ff);
        color: var(--status-billing-text, #6b21a8);
        border-color: var(--status-billing-border, #e9d5ff);
      }

      .status-chip--unknown {
        background: var(--status-unknown-bg, #f3f4f6);
        color: var(--status-unknown-text, #4b5563);
        border-color: var(--status-unknown-border, #e5e7eb);
      }
    `,
  ],
})
export class LtlStatusChip {
  private readonly statusSig = signal<string | null>(null);

  /** The raw status text to display and colour (load status, workflow stage, assignment state). */
  @Input({ required: true }) set status(value: string | null | undefined) {
    this.statusSig.set(value && value.trim() ? value.trim() : null);
  }

  /** Compact styling for dense grid rows. */
  @Input() dense = false;

  protected readonly text = computed(() => this.statusSig() ?? '—');
  protected readonly category = computed(() => statusChipCategory(this.statusSig()));
}
