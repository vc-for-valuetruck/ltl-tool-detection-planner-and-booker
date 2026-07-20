import { LtlNav } from './ltl-nav';
import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

/**
 * Nav tab-strip tests. Search became a live routing destination (previously a Phase 2 stub);
 * these lock in that it renders as a real link to /ltl, carries no Phase 2 badge, and that the
 * active-tab highlight follows the `active` input for both Search and Consolidate.
 */
describe('LtlNav', () => {
  let fixture: ComponentFixture<LtlNav>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [LtlNav],
      providers: [provideRouter([])],
    }).compileComponents();

    fixture = TestBed.createComponent(LtlNav);
  });

  function tabByText(text: string): HTMLElement | undefined {
    const el = fixture.nativeElement as HTMLElement;
    return Array.from(el.querySelectorAll<HTMLElement>('.shell-tab')).find(
      (t) => t.textContent?.trim().startsWith(text),
    );
  }

  it('renders Search as a live link to /ltl with no Phase 2 badge', () => {
    fixture.detectChanges();

    const search = tabByText('Search');
    expect(search).toBeTruthy();
    expect(search!.tagName).toBe('A');
    expect(search!.getAttribute('href')).toBe('/ltl');
    expect(search!.querySelector('.phase-badge')).toBeNull();
  });

  it('highlights the Search tab when active="search"', () => {
    fixture.componentInstance.active = 'search';
    fixture.detectChanges();

    expect(tabByText('Search')!.classList).toContain('active');
    expect(tabByText('Consolidate')!.classList).not.toContain('active');
  });

  it('highlights the Consolidate tab when active="consolidate"', () => {
    fixture.componentInstance.active = 'consolidate';
    fixture.detectChanges();

    expect(tabByText('Consolidate')!.classList).toContain('active');
    expect(tabByText('Search')!.classList).not.toContain('active');
  });

  it('renders Billing and Exceptions as live links, no Phase 2 badge', () => {
    fixture.detectChanges();

    const billing = tabByText('Billing');
    expect(billing!.tagName).toBe('A');
    expect(billing!.getAttribute('href')).toBe('/ltl/billing');
    expect(billing!.querySelector('.phase-badge')).toBeNull();

    const exceptions = tabByText('Exceptions');
    expect(exceptions!.tagName).toBe('A');
    expect(exceptions!.getAttribute('href')).toBe('/ltl/exceptions');
    expect(exceptions!.querySelector('.phase-badge')).toBeNull();
  });

  it('renders Tenders as a live link to /ltl/tenders, no Phase 2 badge', () => {
    fixture.detectChanges();

    const tenders = tabByText('Tenders');
    expect(tenders!.tagName).toBe('A');
    expect(tenders!.getAttribute('href')).toBe('/ltl/tenders');
    expect(tenders!.querySelector('.phase-badge')).toBeNull();
  });

  it('has no Phase 2 stub tabs left in the strip', () => {
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelectorAll('.shell-tab-stub').length).toBe(0);
    expect(el.querySelectorAll('.phase-badge').length).toBe(0);
  });

  it('highlights the Tenders tab when active="tenders"', () => {
    fixture.componentInstance.active = 'tenders';
    fixture.detectChanges();
    expect(tabByText('Tenders')!.classList).toContain('active');
  });
});
