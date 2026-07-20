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
});
