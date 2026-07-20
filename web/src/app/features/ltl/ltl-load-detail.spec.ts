import { LtlLoadDetail } from './ltl-load-detail';
import { LtlService } from './ltl.service';
import { LtlLoadSummary, LtlPlace } from './ltl.models';
import { ActivatedRoute } from '@angular/router';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

function place(partial: Partial<LtlPlace>): LtlPlace {
  return { name: null, city: null, state: null, zip: null, label: null, ...partial };
}

function load(partial: Partial<LtlLoadSummary>): LtlLoadSummary {
  return { id: 'X', loadNumber: 'L-1', ...partial } as LtlLoadSummary;
}

describe('LtlLoadDetail', () => {
  function build(loadNumber: string | null, stub: Partial<LtlService>): LtlLoadDetail {
    TestBed.configureTestingModule({
      providers: [
        { provide: LtlService, useValue: stub },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['loadNumber', loadNumber]]) } } },
      ],
    });
    return TestBed.runInInjectionContext(() => new LtlLoadDetail());
  }

  it('fetches the load by the route param on init', () => {
    let asked: string | undefined;
    const c = build('L-100234', {
      getLoad: (ref) => {
        asked = ref;
        return of(load({ loadNumber: 'L-100234' }));
      },
    });
    c.ngOnInit();
    expect(asked).toBe('L-100234');
    expect(c['hasLoad']()).toBeTrue();
    expect(c['loading']()).toBeFalse();
  });

  it('errors without calling Alvys when no load number is in the URL', () => {
    let called = false;
    const c = build(null, {
      getLoad: () => {
        called = true;
        return of(load({}));
      },
    });
    c.ngOnInit();
    expect(called).toBeFalse();
    expect(c['error']()).toBe('No load number in the URL.');
    expect(c['loading']()).toBeFalse();
  });

  it('surfaces an Alvys error and clears loading', () => {
    const c = build('L-1', { getLoad: () => throwError(() => ({ message: 'boom' })) });
    c.ngOnInit();
    expect(c['error']()).toBe('boom');
    expect(c['loading']()).toBeFalse();
    expect(c['hasLoad']()).toBeFalse();
  });

  it('formats a place from label, then city/state/zip, then em dash', () => {
    const c = build('L-1', { getLoad: () => of(load({})) });
    expect(c['place'](null)).toBe('—');
    expect(c['place'](place({ label: 'Dallas, TX' }))).toBe('Dallas, TX');
    expect(c['place'](place({ city: 'Irving', state: 'TX' }))).toBe('Irving, TX');
    expect(c['place'](place({}))).toBe('—');
  });

  it('renders unknown weight as "Unknown", never zero', () => {
    const c = build('L-1', { getLoad: () => of(load({})) });
    expect(c['weightLabel'](load({ weightLbs: null }))).toBe('Unknown');
    expect(c['weightLabel'](load({ weightLbs: 42360 }))).toBe('42,360 lb');
  });

  it('renders missing money as an em dash, never zero', () => {
    const c = build('L-1', { getLoad: () => of(load({})) });
    expect(c['formatCurrency'](null)).toBe('—');
    expect(c['formatCurrency'](1200)).toBe('$1,200');
  });
});
