import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { of } from 'rxjs';
import { LtlShell } from './ltl-shell';
import { LtlService } from './ltl.service';

@Component({ standalone: true, template: 'stub' })
class Stub {}

/** Minimal LtlService stub — the shell only reads the exceptions / pending-signals counts. */
function ltlStub(overrides: Partial<LtlService> = {}): Partial<LtlService> {
  return {
    exceptions: () => of([]),
    signals: () => of([]),
    ...overrides,
  };
}

/**
 * LtlShell replaces the old horizontal LtlNav tab strip with an Alvys-style vertical sidebar.
 * These lock in the two-group structure (Operations / Back Office), the Search quick item, the
 * collapse / slide-out toggles, and the breadcrumb derived from the active child route's data.
 */
describe('LtlShell', () => {
  let fixture: ComponentFixture<LtlShell>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [LtlShell],
      providers: [provideRouter([]), { provide: LtlService, useValue: ltlStub() }],
    }).compileComponents();
    fixture = TestBed.createComponent(LtlShell);
    fixture.detectChanges();
  });

  function el(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  it('renders Search as a top-level quick item linking to /ltl', () => {
    const quick = el().querySelector('a.nav-item.quick') as HTMLAnchorElement;
    expect(quick).toBeTruthy();
    expect(quick.getAttribute('href')).toBe('/ltl');
    expect(quick.textContent).toContain('Search');
  });

  it('renders exactly the Operations and Back Office groups with their items', () => {
    const titles = Array.from(el().querySelectorAll('.group-title')).map((t) => t.textContent?.trim());
    expect(titles).toEqual(['Operations', 'Back Office']);

    const labels = Array.from(el().querySelectorAll('.nav-item .nav-label')).map((l) =>
      l.textContent?.trim(),
    );
    for (const label of [
      'Search',
      'Dock',
      'Consolidate',
      'Loads',
      'Tenders',
      'Assignments',
      'Billing',
      'Exceptions',
      'Signals',
      'Notifications',
      'Reporting',
    ]) {
      expect(labels).toContain(label);
    }
  });

  it('toggles the desktop collapse state', () => {
    fixture.componentInstance['toggleCollapsed']();
    fixture.detectChanges();
    expect(el().querySelector('.ltl-shell')!.classList).toContain('collapsed');
  });

  it('opens and closes the tablet slide-out', () => {
    fixture.componentInstance['toggleMobile']();
    fixture.detectChanges();
    expect(el().querySelector('.ltl-shell')!.classList).toContain('mobile-open');

    fixture.componentInstance['closeMobile']();
    fixture.detectChanges();
    expect(el().querySelector('.ltl-shell')!.classList).not.toContain('mobile-open');
  });

  it('shows the LTL breadcrumb root by default', () => {
    expect(fixture.componentInstance['breadcrumb']()).toBe('LTL');
  });

  it('focuses the global quick-search on Alt+S', () => {
    const input = el().querySelector('[data-testid="shell-quick-search"]') as HTMLInputElement;
    expect(input).toBeTruthy();
    const focusSpy = spyOn(input, 'focus');
    const event = new KeyboardEvent('keydown', { altKey: true, key: 's' });
    const preventSpy = spyOn(event, 'preventDefault');
    document.dispatchEvent(event);
    expect(focusSpy).toHaveBeenCalled();
    expect(preventSpy).toHaveBeenCalled();
  });

  it('jumps to a load detail on quick-search submit and clears the box', () => {
    const router = TestBed.inject(Router);
    const navSpy = spyOn(router, 'navigate').and.resolveTo(true);
    fixture.componentInstance['quickSearch'].set(' L-100234 ');
    fixture.componentInstance['submitQuickSearch']();
    expect(navSpy).toHaveBeenCalledWith(['/ltl/loads', 'L-100234']);
    expect(fixture.componentInstance['quickSearch']()).toBe('');
  });

  it('does not navigate on an empty quick-search', () => {
    const router = TestBed.inject(Router);
    const navSpy = spyOn(router, 'navigate').and.resolveTo(true);
    fixture.componentInstance['quickSearch'].set('   ');
    fixture.componentInstance['submitQuickSearch']();
    expect(navSpy).not.toHaveBeenCalled();
  });
});

describe('LtlShell badges', () => {
  function build(overrides: Partial<LtlService>): ComponentFixture<LtlShell> {
    TestBed.configureTestingModule({
      imports: [LtlShell],
      providers: [provideRouter([]), { provide: LtlService, useValue: ltlStub(overrides) }],
    });
    const fixture = TestBed.createComponent(LtlShell);
    fixture.detectChanges();
    return fixture;
  }

  it('shows a red exception count and a neutral pending-signal count when non-zero', () => {
    const fixture = build({
      exceptions: () => of([{}, {}, {}] as never[]),
      signals: () => of([{}, {}] as never[]),
    });
    const el = fixture.nativeElement as HTMLElement;
    const exBadge = el.querySelector('[data-testid="nav-badge-exceptions"]');
    const sigBadge = el.querySelector('[data-testid="nav-badge-signals"]');
    expect(exBadge?.textContent?.trim()).toBe('3');
    expect(exBadge?.classList).toContain('nav-badge-danger');
    expect(sigBadge?.textContent?.trim()).toBe('2');
    expect(sigBadge?.classList).not.toContain('nav-badge-danger');
  });

  it('hides both badges when the counts are zero', () => {
    const fixture = build({ exceptions: () => of([]), signals: () => of([]) });
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="nav-badge-exceptions"]')).toBeNull();
    expect(el.querySelector('[data-testid="nav-badge-signals"]')).toBeNull();
  });

  it('requests only pending signals for the needs-review count', () => {
    const signalsSpy = jasmine.createSpy('signals').and.returnValue(of([]));
    build({ signals: signalsSpy });
    expect(signalsSpy).toHaveBeenCalledWith({ status: 'Pending' });
  });
});

describe('LtlShell breadcrumb from route data', () => {
  it('derives the breadcrumb from the active child route crumb', async () => {
    TestBed.configureTestingModule({
      providers: [
        { provide: LtlService, useValue: ltlStub() },
        provideRouter([
          {
            path: 'ltl',
            component: LtlShell,
            children: [{ path: 'dock', component: Stub, data: { crumb: 'Dock' } }],
          },
        ]),
      ],
    });

    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/ltl/dock');
    harness.detectChanges();

    const crumb = (harness.fixture.nativeElement as HTMLElement).querySelector('.crumb');
    expect(crumb?.textContent?.trim()).toBe('LTL / Dock');
  });
});
