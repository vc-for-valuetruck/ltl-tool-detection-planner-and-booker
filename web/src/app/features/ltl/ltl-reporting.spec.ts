import { LtlReporting } from './ltl-reporting';
import { LtlService } from './ltl.service';
import { MarginRollupResponse, MarginRollupRow } from './ltl.models';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

function row(partial: Partial<MarginRollupRow>): MarginRollupRow {
  return {
    key: 'k',
    label: 'Label',
    labelIsId: false,
    loadCount: 1,
    totalRevenue: null,
    totalCarrierPayable: null,
    totalGrossMargin: null,
    grossMarginPercent: null,
    totalUnpaidBalance: null,
    exceptionCount: 0,
    readyToBillCount: 0,
    ...partial,
  };
}

function response(rows: MarginRollupRow[], truncated = false): MarginRollupResponse {
  return { groupBy: 'Customer', rows, truncated };
}

describe('LtlReporting', () => {
  function build(stub: Partial<LtlService>): LtlReporting {
    TestBed.configureTestingModule({
      providers: [{ provide: LtlService, useValue: stub }],
    });
    return TestBed.runInInjectionContext(() => new LtlReporting());
  }

  it('loads the Customer rollup on init and exposes the rows', () => {
    const c = build({ marginRollup: () => of(response([row({ key: 'A' }), row({ key: 'B' })])) });
    c.ngOnInit();
    expect(c['rows']().length).toBe(2);
    expect(c['loading']()).toBeFalse();
    expect(c['hasRows']()).toBeTrue();
    expect(c['groupBy']()).toBe('Customer');
  });

  it('passes the selected group-by to the API and refetches', () => {
    const seen: string[] = [];
    const c = build({
      marginRollup: (groupBy) => {
        seen.push(groupBy);
        return of(response([]));
      },
    });
    c.ngOnInit();
    c['selectGroupBy']('Rep');
    expect(seen).toEqual(['Customer', 'Rep']);
    expect(c['groupBy']()).toBe('Rep');
  });

  it('does not refetch when the same group-by is reselected', () => {
    let calls = 0;
    const c = build({
      marginRollup: () => {
        calls++;
        return of(response([]));
      },
    });
    c.ngOnInit();
    c['selectGroupBy']('Customer');
    expect(calls).toBe(1);
  });

  it('surfaces an Alvys error and clears loading', () => {
    const c = build({ marginRollup: () => throwError(() => ({ message: 'down' })) });
    c.ngOnInit();
    expect(c['error']()).toBe('down');
    expect(c['loading']()).toBeFalse();
  });

  it('tracks the truncated flag from the response', () => {
    const c = build({ marginRollup: () => of(response([row({ key: 'A' })], true)) });
    c.ngOnInit();
    expect(c['truncated']()).toBeTrue();
  });

  it('renders missing money as an em dash, never zero', () => {
    const c = build({ marginRollup: () => of(response([])) });
    expect(c['formatCurrency'](null)).toBe('—');
    expect(c['formatCurrency'](1200)).toBe('$1,200');
  });

  it('renders missing margin percent as an em dash', () => {
    const c = build({ marginRollup: () => of(response([])) });
    expect(c['formatPercent'](null)).toBe('—');
    expect(c['formatPercent'](12.5)).toBe('12.5%');
  });

  it('classes margin risk by percent: unknown neutral, negative danger, thin warn, healthy good', () => {
    const c = build({ marginRollup: () => of(response([])) });
    expect(c['marginClass'](row({ grossMarginPercent: null }))).toContain('chip-neutral');
    expect(c['marginClass'](row({ grossMarginPercent: -5 }))).toContain('chip-danger');
    expect(c['marginClass'](row({ grossMarginPercent: 8 }))).toContain('chip-warn');
    expect(c['marginClass'](row({ grossMarginPercent: 25 }))).toContain('chip-good');
  });
});
