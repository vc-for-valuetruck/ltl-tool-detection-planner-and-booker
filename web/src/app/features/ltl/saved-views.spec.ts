import {
  EMPTY_FILTERS,
  FilterState,
  filtersToSnapshot,
  snapshotToFilterState,
} from './saved-views';
import { SavedViewFilters } from './ltl.models';

describe('saved-views serialization', () => {
  it('maps blank strings and empty enums to null in the snapshot', () => {
    const snapshot = filtersToSnapshot(EMPTY_FILTERS, 'PickupDate', false);
    expect(snapshot.keyword).toBeNull();
    expect(snapshot.customer).toBeNull();
    expect(snapshot.originState).toBeNull();
    expect(snapshot.assignment).toBeNull();
    expect(snapshot.billingBadge).toBeNull();
    expect(snapshot.stage).toBeNull();
    expect(snapshot.ltlOnly).toBeFalse();
    expect(snapshot.sort).toBe('PickupDate');
    expect(snapshot.sortDescending).toBeFalse();
  });

  it('trims whitespace-only text fields to null', () => {
    const filters: FilterState = { ...EMPTY_FILTERS, keyword: '   ', customer: ' Acme ' };
    const snapshot = filtersToSnapshot(filters, 'DeliveryDate', true);
    expect(snapshot.keyword).toBeNull();
    expect(snapshot.customer).toBe('Acme');
    expect(snapshot.sort).toBe('DeliveryDate');
    expect(snapshot.sortDescending).toBeTrue();
  });

  it('round-trips a fully populated filter state losslessly', () => {
    const filters: FilterState = {
      keyword: 'reefer',
      customer: 'Globex',
      originState: 'TX',
      originCity: 'Dallas',
      destinationState: 'CA',
      destinationCity: 'Fresno',
      equipmentType: 'Reefer',
      assignment: 'Assigned',
      pickupFrom: '2026-06-01',
      pickupTo: '2026-06-05',
      deliveryFrom: '2026-06-06',
      deliveryTo: '2026-06-10',
      billingBadge: 'ReadyToBill',
      stage: 'Bill',
      ltlOnly: true,
      readyToBill: true,
      missingBillingData: false,
      exceptionsOnly: true,
      blockedOnly: false,
    };
    const snapshot = filtersToSnapshot(filters, 'BillingReadiness', true);
    const restored = snapshotToFilterState(snapshot);
    expect(restored).toEqual(filters);
  });

  it('restores null snapshot values to blank strings and false flags', () => {
    const snapshot: SavedViewFilters = {
      keyword: null,
      customer: null,
      originState: null,
      originCity: null,
      destinationState: null,
      destinationCity: null,
      equipmentType: null,
      assignment: null,
      pickupFrom: null,
      pickupTo: null,
      deliveryFrom: null,
      deliveryTo: null,
      billingBadge: null,
      stage: null,
      ltlOnly: false,
      readyToBill: false,
      missingBillingData: false,
      exceptionsOnly: false,
      blockedOnly: false,
      sort: 'PickupDate',
      sortDescending: false,
    };
    const restored = snapshotToFilterState(snapshot);
    expect(restored).toEqual(EMPTY_FILTERS);
  });
});
