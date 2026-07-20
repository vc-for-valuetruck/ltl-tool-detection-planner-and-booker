import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { LtlArrivalsBoard } from './ltl-arrivals-board';
import { LtlService } from './ltl.service';
import { LaredoArrival, LaredoArrivalsBoard } from './arrivals.models';
import { YardArtifactView } from './yard-artifacts.models';

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
    // Artifacts are supplementary; default to an empty feed unless a test overrides it.
    const withArtifacts: Partial<LtlService> = { yardArtifacts: () => of([]), ...stub };
    TestBed.configureTestingModule({
      providers: [
        { provide: LtlService, useValue: withArtifacts },
        { provide: Router, useValue: router },
      ],
    });
    return TestBed.runInInjectionContext(() => new LtlArrivalsBoard());
  }

  function artifact(overrides: Partial<YardArtifactView>): YardArtifactView {
    return {
      id: 'a1',
      yard: 'LAREDO',
      truckUnit: null,
      trailerUnit: null,
      loadNumber: null,
      submittedBy: 'dock@valuetruck.com',
      capturedAt: '2026-07-20T00:00:00Z',
      createdAt: '2026-07-20T00:00:00Z',
      status: 'Passed',
      passedItems: 3,
      failedItems: 0,
      naItems: 0,
      verifiedPallets: null,
      files: [],
      ...overrides,
    };
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

  it('matches a yard artifact to an arrival by truck or trailer unit', () => {
    const c = build({
      arrivals: () =>
        of(board([arrival({ tripId: 'a', truck: { id: 'e1', unit: 'T1', equipmentType: null, lengthFeet: null, fleetName: null, ownership: 'Fleet' } })])),
      yardArtifacts: () => of([artifact({ truckUnit: 'T1', status: 'Flagged' })]),
    });
    c.ngOnInit();

    const matched = c['artifactFor'](
      arrival({ truck: { id: 'e1', unit: 'T1', equipmentType: null, lengthFeet: null, fleetName: null, ownership: 'Fleet' } }),
    );
    expect(matched).not.toBeNull();
    expect(matched!.status).toBe('Flagged');

    expect(c['artifactFor'](arrival({}))).toBeNull();
  });

  it('maps an artifact status to a chip class', () => {
    const c = build({ arrivals: () => of(board([])) });
    expect(c['artifactChipClass']('Passed')).toBe('chip chip-good');
    expect(c['artifactChipClass']('Flagged')).toBe('chip chip-danger');
    expect(c['artifactChipClass']('Submitted')).toBe('chip chip-neutral');
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
