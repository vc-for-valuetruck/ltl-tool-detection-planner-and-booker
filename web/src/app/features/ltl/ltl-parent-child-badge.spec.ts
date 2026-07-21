import { ComponentFixture, TestBed } from '@angular/core/testing';
import { LtlParentChildBadge } from './ltl-parent-child-badge';

describe('LtlParentChildBadge', () => {
  let fixture: ComponentFixture<LtlParentChildBadge>;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [LtlParentChildBadge] });
    fixture = TestBed.createComponent(LtlParentChildBadge);
  });

  function text(): string {
    return (fixture.nativeElement as HTMLElement).textContent?.replace(/\s+/g, ' ').trim() ?? '';
  }

  it('renders the parent badge with BOL-controlling wording', () => {
    fixture.componentRef.setInput('role', 'parent');
    fixture.detectChanges();
    expect(text()).toContain('PARENT · BOL controlling');
    expect((fixture.nativeElement as HTMLElement).querySelector('.pc-parent')).toBeTruthy();
  });

  it('renders the child badge naming the controlling parent', () => {
    fixture.componentRef.setInput('role', 'child');
    fixture.componentRef.setInput('parentLabel', 'L-100234');
    fixture.detectChanges();
    expect(text()).toBe('CHILD of L-100234');
    expect((fixture.nativeElement as HTMLElement).querySelector('.pc-child')).toBeTruthy();
  });

  it('falls back to a plain CHILD when the parent label is missing (never fabricated)', () => {
    fixture.componentRef.setInput('role', 'child');
    fixture.componentRef.setInput('parentLabel', '   ');
    fixture.detectChanges();
    expect(text()).toBe('CHILD');
  });

  it('renders nothing when the load is not part of any plan', () => {
    fixture.componentRef.setInput('role', null);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('.pc-badge')).toBeNull();
    expect(text()).toBe('');
  });
});
