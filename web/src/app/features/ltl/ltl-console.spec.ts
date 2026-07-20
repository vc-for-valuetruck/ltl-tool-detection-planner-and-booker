import { LtlConsole } from './ltl-console';
import { LtlService } from './ltl.service';
import { CapacitySnapshot, LtlLoadSummary, LtlSearchResponse, SavedView, SavedViewCollection } from './ltl.models';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

function load(partial: Partial<LtlLoadSummary>): LtlLoadSummary {
  return { id: 'X', loadNumber: 'L-1', status: 'Open', assignment: 'Unassigned', ...partial } as LtlLoadSummary;
}

function response(items: LtlLoadSummary[], extra: Partial<LtlSearchResponse> = {}): LtlSearchResponse {
  return { page: 1, pageSize: 25, total: items.length, items, truncated: false, ...extra };
}

function view(partial: Partial<SavedView>): SavedView {
  return {
    id: 'v1',
    name: 'My view',
    description: null,
    isBuiltIn: false,
    ownerId: null,
    createdAt: null,
    updatedAt: null,
    filters: {
      keyword: 'abc',
      customer: null,
      originState: 'TX',
      originCity: null,
      destinationState: null,
      destinationCity: null,
      equipmentType: null,
      assignment: 'Unassigned',
      pickupFrom: null,
      pickupTo: null,
      deliveryFrom: null,
      deliveryTo: null,
      billingBadge: null,
      stage: null,
      ltlOnly: true,
      readyToBill: false,
      missingBillingData: false,
      exceptionsOnly: false,
      blockedOnly: false,
      sort: 'Revenue',
      sortDescending: true,
    },
    ...partial,
  };
}

describe('LtlConsole', () => {
  function build(stub: Partial<LtlService>): LtlConsole {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [{ provide: LtlService, useValue: stub }] });
    return TestBed.runInInjectionContext(() => new LtlConsole());
  }

  it('maps filters, quick toggles and sort into the search query', () => {
    const c = build({ search: () => of(response([])) });
    c['filters'].keyword = ' L-100 ';
    c['filters'].originCity = 'Laredo';
    c['filters'].ltlOnly = true;
    c['sort'] = 'Revenue';
    c['sortDescending'] = true;

    const q = c['buildQuery']();
    expect(q.keyword).toBe('L-100');
    expect(q.originCity).toBe('Laredo');
    expect(q.ltlOnly).toBeTrue();
    expect(q.sort).toBe('Revenue');
    expect(q.sortDescending).toBeTrue();
    // Blank filters must be omitted, never sent as empty strings.
    expect(q.customer).toBeUndefined();
    expect(q.readyToBill).toBeUndefined();
  });

  it('populates the grid from the search response and clears loading', () => {
    const c = build({ search: () => of(response([load({})], { total: 42, truncated: true })) });
    c['search']();
    expect(c['loads']().length).toBe(1);
    expect(c['total']()).toBe(42);
    expect(c['truncated']()).toBeTrue();
    expect(c['loading']()).toBeFalse();
  });

  it('surfaces a search error and clears loading', () => {
    const c = build({ search: () => throwError(() => ({ message: 'boom' })) });
    c['search']();
    expect(c['error']()).toBe('boom');
    expect(c['loading']()).toBeFalse();
    expect(c['hasLoads']()).toBeFalse();
  });

  it('toggling a quick filter flips it, clears the active view, and refetches', () => {
    let calls = 0;
    const c = build({ search: () => { calls++; return of(response([])); } });
    c['activeViewId'].set('v1');
    c['toggleQuick']('unassigned');
    expect(c['filters'].assignment).toBe('Unassigned');
    expect(c['activeViewId']()).toBeNull();
    expect(calls).toBe(1);
  });

  it('sortBy toggles direction on the same field and resets on a new field', () => {
    const c = build({ search: () => of(response([])) });
    c['sortBy']('Revenue');
    expect(c['sort']).toBe('Revenue');
    expect(c['sortDescending']).toBeFalse();
    c['sortBy']('Revenue');
    expect(c['sortDescending']).toBeTrue();
    c['sortBy']('Weight');
    expect(c['sort']).toBe('Weight');
    expect(c['sortDescending']).toBeFalse();
  });

  it('applies a saved view into the editable filter state and marks it active', () => {
    const c = build({ search: () => of(response([])) });
    c['applyView'](view({ id: 'v9' }));
    expect(c['filters'].keyword).toBe('abc');
    expect(c['filters'].originState).toBe('TX');
    expect(c['filters'].assignment).toBe('Unassigned');
    expect(c['filters'].ltlOnly).toBeTrue();
    expect(c['sort']).toBe('Revenue');
    expect(c['sortDescending']).toBeTrue();
    expect(c['activeViewId']()).toBe('v9');
  });

  it('loads presets and views, and empties them on failure', () => {
    const collection: SavedViewCollection = { presets: [view({ id: 'p1', isBuiltIn: true })], views: [view({ id: 'v1' })] };
    const ok = build({ listSavedViews: () => of(collection) });
    ok['loadSavedViews']();
    expect(ok['presets']().length).toBe(1);
    expect(ok['views']().length).toBe(1);

    const bad = build({ listSavedViews: () => throwError(() => ({ message: 'x' })) });
    bad['loadSavedViews']();
    expect(bad['presets']().length).toBe(0);
    expect(bad['views']().length).toBe(0);
  });

  it('appends a newly saved view via the API', () => {
    spyOn(window, 'prompt').and.returnValue('Ready to bill TX');
    const created = view({ id: 'new', name: 'Ready to bill TX' });
    const c = build({ createSavedView: () => of(created) });
    c['saveCurrentView']();
    expect(c['views']().some((v) => v.id === 'new')).toBeTrue();
    expect(c['activeViewId']()).toBe('new');
  });

  function snapshot(partial: Partial<CapacitySnapshot> = {}): CapacitySnapshot {
    return {
      generatedAt: '2026-07-20T00:00:00Z',
      activeTrucks: 12,
      totalTrucks: 20,
      inTransitTrips: 5,
      totalTrailers: 30,
      trailersByType: [
        { equipmentType: 'Dry Van', count: 18 },
        { equipmentType: 'Reefer', count: 8 },
        { equipmentType: 'Flatbed', count: 3 },
        { equipmentType: 'Unspecified', count: 1 },
      ],
      truncated: false,
      source: 'Live Alvys',
      ...partial,
    };
  }

  it('loads the capacity snapshot and caps the trailer-type breakdown at four', () => {
    const c = build({ capacityToday: () => of(snapshot()) });
    c['loadCapacity']();
    expect(c['capacity']()?.activeTrucks).toBe(12);
    expect(c['topTrailerTypes'](c['capacity']()!).length).toBe(4);
  });

  it('hides the capacity widget when the snapshot read fails (never blanks the grid)', () => {
    const c = build({ capacityToday: () => throwError(() => ({ message: 'x' })) });
    c['loadCapacity']();
    expect(c['capacity']()).toBeNull();
  });

  it('renders unknown weight and missing money honestly, never zero', () => {
    const c = build({ search: () => of(response([])) });
    expect(c['weightLabel'](load({ weightLbs: null }))).toBe('—');
    expect(c['weightLabel'](load({ weightLbs: 42360 }))).toBe('42,360 lb');
    expect(c['formatCurrency'](null)).toBe('—');
    expect(c['formatCurrency'](1200)).toBe('$1,200');
    expect(c['rpmLabel'](load({ revenuePerMile: null }))).toBe('—');
    expect(c['rpmLabel'](load({ revenuePerMile: 2.5 }))).toBe('$2.50');
  });
});
