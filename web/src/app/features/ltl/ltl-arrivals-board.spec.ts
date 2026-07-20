import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { LtlArrivalsBoard } from './ltl-arrivals-board';
import { LtlService } from './ltl.service';
import { LaredoArrival, LaredoArrivalsBoard } from './arrivals.models';

function arrival(overrides: Partial<LaredoArrival>): LaredoArrival {
  return {
    tripId: 't1',
    tripNumber: 'T1',
    loadNumber: 'L1',
    orderNumber: null,
    truck: null,
    trailer: null,
    driverName: null,
    inboundFrom: null,
    laredo: { city: 'Laredo', state: 'TX', label: 'Laredo, TX' },
    scheduledArrivalStart: null,
    scheduledArrivalEnd: null,
    arrivedAt: null,
    departedAt: null,
    status: 'Scheduled',
    predictedArrivalAt: null,
    etaBasis: null,
    predictedLate: false,
    dallasBound: false,
    onwardStops: [],
    ...overrides,
  };
}

function board(arrivals: LaredoArrival[], truncated = false): LaredoArrivalsBoard {
  return {
    generatedAt: '2026-07-20T12:00:00Z',
    date: '2026-07-20',
    yard: 'LAREDO',
    arrivals,
    truncated,
    source: 'Live Alvys trips.',
  };
}

describe('LtlArrivalsBoard', () => {
  let router: { navigate: jasmine.Spy };

  function build(stub: Partial<LtlService>): LtlArrivalsBoard {
    router = { navigate: jasmine.createSpy('navigate') };
    TestBed.configureTestingModule({
      providers: [
        { provide: LtlService, useValue: stub },
        { provide: Router, useValue: router },
      ],
    });
    return TestBed.runInInjectionContext(() => new LtlArrivalsBoard());
  }

  it('loads the board on init and exposes arrivals + Dallas-bound count', () => {
    const c = build({
      arrivals: () =>
        of(board([arrival({ tripId: 'a', dallasBound: true }), arrival({ tripId: 'b' })])),
    });
    c.ngOnInit();
    expect(c['arrivals']().length).toBe(2);
    expect(c['dallasBoundCount']()).toBe(1);
    expect(c['hasArrivals']()).toBeTrue();
    expect(c['loading']()).toBeFalse();
  });

  it('surfaces an Alvys error and clears loading', () => {
    const c = build({ arrivals: () => throwError(() => ({ message: 'boom' })) });
    c.ngOnInit();
    expect(c['error']()).toBe('boom');
    expect(c['loading']()).toBeFalse();
    expect(c['hasArrivals']()).toBeFalse();
  });

  it('maps status to a chip class', () => {
    const c = build({ arrivals: () => of(board([])) });
    expect(c['statusClass']('Arrived')).toBe('chip chip-arrived');
    expect(c['statusClass']('Departed')).toBe('chip chip-departed');
    expect(c['statusClass']('Scheduled')).toBe('chip chip-scheduled');
  });

  it('labels ownership honestly, never guessing', () => {
    const c = build({ arrivals: () => of(board([])) });
    expect(c['ownershipLabel']('Fleet')).toBe('Fleet');
    expect(c['ownershipLabel']('ThirdPartyLeased')).toBe('3P-leased');
    expect(c['ownershipLabel']('Unknown')).toBe('Unknown');
  });

  it('opens the pilot Laredo → Dallas Consolidate corridor for an LTL opportunity', () => {
    const c = build({ arrivals: () => of(board([])) });
    const event = { stopPropagation: jasmine.createSpy('stopPropagation') } as unknown as Event;
    c['openConsolidate'](event);
    expect(event.stopPropagation).toHaveBeenCalled();
    expect(router.navigate).toHaveBeenCalledWith(['/ltl/consolidate']);
  });

  it('opens the load detail when a load number is present, no-op otherwise', () => {
    const c = build({ arrivals: () => of(board([])) });
    c['openLoad'](arrival({ loadNumber: 'L42' }));
    expect(router.navigate).toHaveBeenCalledWith(['/ltl/loads', 'L42']);

    router.navigate.calls.reset();
    c['openLoad'](arrival({ loadNumber: null }));
    expect(router.navigate).not.toHaveBeenCalled();
  });

  it('joins onward stop labels or shows a dash', () => {
    const c = build({ arrivals: () => of(board([])) });
    expect(
      c['onwardLabel'](
        arrival({
          onwardStops: [
            { city: 'Dallas', state: 'TX', label: 'Dallas, TX' },
            { city: 'Plano', state: 'TX', label: 'Plano, TX' },
          ],
        }),
      ),
    ).toBe('Dallas, TX → Plano, TX');
    expect(c['onwardLabel'](arrival({ onwardStops: [] }))).toBe('—');
  });
});
