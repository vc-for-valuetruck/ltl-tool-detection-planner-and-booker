import { Component, Input, computed, signal } from '@angular/core';

/** Which side of a consolidation a load sits on, or null when it is not part of any plan. */
export type ParentChildRole = 'parent' | 'child' | null;

/**
 * One consistent parent/child marking used everywhere a consolidated load is shown: the dock flow,
 * the Consolidate board, plan-audited search rows, the printed BOL packet / dock manifest and the
 * Alvys click card. Rendering the same badge in every surface means a dock worker, a dispatcher and
 * accounting all read the same words for the same machine convention (the load references
 * `LTL="true"` + a Main Load Id pointing at the parent).
 *
 * - `parent` → "PARENT · BOL controlling"
 * - `child`  → "CHILD of L-XXXXXX" (always names the controlling parent so the link is never implicit)
 * - `null`   → renders nothing (a load not in any plan carries no badge)
 *
 * Tablet-legible by default; pass `dense` for tight rows. Never fabricates a parent label — when the
 * parent id is unknown a child badge falls back to a plain "CHILD" rather than inventing a number.
 */
@Component({
  selector: 'app-ltl-parent-child-badge',
  standalone: true,
  template: `
    @if (roleSig() === 'parent') {
      <span class="pc-badge pc-parent" [class.pc-dense]="dense" role="status">
        <span class="pc-dot" aria-hidden="true"></span>PARENT · BOL controlling
      </span>
    } @else if (roleSig() === 'child') {
      <span class="pc-badge pc-child" [class.pc-dense]="dense" role="status">
        <span class="pc-dot" aria-hidden="true"></span>{{ childLabel() }}
      </span>
    }
  `,
  styles: [
    `
      .pc-badge {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        font-size: 13px;
        font-weight: 700;
        line-height: 1;
        letter-spacing: 0.02em;
        padding: 6px 10px;
        border-radius: 999px;
        white-space: nowrap;
        border: 1px solid transparent;
      }

      .pc-badge.pc-dense {
        font-size: 11px;
        padding: 4px 8px;
      }

      .pc-dot {
        width: 7px;
        height: 7px;
        border-radius: 50%;
        background: currentColor;
      }

      .pc-parent {
        color: var(--accent, #1d4ed8);
        background: var(--accent-bg, rgba(29, 78, 216, 0.12));
        border-color: var(--accent-border, rgba(29, 78, 216, 0.35));
      }

      .pc-child {
        color: var(--text-secondary, #475569);
        background: var(--surface-muted, rgba(100, 116, 139, 0.12));
        border-color: var(--card-border, rgba(100, 116, 139, 0.3));
      }
    `,
  ],
})
export class LtlParentChildBadge {
  protected readonly roleSig = signal<ParentChildRole>(null);
  private readonly parentLabelSig = signal<string | null>(null);

  /** Which side of the consolidation this load sits on. */
  @Input({ required: true }) set role(value: ParentChildRole) {
    this.roleSig.set(value ?? null);
  }

  /** The controlling parent's load number / id, named on a child badge. */
  @Input() set parentLabel(value: string | null | undefined) {
    this.parentLabelSig.set(value && value.trim() ? value.trim() : null);
  }

  /** Compact styling for dense rows (search / candidate tables). */
  @Input() dense = false;

  protected readonly childLabel = computed(() => {
    const parent = this.parentLabelSig();
    return parent ? `CHILD of ${parent}` : 'CHILD';
  });
}
