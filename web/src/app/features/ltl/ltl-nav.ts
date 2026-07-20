import { Component, Input } from '@angular/core';
import { RouterLink } from '@angular/router';

/**
 * Shared LTL console tab strip. Rendered on every LTL screen (landing, consolidate board,
 * plan detail) so the navigation is visible from the `/ltl` landing — previously it only
 * appeared on the Consolidate board (issue #79).
 *
 * Search / Billing / Exceptions / Tenders are Phase 2 stubs: their backend endpoints exist
 * but no screen is wired yet, so they are rendered as non-navigating tabs carrying a "Phase 2"
 * badge rather than links that mislead the dispatcher into a dead redirect. Consolidate is the
 * only live destination today.
 */
@Component({
  selector: 'app-ltl-nav',
  standalone: true,
  imports: [RouterLink],
  template: `
    <nav class="shell-tabs" role="tablist" aria-label="LTL console">
      <div class="shell-tabs-inner">
        <span
          class="shell-tab shell-tab-stub"
          [class.active]="active === 'search'"
          role="tab"
          aria-disabled="true"
          title="Coming in Phase 2"
        >
          Search
          <span class="phase-badge">Phase 2</span>
        </span>
        <span class="shell-tab shell-tab-stub" role="tab" aria-disabled="true" title="Coming in Phase 2">
          Billing
          <span class="phase-badge">Phase 2</span>
        </span>
        <span class="shell-tab shell-tab-stub" role="tab" aria-disabled="true" title="Coming in Phase 2">
          Exceptions
          <span class="phase-badge">Phase 2</span>
        </span>
        <span class="shell-tab shell-tab-stub" role="tab" aria-disabled="true" title="Coming in Phase 2">
          Tenders
          <span class="phase-badge">Phase 2</span>
        </span>
        <a routerLink="/ltl/consolidate" class="shell-tab" [class.active]="active === 'consolidate'" role="tab">
          Consolidate
        </a>
      </div>
    </nav>
  `,
  styles: [
    `
      .shell-tabs {
        background: var(--card-bg);
        border-bottom: 1px solid var(--card-border);
        position: sticky;
        top: 60px;
        z-index: 20;
      }

      .shell-tabs-inner {
        max-width: 1400px;
        margin: 0 auto;
        padding: 0 24px;
        display: flex;
        align-items: center;
        gap: 24px;
        height: 48px;
      }

      .shell-tab {
        appearance: none;
        border: none;
        background: none;
        cursor: pointer;
        font-family: inherit;
        font-size: 14px;
        font-weight: 500;
        color: var(--text-secondary);
        height: 48px;
        display: inline-flex;
        align-items: center;
        gap: 6px;
        padding: 0 2px;
        border-bottom: 2px solid transparent;
        text-decoration: none;
        white-space: nowrap;
      }

      .shell-tab:hover {
        color: var(--text-primary);
      }

      .shell-tab.active {
        color: var(--accent);
        border-bottom-color: var(--accent);
        font-weight: 600;
      }

      .shell-tab-stub {
        cursor: default;
        color: var(--text-muted);
      }

      .shell-tab-stub:hover {
        color: var(--text-muted);
      }

      .phase-badge {
        font-size: 10px;
        font-weight: 600;
        text-transform: uppercase;
        letter-spacing: 0.03em;
        color: var(--text-secondary);
        background: var(--body-bg);
        border: 1px solid var(--card-border);
        border-radius: var(--radius-pill, 999px);
        padding: 1px 6px;
        line-height: 1.4;
      }

      @media (max-width: 720px) {
        .shell-tabs-inner {
          gap: 14px;
          padding: 0 12px;
          overflow-x: auto;
        }
      }
    `,
  ],
})
export class LtlNav {
  /** Which live tab is currently active. Stub tabs are never "active". */
  @Input() active: 'search' | 'consolidate' | null = null;
}
