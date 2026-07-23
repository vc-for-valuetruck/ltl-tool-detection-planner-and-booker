import { CommonModule } from '@angular/common';
import {
  Component,
  ElementRef,
  HostListener,
  OnDestroy,
  OnInit,
  ViewChild,
  computed,
  inject,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivatedRoute,
  NavigationEnd,
  Router,
  RouterLink,
  RouterLinkActive,
  RouterOutlet,
} from '@angular/router';
import { Subscription, filter } from 'rxjs';
import { LtlService } from './ltl.service';

/** One clickable destination in the sidebar. `monogram` is the collapsed-mode icon glyph. */
interface NavItem {
  readonly label: string;
  readonly route: string;
  readonly monogram: string;
  /** true → active only on an exact URL match (the Search quick item, whose route prefixes them all). */
  readonly exact?: boolean;
  /** Which attention count to badge this item with (from live endpoints); absent → no badge. */
  readonly badge?: 'exceptions' | 'signals';
}

interface NavGroup {
  readonly title: string;
  readonly items: readonly NavItem[];
}

/**
 * LTL workspace layout (Alvys-style vertical sidebar). Replaces the crowded 11-tab horizontal strip
 * (<c>LtlNav</c>) with a light, collapsible left rail that mirrors the Alvys dispatch console: a brand
 * block on top, Search as a top-level quick item, then two collapsible groups (Operations / Back
 * Office), and a pinned Help/user block at the bottom. The active item gets a soft pill highlight.
 *
 * The rail collapses to icons on the desktop (persisted per session) and becomes a hamburger slide-out
 * overlay on tablet widths, with large touch targets for dock tablets. The content area carries a
 * breadcrumb bar (e.g. "LTL / Dock") derived from the active child route's <c>data.crumb</c>.
 */
@Component({
  selector: 'app-ltl-shell',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './ltl-shell.html',
  styleUrls: ['./ltl-shell.css'],
})
export class LtlShell implements OnInit, OnDestroy {
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly ltl = inject(LtlService);

  /** Collapsed-to-icons on desktop. */
  protected readonly collapsed = signal(false);
  /** Slide-out overlay open on tablet/narrow widths. */
  protected readonly mobileOpen = signal(false);

  /** Global "go to load" quick-search box in the header (focused by Alt+S). */
  @ViewChild('quickSearchInput') private quickSearchInput?: ElementRef<HTMLInputElement>;
  protected readonly quickSearch = signal('');

  /** The active leaf route's breadcrumb label ("Dock", "Billing", …); null on the Search landing. */
  protected readonly crumb = signal<string | null>(null);
  protected readonly breadcrumb = computed(() => {
    const leaf = this.crumb();
    return leaf ? `LTL / ${leaf}` : 'LTL';
  });

  protected readonly quickItem: NavItem = { label: 'Search', route: '/ltl', monogram: 'S', exact: true };

  protected readonly groups: readonly NavGroup[] = [
    {
      title: 'Operations',
      items: [
        { label: 'Dispatch Assist', route: '/ltl/dispatch', monogram: 'P' },
        { label: 'Dock', route: '/ltl/dock', monogram: 'D' },
        { label: 'Consolidate', route: '/ltl/consolidate', monogram: 'C' },
        { label: 'Loads', route: '/ltl/loads', monogram: 'L' },
        { label: 'Tenders', route: '/ltl/tenders', monogram: 'T' },
        { label: 'Assignments', route: '/ltl/assignments', monogram: 'A' },
      ],
    },
    {
      title: 'Back Office',
      items: [
        { label: 'Billing', route: '/ltl/billing', monogram: 'B' },
        { label: 'Invoice Studio', route: '/ltl/invoice-studio', monogram: 'I' },
        { label: 'Exceptions', route: '/ltl/exceptions', monogram: 'E', badge: 'exceptions' },
        { label: 'Signals', route: '/ltl/signals', monogram: 'G', badge: 'signals' },
        { label: 'Notifications', route: '/ltl/notifications', monogram: 'N' },
        { label: 'Reporting', route: '/ltl/reporting', monogram: 'R' },
      ],
    },
  ];

  /**
   * Live attention counts for the sidebar badges. `null` = not yet loaded or the read degraded — the
   * badge stays hidden rather than showing a misleading "0". Fetched once on init from the same
   * read-only endpoints the Exceptions / Signals pages use.
   */
  protected readonly exceptionCount = signal<number | null>(null);
  protected readonly signalCount = signal<number | null>(null);

  private sub: Subscription | null = null;

  ngOnInit(): void {
    this.updateCrumb();
    this.loadBadgeCounts();
    this.sub = this.router.events
      .pipe(filter((e): e is NavigationEnd => e instanceof NavigationEnd))
      .subscribe(() => {
        this.updateCrumb();
        // A route change on tablet closes the slide-out so the content isn't left behind the scrim.
        this.mobileOpen.set(false);
      });
  }

  /** Best-effort attention counts; a failure leaves the count null so the badge simply doesn't show. */
  private loadBadgeCounts(): void {
    this.ltl.exceptions().subscribe({
      next: (loads) => this.exceptionCount.set(loads.length),
      error: () => this.exceptionCount.set(null),
    });
    this.ltl.signals({ status: 'Pending' }).subscribe({
      next: (signals) => this.signalCount.set(signals.length),
      error: () => this.signalCount.set(null),
    });
  }

  /** Count to render on a nav item's badge, or null when there's nothing to flag (hides the pill). */
  protected badgeCount(item: NavItem): number | null {
    const count = item.badge === 'exceptions' ? this.exceptionCount() : item.badge === 'signals' ? this.signalCount() : null;
    return count && count > 0 ? count : null;
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
  }

  protected toggleCollapsed(): void {
    this.collapsed.update((c) => !c);
  }

  protected toggleMobile(): void {
    this.mobileOpen.update((o) => !o);
  }

  protected closeMobile(): void {
    this.mobileOpen.set(false);
  }

  /** Alt+S focuses the global quick-search from anywhere in the workspace (familiar TMS shortcut). */
  @HostListener('document:keydown', ['$event'])
  protected onGlobalKeydown(event: KeyboardEvent): void {
    if (event.altKey && (event.key === 's' || event.key === 'S')) {
      event.preventDefault();
      this.quickSearchInput?.nativeElement.focus();
      this.quickSearchInput?.nativeElement.select();
    }
  }

  /** Jump straight to a load's detail by number/id. The detail route resolves it against Alvys. */
  protected submitQuickSearch(): void {
    const value = this.quickSearch().trim();
    if (!value) return;
    this.router.navigate(['/ltl/loads', value]);
    this.quickSearch.set('');
    this.quickSearchInput?.nativeElement.blur();
  }

  /** Walks to the deepest activated child and reads its `data.crumb` for the breadcrumb bar. */
  private updateCrumb(): void {
    let r = this.route;
    while (r.firstChild) r = r.firstChild;
    const crumb = r.snapshot.data['crumb'];
    this.crumb.set(typeof crumb === 'string' && crumb.length > 0 ? crumb : null);
  }
}
