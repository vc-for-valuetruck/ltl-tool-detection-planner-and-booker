import { Component, Input } from '@angular/core';
import { RouterLink } from '@angular/router';

/**
 * Shared LTL console tab strip. Rendered on every LTL screen (landing, consolidate board,
 * plan detail) so the navigation is visible from the `/ltl` landing — previously it only
 * appeared on the Consolidate board (issue #79).
 *
 * Search (the `/ltl` consolidation queue), Loads (the `/ltl/loads` operating console), Billing,
 * Exceptions, Tenders and Consolidate are all live destinations. Every tab is a real routerLink;
 * there is no Phase 2 stub left in the strip.
 */
@Component({
  selector: 'app-ltl-nav',
  standalone: true,
  imports: [RouterLink],
  template: `
    <nav class="shell-tabs" role="tablist" aria-label="LTL console">
      <div class="shell-tabs-inner">
        <a routerLink="/ltl" class="shell-tab" [class.active]="active === 'search'" role="tab">
          Search
        </a>
        <a routerLink="/ltl/loads" class="shell-tab" [class.active]="active === 'loads'" role="tab">
          Loads
        </a>
        <a routerLink="/ltl/billing" class="shell-tab" [class.active]="active === 'billing'" role="tab">
          Billing
        </a>
        <a routerLink="/ltl/exceptions" class="shell-tab" [class.active]="active === 'exceptions'" role="tab">
          Exceptions
        </a>
        <a routerLink="/ltl/tenders" class="shell-tab" [class.active]="active === 'tenders'" role="tab">
          Tenders
        </a>
        <a routerLink="/ltl/consolidate" class="shell-tab" [class.active]="active === 'consolidate'" role="tab">
          Consolidate
        </a>
        <a routerLink="/ltl/notifications" class="shell-tab" [class.active]="active === 'notifications'" role="tab">
          Notifications
        </a>
        <a routerLink="/ltl/signals" class="shell-tab" [class.active]="active === 'signals'" role="tab">
          Signals
        </a>
        <a routerLink="/ltl/reporting" class="shell-tab" [class.active]="active === 'reporting'" role="tab">
          Reporting
        </a>
        <a routerLink="/ltl/assignments" class="shell-tab" [class.active]="active === 'assignments'" role="tab">
          Assignments
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
  /** Which tab is currently active; null on screens outside the strip. */
  @Input() active:
    | 'search'
    | 'loads'
    | 'consolidate'
    | 'billing'
    | 'exceptions'
    | 'tenders'
    | 'notifications'
    | 'signals'
    | 'reporting'
    | 'assignments'
    | null = null;
}
