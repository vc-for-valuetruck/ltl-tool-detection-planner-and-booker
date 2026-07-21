import { LtlBilling } from './ltl-billing';
import { LtlService } from './ltl.service';
import { LtlLoadSummary } from './ltl.models';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

function load(partial: Partial<LtlLoadSummary>): LtlLoadSummary {
  return {
    id: 'X',
    billing: { badges: [], missingFields: [], unpaidBalance: null, agingDays: null },
    ...partial,
  } as LtlLoadSummary;
}

describe('LtlBilling', () => {
  function build(stub: Partial<LtlService>): LtlBilling {
    TestBed.configureTestingModule({
      providers: [{ provide: LtlService, useValue: stub }],
    });
    return TestBed.runInInjectionContext(() => new LtlBilling());
  }

  it('loads the worklist on init and exposes the rows', () => {
    const c = build({ billingWorklist: () => of([load({ id: 'A' }), load({ id: 'B' })]) });
    c.ngOnInit();
    expect(c['loads']().length).toBe(2);
    expect(c['loading']()).toBeFalse();
    expect(c['hasLoads']()).toBeTrue();
  });

  it('passes the selected badge to the API and refetches', () => {
    const seen: (string | undefined)[] = [];
    const c = build({
      billingWorklist: (badge?) => {
        seen.push(badge);
        return of([]);
      },
    });
    c.ngOnInit();
    c['selectBadge']('MissingPod');
    expect(seen).toEqual([undefined, 'MissingPod']);
    expect(c['activeBadge']()).toBe('MissingPod');
  });

  it('does not refetch when the same badge is reselected', () => {
    let calls = 0;
    const c = build({
      billingWorklist: () => {
        calls++;
        return of([]);
      },
    });
    c.ngOnInit();
    c['selectBadge'](null);
    expect(calls).toBe(1);
  });

  it('surfaces an Alvys error and clears loading', () => {
    const c = build({ billingWorklist: () => throwError(() => ({ message: 'down' })) });
    c.ngOnInit();
    expect(c['error']()).toBe('down');
    expect(c['loading']()).toBeFalse();
  });

  it('maps badges to human labels and readiness classes', () => {
    const c = build({ billingWorklist: () => of([]) });
    expect(c['badgeLabel']('MissingPod')).toBe('Missing POD');
    expect(c['badgeLabel']('DaysPastTerms')).toBe('Days Past Terms');
    expect(c['badgeClass']('ReadyToBill')).toContain('chip-good');
    expect(c['badgeClass']('ExceptionBlockingBilling')).toContain('chip-danger');
    expect(c['badgeClass']('DaysPastTerms')).toContain('chip-danger');
    expect(c['badgeClass']('MissingRate')).toContain('chip-warn');
  });

  it('renders missing money as an em dash, never zero', () => {
    const c = build({ billingWorklist: () => of([]) });
    expect(c['formatCurrency'](null)).toBe('—');
    expect(c['formatCurrency'](1200)).toBe('$1,200');
  });
});
