import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { Dock } from './dock';
import { DockService } from './dock.service';
import { ConsolidationService } from './consolidation.service';
import { DispatchPlannerService } from './dispatch-planner.service';
import { DispatchPreferenceView } from './dispatch-planner.models';
import { LaredoArrival, LaredoArrivalsBoard } from './arrivals.models';
import {
  ConsolidationCandidate,
  ConsolidationCandidateResponse,
  ConsolidationPlanResponse,
} from './consolidation.models';
import { DockCombineResponse, DockNotificationResult, DockUndoResponse } from './dock.models';

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

function board(arrivals: LaredoArrival[]): LaredoArrivalsBoard {
  return {
    generatedAt: '2026-07-21T12:00:00Z',
    date: '2026-07-21',
    yard: 'LAREDO',
    arrivals,
    truncated: false,
    source: 'Live Alvys trips.',
  };
}

function candidate(overrides: Partial<ConsolidationCandidate>): ConsolidationCandidate {
  return {
    loadId: 'L-2',
    loadNumber: 'L-2',
    corridorCode: 'LAREDO_TO_DALLAS',
    factors: [],
    isBlocked: false,
    customerTier: 'Allowed',
    ...overrides,
  };
}

function plan(overrides: Partial<ConsolidationPlanResponse>): ConsolidationPlanResponse {
  return { blockers: [], clickCard: {}, ...overrides } as unknown as ConsolidationPlanResponse;
}

