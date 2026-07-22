import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { LtlShell } from './ltl-shell';

@Component({ standalone: true, template: 'stub' })
class Stub {}

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
      providers: [provideRouter([])],
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

describe('LtlShell breadcrumb from route data', () => {
  it('derives the breadcrumb from the active child route crumb', async () => {
    TestBed.configureTestingModule({
      providers: [
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
