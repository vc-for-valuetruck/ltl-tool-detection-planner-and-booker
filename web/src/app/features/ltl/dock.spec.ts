import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { MsalService } from '@azure/msal-angular';
import { RUNTIME_CONFIG } from '../../runtime-config';
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
      // Yard boundary (issue #166): default to the honest grey "unavailable" presence and no
      // opportunities so existing flows are unaffected; individual tests override as needed.
      getPresence: () =>
        of({
          configured: false,
          available: false,
          onRecord: false,
          atYard: false,
          driverPresent: false,
          securityHold: false,
        }),
      getOpportunities: () => of({ opportunities: [] }),
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
        // Session-expired re-auth wiring (see docs/BOUNDARIES.md, issue #164).
        // MsalService is only used by the Sign-in button in the re-auth branch — a light stub
        // is enough for these existing dock behaviour tests. RUNTIME_CONFIG mirrors the shape
        // used across the other feature specs (see dock.service.spec.ts).
        {
          provide: MsalService,
          useValue: { loginRedirect: (_req?: unknown): void => undefined },
        },
        {
          provide: RUNTIME_CONFIG,
          useValue: { tenantId: '', clientId: '', apiScope: '', apiBaseUrl: '/api' },
        },
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

  it('shows a green presence chip when equipment is at the yard', () => {
    const getPresence = jasmine.createSpy('getPresence').and.returnValue(
      of({
        configured: true,
        available: true,
        onRecord: true,
        atYard: true,
        driverPresent: true,
        securityHold: false,
        releasedAt: '2026-07-21T15:30:00Z',
      }),
    );
    const c = build({ getPresence });
    c['parent'].set(
      arrival({
        loadNumber: 'L1',
        truck: { id: 'TRK-1', unit: '101', equipmentType: null, lengthFeet: null, fleetName: null, ownership: 'Unknown' },
      }),
    );
    c['selectedSiblingIds'].set(['L-2']);

    c['goToReview']();

    expect(getPresence).toHaveBeenCalledWith('TRK-1', undefined);
    expect(c['presenceChip']()?.state).toBe('green');
    expect(c['presenceBlocksCombine']()).toBeFalse();
  });

  it('shows an amber chip and still allows combine when equipment is not at the yard', () => {
    const c = build({
      getPresence: () =>
        of({
          configured: true,
          available: true,
          onRecord: true,
          atYard: false,
          driverPresent: false,
          securityHold: false,
        }),
    });
    c['parent'].set(arrival({ loadNumber: 'L1' }));
    c['selectedSiblingIds'].set(['L-2']);

    c['goToReview']();

    expect(c['presenceChip']()?.state).toBe('amber');
    expect(c['presenceBlocksCombine']()).toBeFalse();
  });

  it('blocks combine on a yard security hold (red chip)', () => {
    const combineSpy = jasmine.createSpy('combine');
    const c = build({
      combine: combineSpy,
      getPresence: () =>
        of({
          configured: true,
          available: true,
          onRecord: true,
          atYard: true,
          driverPresent: true,
          securityHold: true,
        }),
    });
    c['parent'].set(arrival({ loadNumber: 'L1' }));
    c['selectedSiblingIds'].set(['L-2']);

    c['goToReview']();
    expect(c['presenceChip']()?.state).toBe('red');
    expect(c['presenceBlocksCombine']()).toBeTrue();

    c['combine']();
    expect(combineSpy).not.toHaveBeenCalled();
  });

  it('shows a grey unavailable chip when the yard integration is off, never fabricating a pass', () => {
    const c = build({}); // default getPresence returns configured:false
    c['parent'].set(arrival({ loadNumber: 'L1' }));
    c['selectedSiblingIds'].set(['L-2']);

    c['goToReview']();

    expect(c['presenceChip']()?.state).toBe('grey');
    expect(c['presenceBlocksCombine']()).toBeFalse();
  });

  it('surfaces yard-originated incoming opportunities on init', () => {
    const c = build({
      getOpportunities: () =>
        of({
          opportunities: [
            {
              id: 'yopp-1',
              draftId: 'DRAFT-1',
              yardCode: 'LAREDO',
              parentLoadId: 'L1',
              siblingLoadIds: ['L-2', 'L-3'],
              freight: [],
              createdByStation: 'DOCK-3',
              receivedAt: '2026-07-21T16:00:00Z',
            },
          ],
        }),
    });
    c.ngOnInit();
    expect(c['opportunities']().length).toBe(1);
    expect(c['opportunities']()[0].parentLoadId).toBe('L1');
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

  it('downloads the BOL packet PDF and saves the returned blob', () => {
    const blob = new Blob(['%PDF-1.4'], { type: 'application/pdf' });
    const spy = jasmine.createSpy('downloadBolPacket').and.returnValue(of(blob));
    spyOn(URL, 'createObjectURL').and.returnValue('blob:fake');
    spyOn(URL, 'revokeObjectURL');
    const c = build({ downloadBolPacket: spy });
    c['parent'].set(arrival({ loadNumber: 'L1' }));
    c['selectedSiblingIds'].set(['L-2']);

    c['downloadPdf']();

    expect(spy).toHaveBeenCalledWith(
      jasmine.objectContaining({ parentLoadId: 'L1', siblingLoadIds: ['L-2'] }),
    );
    expect(URL.createObjectURL).toHaveBeenCalledWith(blob);
    expect(c['pdfDownloading']()).toBeFalse();
  });

  it('does not request a PDF without a parent and at least one sibling', () => {
    const spy = jasmine.createSpy('downloadBolPacket');
    const c = build({ downloadBolPacket: spy });
    c['parent'].set(arrival({ loadNumber: 'L1' }));
    c['selectedSiblingIds'].set([]);
    c['downloadPdf']();
    expect(spy).not.toHaveBeenCalled();
  });

  it('falls back to a legible message when PDF generation fails, pointing at Print', () => {
    const spy = jasmine.createSpy('downloadBolPacket').and.returnValue(throwError(() => ({ message: 'boom' })));
    const c = build({ downloadBolPacket: spy });
    c['parent'].set(arrival({ loadNumber: 'L1' }));
    c['selectedSiblingIds'].set(['L-2']);
    c['downloadPdf']();
    expect(c['copyMessage']()).toContain('Print BOL packet');
    expect(c['pdfDownloading']()).toBeFalse();
  });

  it('formats missing values honestly as an em dash', () => {
    const c = build({});
    expect(c['formatCurrency'](null)).toBe('—');
    expect(c['formatRpm'](undefined)).toBe('—');
    expect(c['formatWeight'](NaN)).toBe('—');
    expect(c['formatCurrency'](1200)).toContain('1,200');
    expect(c['formatRpm'](1.5)).toBe('$1.50 / mi');
  });

  describe('auto-combine mode', () => {
    /** Flush the effect-driven cascade to convergence (each transition needs another tick). */
    function flush(): void {
      for (let i = 0; i < 8; i++) TestBed.tick();
    }

    const laredo = { code: 'LAREDO', name: 'Laredo', state: 'TX', nearbyCities: [] };

    it('is off by default and leaves the manual flow untouched', () => {
      const c = build({});
      expect(c['autoMode']()).toBeFalse();
      c['pickWarehouse'](laredo);
      flush();
      // With auto off, picking a yard advances to arrivals and stops — no auto parent pick.
      expect(c['step']()).toBe('arrivals');
      expect(c['parent']()).toBeNull();
    });

    it('cascades yard → arrivals → siblings → review, then one-tap combines to result', () => {
      const combineSpy = jasmine.createSpy('combine').and.returnValue(
        of({
          plan: { blockers: [], clickCard: { plainText: 'card' } },
          audit: { alvysWriteback: 'NotPerformed' },
          notification: { state: 'Disabled', recipients: [] },
        } as unknown as DockCombineResponse),
      );
      spyOn(window, 'open').and.returnValue(null);
      spyOn(URL, 'createObjectURL').and.returnValue('blob:x');
      spyOn(URL, 'revokeObjectURL');
      const c = build({
        combine: combineSpy,
        downloadBolPacket: () => of(new Blob(['%PDF'])),
        getArrivals: () => of(board([arrival({ tripId: 'a', loadNumber: 'L1', dallasBound: true })])),
        getCandidates: () =>
          of({
            corridorCode: 'LAREDO_TO_DALLAS',
            candidates: [candidate({ loadId: 'L-2' }), candidate({ loadId: 'L-3' })],
            scanTruncated: false,
          } as ConsolidationCandidateResponse),
      });
      c['autoMode'].set(true);
      c['pickWarehouse'](laredo);
      flush();

      expect(c['step']()).toBe('review');
      expect(c['parent']()?.loadNumber).toBe('L1');
      expect(c['selectedSiblingIds']()).toEqual(['L-2', 'L-3']);
      expect(c['autoReady']()).toBeTrue();
      expect(c['autoUsed']()).toBeTrue();

      c['oneTapCombine']();
      expect(combineSpy).toHaveBeenCalled();
      expect(c['step']()).toBe('result');
      c['ngOnDestroy']();
    });

    it('respects the sibling cap', () => {
      const c = build({
        getCandidates: () =>
          of({
            corridorCode: 'LAREDO_TO_DALLAS',
            candidates: [
              candidate({ loadId: 'L-2' }),
              candidate({ loadId: 'L-3' }),
              candidate({ loadId: 'L-4' }),
              candidate({ loadId: 'L-5' }),
            ],
            scanTruncated: false,
          } as ConsolidationCandidateResponse),
      });
      c['autoMode'].set(true);
      c['setAutoSiblingCap'](2);
      c['pickWarehouse'](laredo);
      flush();
      expect(c['selectedSiblingIds']()).toEqual(['L-2', 'L-3']);
    });

    it('never auto-selects a blocked candidate', () => {
      const c = build({
        getCandidates: () =>
          of({
            corridorCode: 'LAREDO_TO_DALLAS',
            candidates: [candidate({ loadId: 'L-2', isBlocked: true }), candidate({ loadId: 'L-3' })],
            scanTruncated: false,
          } as ConsolidationCandidateResponse),
      });
      c['autoMode'].set(true);
      c['pickWarehouse'](laredo);
      flush();
      expect(c['selectedSiblingIds']()).toEqual(['L-3']);
    });

    it('ejects to manual when the plan preview has blockers, without combining', () => {
      const combineSpy = jasmine.createSpy('combine');
      const c = build(
        { combine: combineSpy },
        { buildPlan: () => of(plan({ blockers: ['A sibling is Never-consolidate.'] })) },
      );
      c['autoMode'].set(true);
      c['pickWarehouse'](laredo);
      flush();

      expect(c['step']()).toBe('review');
      expect(c['autoMode']()).toBeFalse();
      expect(c['autoReady']()).toBeFalse();
      expect(c['autoEjectReason']()).toContain('blockers');
      expect(combineSpy).not.toHaveBeenCalled();
    });

    it('ejects to manual when no inbound arrival has a load number', () => {
      const c = build({ getArrivals: () => of(board([arrival({ loadNumber: null })])) });
      c['autoMode'].set(true);
      c['pickWarehouse'](laredo);
      flush();
      expect(c['autoMode']()).toBeFalse();
      expect(c['autoEjectReason']()).toContain('load number');
      expect(c['parent']()).toBeNull();
    });

    it('ejects to manual when no siblings are suggested', () => {
      const c = build({
        getCandidates: () =>
          of({
            corridorCode: 'LAREDO_TO_DALLAS',
            candidates: [],
            scanTruncated: false,
          } as ConsolidationCandidateResponse),
      });
      c['autoMode'].set(true);
      c['pickWarehouse'](laredo);
      flush();
      expect(c['autoMode']()).toBeFalse();
      expect(c['autoEjectReason']()).toContain('manually');
      expect(c['step']()).toBe('siblings');
    });

    it('ejects to manual when the arrivals read fails', () => {
      const c = build({ getArrivals: () => throwError(() => ({ message: 'boom' })) });
      c['autoMode'].set(true);
      c['pickWarehouse'](laredo);
      flush();
      expect(c['autoMode']()).toBeFalse();
      expect(c['autoEjectReason']()).toContain('Alvys read failed');
    });

    it('does not one-tap combine when not armed or when blockers exist', () => {
      const combineSpy = jasmine.createSpy('combine');
      const c = build({ combine: combineSpy });
      c['parent'].set(arrival({ loadNumber: 'L1' }));
      c['selectedSiblingIds'].set(['L-2']);
      // autoReady is false → guard blocks.
      c['oneTapCombine']();
      expect(combineSpy).not.toHaveBeenCalled();
    });
  });
});