describe('Dock', () => {
  function build(
    stub: Partial<DockService>,
    planStub?: Partial<ConsolidationService>,
    plannerStub?: Partial<DispatchPlannerService>,
  ): Dock {
    const defaults: Partial<DockService> = {
      getWarehouses: () =>
        of({ warehouses: [{ code: 'LAREDO', name: 'Laredo', state: 'TX', nearbyCities: [] }] }),
      getArrivals: () => of(board([arrival({ tripId: 'a', loadNumber: 'L1' })])),
      getCandidates: () =>
        of({
          corridorCode: 'LAREDO_TO_DALLAS',
          candidates: [candidate({})],
          scanTruncated: false,
        } as ConsolidationCandidateResponse),
      combine: () =>
        of({
          plan: { blockers: [], clickCard: {} },
          audit: { alvysWriteback: 'NotPerformed' },
          notification: { state: 'Disabled', recipients: [] },
        } as unknown as DockCombineResponse),
      undo: () => of({ audit: { alvysWriteback: 'NotPerformed' } } as unknown as DockUndoResponse),
      renotify: () => of({ state: 'Delivered', recipients: ['ops@example.com'] } as DockNotificationResult),
      recordCombineMetric: () => of(undefined),
      ...stub,
    };
    const consolidationDefaults: Partial<ConsolidationService> = {
      buildPlan: () => of(plan({ blockers: [] })),
      ...planStub,
    };
    const plannerDefaults: Partial<DispatchPlannerService> = {
      getPreferredPairing: () =>
        of({ resolved: false, source: 'test' } as DispatchPreferenceView),
      ...plannerStub,
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: DockService, useValue: defaults },
        { provide: ConsolidationService, useValue: consolidationDefaults },
        { provide: DispatchPlannerService, useValue: plannerDefaults },
      ],
    });
    return TestBed.runInInjectionContext(() => new Dock());
  }

  it('loads warehouses on init', () => {
    const c = build({});
    c.ngOnInit();
    expect(c['warehouses']().length).toBe(1);
    expect(c['step']()).toBe('warehouse');
    expect(c['loading']()).toBeFalse();
  });

  it('surfaces an Alvys error loading warehouses', () => {
    const c = build({ getWarehouses: () => throwError(() => ({ message: 'boom' })) });
    c.ngOnInit();
    expect(c['error']()).toBe('boom');
    expect(c['loading']()).toBeFalse();
  });

  it('picking a yard advances to arrivals and loads the board', () => {
    const c = build({});
    c['pickWarehouse']({ code: 'LAREDO', name: 'Laredo', state: 'TX', nearbyCities: [] });
    expect(c['step']()).toBe('arrivals');
    expect(c['arrivals']().length).toBe(1);
  });

  it('only allows an arrival with a load number to be the parent', () => {
    const c = build({});
    expect(c['canBeParent'](arrival({ loadNumber: 'L1' }))).toBeTrue();
    expect(c['canBeParent'](arrival({ loadNumber: null }))).toBeFalse();
  });

  it('picking a parent advances to siblings and loads candidates', () => {
    const c = build({});
    c['pickParent'](arrival({ loadNumber: 'L1' }));
    expect(c['step']()).toBe('siblings');
    expect(c['candidateList']().length).toBe(1);
  });

  it('toggles a sibling on and off, ignoring blocked ones', () => {
    const c = build({});
    const cand = candidate({ loadId: 'L-2' });
    c['toggleSibling'](cand);
    expect(c['isSelected'](cand)).toBeTrue();
    c['toggleSibling'](cand);
    expect(c['isSelected'](cand)).toBeFalse();

    c['toggleSibling'](candidate({ loadId: 'L-9', isBlocked: true }));
    expect(c['selectedSiblingIds']().length).toBe(0);
  });

  it('adds and removes manual siblings without duplicates', () => {
    const c = build({});
    c['manualSiblingId'].set('L-77');
    c['addManualSibling']();
    c['manualSiblingId'].set('L-77');
    c['addManualSibling']();
    expect(c['selectedSiblingIds']()).toEqual(['L-77']);
    c['removeSibling']('L-77');
    expect(c['selectedSiblingIds']().length).toBe(0);
  });

  it('combine posts the parent + siblings and lands on the result step', () => {
    const spy = jasmine.createSpy('combine').and.returnValue(
      of({ plan: { blockers: [], clickCard: {} }, audit: { alvysWriteback: 'NotPerformed' } } as unknown as DockCombineResponse),
    );
    const c = build({ combine: spy });
    c['parent'].set(arrival({ loadNumber: 'L1' }));
    c['selectedSiblingIds'].set(['L-2']);
    c['combine']();

    expect(spy).toHaveBeenCalledWith(
      jasmine.objectContaining({ parentLoadId: 'L1', siblingLoadIds: ['L-2'] }),
    );
    expect(c['step']()).toBe('result');
    expect(c['audit']()!.alvysWriteback).toBe('NotPerformed');
  });

  it('combine posts the warehouse code and captures the notification result', () => {
    const spy = jasmine.createSpy('combine').and.returnValue(
      of({
        plan: { blockers: [], clickCard: {} },
        audit: { alvysWriteback: 'NotPerformed' },
        notification: { state: 'Pending', recipients: ['ops@example.com'] },
      } as unknown as DockCombineResponse),
    );
    const c = build({ combine: spy });
    c['selectedWarehouse'].set({ code: 'LAREDO', name: 'Laredo', state: 'TX', nearbyCities: [] });
    c['parent'].set(arrival({ loadNumber: 'L1' }));
    c['selectedSiblingIds'].set(['L-2']);
    c['combine']();

    expect(spy).toHaveBeenCalledWith(jasmine.objectContaining({ warehouseCode: 'LAREDO' }));
    expect(c['notification']()!.state).toBe('Pending');
  });

  it('offers a one-tap Undo after a combine, then marks it undone', () => {
    const undoSpy = jasmine.createSpy('undo').and.returnValue(
      of({ audit: { alvysWriteback: 'NotPerformed' } } as unknown as DockUndoResponse),
    );
    const c = build({ undo: undoSpy });
    c['parent'].set(arrival({ loadNumber: 'L1' }));
    c['selectedSiblingIds'].set(['L-2']);
    c['combine']();
    expect(c['undoAvailable']()).toBeTrue();

    c['undoCombine']();
    expect(undoSpy).toHaveBeenCalled();
    expect(c['undone']()).toBeTrue();
    expect(c['undoAvailable']()).toBeFalse();
    c['ngOnDestroy']();
  });

  it('counts taps on the happy path (parent → sibling → combine)', () => {
    const c = build({});
    c['pickParent'](arrival({ loadNumber: 'L1' }));
    expect(c['tapCount']()).toBe(1);
    c['toggleSibling'](candidate({ loadId: 'L-2' }));
    expect(c['tapCount']()).toBe(2);
    c['combine']();
    expect(c['tapCount']()).toBe(3);
    c['ngOnDestroy']();
  });

  it('retries the notification and updates the chip state without a new audit', () => {
    const renotifySpy = jasmine.createSpy('renotify').and.returnValue(
      of({ state: 'Delivered', recipients: ['ops@example.com'] } as DockNotificationResult),
    );
    const c = build({ renotify: renotifySpy });
    c['parent'].set(arrival({ loadNumber: 'L1' }));
    c['selectedSiblingIds'].set(['L-2']);
    c['retryNotify']();
    expect(renotifySpy).toHaveBeenCalled();
    expect(c['notification']()!.state).toBe('Delivered');
    expect(c['notifyRetrying']()).toBeFalse();
  });

  it('does not combine without a parent and at least one sibling', () => {
    const spy = jasmine.createSpy('combine');
    const c = build({ combine: spy });
    c['parent'].set(arrival({ loadNumber: 'L1' }));
    c['selectedSiblingIds'].set([]);
    c['combine']();
    expect(spy).not.toHaveBeenCalled();
  });

  it('surfaces plan blockers at the review step and disables combine', () => {
    const buildPlanSpy = jasmine.createSpy('buildPlan').and.returnValue(
      of(plan({ blockers: ['Parent load is outside the Laredo→Dallas corridor.'] })),
    );
    const combineSpy = jasmine.createSpy('combine');
    const c = build({ combine: combineSpy }, { buildPlan: buildPlanSpy });
    c['parent'].set(arrival({ loadNumber: 'L-OFF' }));
    c['selectedSiblingIds'].set(['L-2']);

    c['goToReview']();
    expect(c['step']()).toBe('review');
    expect(buildPlanSpy).toHaveBeenCalled();
    expect(c['hasBlockers']()).toBeTrue();
    expect(c['previewBlockers']()).toContain('Parent load is outside the Laredo→Dallas corridor.');

    // A blocked plan must not combine — the button is disabled and the guard is a no-op.
    c['combine']();
    expect(combineSpy).not.toHaveBeenCalled();
  });

  it('review with a clean plan allows combine', () => {
    const c = build({}, { buildPlan: () => of(plan({ blockers: [] })) });
    c['parent'].set(arrival({ loadNumber: 'L1' }));
    c['selectedSiblingIds'].set(['L-2']);
    c['goToReview']();
    expect(c['hasBlockers']()).toBeFalse();
  });

  it('loads the parent equipment preferred pairing at review from the dispatch planner', () => {
    const getPreferredPairing = jasmine.createSpy('getPreferredPairing').and.returnValue(
      of({ resolved: true, driver1Id: 'D-9', truckId: 'TRK-1', trailerId: 'TRL-7', source: 't' }),
    );
    const c = build({}, undefined, { getPreferredPairing });
    c['parent'].set(
      arrival({
        loadNumber: 'L1',
        truck: { id: 'TRK-1', unit: '101', equipmentType: null, lengthFeet: null, fleetName: null, ownership: 'Unknown' },
        trailer: { id: 'TRL-7', unit: '900', equipmentType: null, lengthFeet: null, fleetName: null, ownership: 'Unknown' },
      }),
    );
    c['selectedSiblingIds'].set(['L-2']);

    c['goToReview']();

    expect(getPreferredPairing).toHaveBeenCalledWith({ truckId: 'TRK-1', trailerId: 'TRL-7' });
    expect(c['hasPreferredPairing']()).toBeTrue();
    expect(c['preferredPairing']()?.driver1Id).toBe('D-9');
  });

  it('does not query the planner when the parent has no truck or trailer id', () => {
    const getPreferredPairing = jasmine.createSpy('getPreferredPairing');
    const c = build({}, undefined, { getPreferredPairing });
    c['parent'].set(arrival({ loadNumber: 'L1', truck: null, trailer: null }));
    c['selectedSiblingIds'].set(['L-2']);

    c['goToReview']();

    expect(getPreferredPairing).not.toHaveBeenCalled();
    expect(c['preferredPairing']()).toBeNull();
  });

  it('routes a 422 blocked-plan combine back to review with its blockers', () => {
    const blocked = plan({ blockers: ['A sibling is Never-consolidate.'] });
    const combineSpy = jasmine
      .createSpy('combine')
      .and.returnValue(throwError(() => ({ status: 422, error: blocked })));
    const c = build({ combine: combineSpy });
    c['parent'].set(arrival({ loadNumber: 'L1' }));
    c['selectedSiblingIds'].set(['L-2']);

    c['combine']();
    expect(c['step']()).toBe('review');
    expect(c['previewBlockers']()).toContain('A sibling is Never-consolidate.');
    expect(c['loading']()).toBeFalse();
  });

  it('defaults sibling sort to Best match (untouched server order)', () => {
    const c = build({
      getCandidates: () =>
        of({
          corridorCode: 'LAREDO_TO_DALLAS',
          candidates: [
            candidate({ loadId: 'A', revenue: 100 }),
            candidate({ loadId: 'B', revenue: 900 }),
          ],
          scanTruncated: false,
        } as ConsolidationCandidateResponse),
    });
    c['pickParent'](arrival({ loadNumber: 'L1' }));
    expect(c['candidateSort']()).toBe('best');
    expect(c['sortedCandidates']().map((x) => x.loadId)).toEqual(['A', 'B']);
  });

  it('sorts by highest revenue with missing revenue pushed to the bottom', () => {
    const c = build({
      getCandidates: () =>
        of({
          corridorCode: 'LAREDO_TO_DALLAS',
          candidates: [
            candidate({ loadId: 'A', revenue: 100 }),
            candidate({ loadId: 'B', revenue: undefined }),
            candidate({ loadId: 'C', revenue: 900 }),
          ],
          scanTruncated: false,
        } as ConsolidationCandidateResponse),
    });
    c['pickParent'](arrival({ loadNumber: 'L1' }));
    c['setCandidateSort']('revenue');
    expect(c['sortedCandidates']().map((x) => x.loadId)).toEqual(['C', 'A', 'B']);
  });

  it('sorts by earliest pickup with missing pickup pushed to the bottom', () => {
    const c = build({
      getCandidates: () =>
        of({
          corridorCode: 'LAREDO_TO_DALLAS',
          candidates: [
            candidate({ loadId: 'A', scheduledPickupAt: '2026-07-24T08:00:00Z' }),
            candidate({ loadId: 'B', scheduledPickupAt: undefined }),
            candidate({ loadId: 'C', scheduledPickupAt: '2026-07-22T08:00:00Z' }),
          ],
          scanTruncated: false,
        } as ConsolidationCandidateResponse),
    });
    c['pickParent'](arrival({ loadNumber: 'L1' }));
    c['setCandidateSort']('earliest');
    expect(c['sortedCandidates']().map((x) => x.loadId)).toEqual(['C', 'A', 'B']);
  });

  it('formats missing values honestly as an em dash', () => {
    const c = build({});
    expect(c['formatCurrency'](null)).toBe('—');
    expect(c['formatRpm'](undefined)).toBe('—');
    expect(c['formatWeight'](NaN)).toBe('—');
    expect(c['formatCurrency'](1200)).toContain('1,200');
    expect(c['formatRpm'](1.5)).toBe('$1.50 / mi');
  });
});
