import { LtlExceptions } from './ltl-exceptions';
import { LtlService } from './ltl.service';
import { LtlLoadSummary, LtlExceptionFlag } from './ltl.models';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

function ex(code: string, blocksBilling: boolean): LtlExceptionFlag {
  return { code, message: `${code} message`, blocksBilling };
}

function load(exceptions: LtlExceptionFlag[]): LtlLoadSummary {
  return { id: code(exceptions), exceptions } as LtlLoadSummary;
}
function code(e: LtlExceptionFlag[]): string {
  return e.map((x) => x.code).join('-') || 'none';
}

describe('LtlExceptions', () => {
  function build(stub: Partial<LtlService>): LtlExceptions {
    TestBed.configureTestingModule({
      providers: [{ provide: LtlService, useValue: stub }],
    });
    return TestBed.runInInjectionContext(() => new LtlExceptions());
  }

  it('loads exceptions on init and exposes the rows', () => {
    const c = build({ exceptions: () => of([load([ex('DETENTION', false)])]) });
    c.ngOnInit();
    expect(c['loads']().length).toBe(1);
    expect(c['loading']()).toBeFalse();
    expect(c['hasLoads']()).toBeTrue();
  });

  it('counts only loads with a billing-blocking exception', () => {
    const c = build({
      exceptions: () =>
        of([
          load([ex('A', true)]),
          load([ex('B', false)]),
          load([ex('C', false), ex('D', true)]),
        ]),
    });
    c.ngOnInit();
    expect(c['blockingCount']()).toBe(2);
  });

  it('surfaces an Alvys error and clears loading', () => {
    const c = build({ exceptions: () => throwError(() => ({ message: 'boom' })) });
    c.ngOnInit();
    expect(c['error']()).toBe('boom');
    expect(c['loading']()).toBeFalse();
    expect(c['hasLoads']()).toBeFalse();
  });
});
