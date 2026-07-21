import { LtlExceptions } from './ltl-exceptions';
import { LtlService } from './ltl.service';
import { LtlLoadSummary, LtlExceptionFlag, LtlLateDelivery, LtlStuckStop } from './ltl.models';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
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

  describe('late-delivery rendering', () => {
    const honestMessage =
      'Late delivery — delivery window ended Jul 20, 2026 5:00 PM UTC-05:00, ' +
      'no arrival recorded (per Alvys stop status)';

    function lateLoad(): LtlLoadSummary {
      const late: LtlLateDelivery = {
        stopId: 'stop-1004253',
        destinationCity: 'Laredo',
        destinationState: 'TX',
        windowEnd: '2026-07-20T22:00:00Z',
        windowBasis: 'delivery window',
        hoursOverdue: 1.9,
        message: honestMessage,
      };
      return {
        id: 'L-1004253',
        loadNumber: '1004253',
        // The server folds the honest wording into the exception flag's message; the chip renders it.
        exceptions: [{ code: 'LateDelivery', message: honestMessage, blocksBilling: false }],
        lateDelivery: late,
        workflow: { stageLabel: 'Bill' },
      } as unknown as LtlLoadSummary;
    }

    it('renders the honest late-delivery chip with hours overdue and a drill-through link', () => {
      TestBed.configureTestingModule({
        providers: [
          provideRouter([]),
          { provide: LtlService, useValue: { exceptions: () => of([lateLoad()]) } },
        ],
      });
      const fixture = TestBed.createComponent(LtlExceptions);
      fixture.detectChanges();
      const el: HTMLElement = fixture.nativeElement;

      const chip = el.querySelector('[data-testid="exceptions-late-delivery"]');
      expect(chip).not.toBeNull();
      expect(chip!.textContent).toContain('no arrival recorded (per Alvys stop status)');
      expect(chip!.textContent).toContain('1.9h overdue');

      const link = el.querySelector('[data-testid="exceptions-load-link"]') as HTMLAnchorElement;
      expect(link).not.toBeNull();
      expect(link.getAttribute('href')).toBe('/ltl/loads/1004253');
    });
  });

  describe('stuck-at-stop rendering', () => {
    const stuckMessage =
      'Stuck at stop — Delivery in Williamston, NC arrived Jul 14, 2026 3:00 AM UTC+00:00, ' +
      'no departure recorded after 164.8h. Per Alvys stop status — driver may not have closed the stop';

    function stuckLoad(): LtlLoadSummary {
      const stuck: LtlStuckStop = {
        stopId: 'stop-1003339',
        stopType: 'Delivery',
        city: 'Williamston',
        state: 'NC',
        arrivedAt: '2026-07-14T03:00:00Z',
        hoursSinceArrival: 164.8,
        message: stuckMessage,
      };
      return {
        id: 'L-1003339',
        loadNumber: '1003339',
        exceptions: [{ code: 'StuckAtStop', message: stuckMessage, blocksBilling: false }],
        stuckStop: stuck,
        workflow: { stageLabel: 'Bill' },
      } as unknown as LtlLoadSummary;
    }

    it('renders the honest stuck-at-stop chip with the caveat, hours, and a drill-through link', () => {
      TestBed.configureTestingModule({
        providers: [
          provideRouter([]),
          { provide: LtlService, useValue: { exceptions: () => of([stuckLoad()]) } },
        ],
      });
      const fixture = TestBed.createComponent(LtlExceptions);
      fixture.detectChanges();
      const el: HTMLElement = fixture.nativeElement;

      const chip = el.querySelector('[data-testid="exceptions-stuck-at-stop"]');
      expect(chip).not.toBeNull();
      expect(chip!.textContent).toContain('driver may not have closed the stop');
      expect(chip!.textContent).toContain('164.8h since arrival');

      const link = el.querySelector('[data-testid="exceptions-load-link"]') as HTMLAnchorElement;
      expect(link.getAttribute('href')).toBe('/ltl/loads/1003339');
    });
  });

  describe('type filter chips', () => {
    function lateOnly(): LtlLoadSummary {
      return {
        id: 'late',
        loadNumber: 'L1',
        exceptions: [{ code: 'LateDelivery', message: 'm', blocksBilling: false }],
        workflow: { stageLabel: 'Bill' },
      } as unknown as LtlLoadSummary;
    }
    function stuckOnly(): LtlLoadSummary {
      return {
        id: 'stuck',
        loadNumber: 'L2',
        exceptions: [{ code: 'StuckAtStop', message: 'm', blocksBilling: false }],
        workflow: { stageLabel: 'Bill' },
      } as unknown as LtlLoadSummary;
    }

    it('counts each exception type and filters the rows on chip selection', () => {
      const c = build({ exceptions: () => of([lateOnly(), stuckOnly()]) });
      c.ngOnInit();

      expect(c['lateDeliveryCount']()).toBe(1);
      expect(c['stuckStopCount']()).toBe(1);
      expect(c['filteredLoads']().length).toBe(2);

      c['setFilter']('StuckAtStop');
      expect(c['filteredLoads']().length).toBe(1);
      expect(c['filteredLoads']()[0].id).toBe('stuck');

      c['setFilter']('LateDelivery');
      expect(c['filteredLoads']().length).toBe(1);
      expect(c['filteredLoads']()[0].id).toBe('late');

      c['setFilter']('all');
      expect(c['filteredLoads']().length).toBe(2);
    });
  });
});
