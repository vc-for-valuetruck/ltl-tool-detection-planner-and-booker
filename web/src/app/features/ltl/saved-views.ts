import {
  AssignmentState,
  BillingBadge,
  LtlSortField,
  SavedViewFilters,
  WorkflowStage,
} from './ltl.models';

/**
 * Filter shape held in the workbench component — the subset of `LtlSearchQuery` the dispatcher
 * edits directly. Enum-ish selects use `''` for "any" so they bind cleanly to `<select>`; the
 * saved-view snapshot uses `null` instead. The helpers below translate between the two so a view
 * round-trips losslessly.
 */
export interface FilterState {
  keyword: string;
  customer: string;
  originState: string;
  originCity: string;
  destinationState: string;
  destinationCity: string;
  equipmentType: string;
  assignment: AssignmentState | '';
  pickupFrom: string;
  pickupTo: string;
  deliveryFrom: string;
  deliveryTo: string;
  billingBadge: BillingBadge | '';
  stage: WorkflowStage | '';
  ltlOnly: boolean;
  readyToBill: boolean;
  missingBillingData: boolean;
  exceptionsOnly: boolean;
  blockedOnly: boolean;
}

export const EMPTY_FILTERS: FilterState = {
  keyword: '',
  customer: '',
  originState: '',
  originCity: '',
  destinationState: '',
  destinationCity: '',
  equipmentType: '',
  assignment: '',
  pickupFrom: '',
  pickupTo: '',
  deliveryFrom: '',
  deliveryTo: '',
  billingBadge: '',
  stage: '',
  ltlOnly: false,
  readyToBill: false,
  missingBillingData: false,
  exceptionsOnly: false,
  blockedOnly: false,
};

/** Serializes the current filter/sort state into a saved-view snapshot (blank strings → null). */
export function filtersToSnapshot(
  f: FilterState,
  sort: LtlSortField,
  sortDescending: boolean,
): SavedViewFilters {
  const text = (v: string): string | null => (v.trim() ? v.trim() : null);
  return {
    keyword: text(f.keyword),
    customer: text(f.customer),
    originState: text(f.originState),
    originCity: text(f.originCity),
    destinationState: text(f.destinationState),
    destinationCity: text(f.destinationCity),
    equipmentType: text(f.equipmentType),
    assignment: f.assignment || null,
    pickupFrom: text(f.pickupFrom),
    pickupTo: text(f.pickupTo),
    deliveryFrom: text(f.deliveryFrom),
    deliveryTo: text(f.deliveryTo),
    billingBadge: f.billingBadge || null,
    stage: f.stage || null,
    ltlOnly: f.ltlOnly,
    readyToBill: f.readyToBill,
    missingBillingData: f.missingBillingData,
    exceptionsOnly: f.exceptionsOnly,
    blockedOnly: f.blockedOnly,
    sort,
    sortDescending,
  };
}

/** Rebuilds the editable filter state from a saved-view snapshot (null → blank string). */
export function snapshotToFilterState(snapshot: SavedViewFilters): FilterState {
  return {
    keyword: snapshot.keyword ?? '',
    customer: snapshot.customer ?? '',
    originState: snapshot.originState ?? '',
    originCity: snapshot.originCity ?? '',
    destinationState: snapshot.destinationState ?? '',
    destinationCity: snapshot.destinationCity ?? '',
    equipmentType: snapshot.equipmentType ?? '',
    assignment: snapshot.assignment ?? '',
    pickupFrom: snapshot.pickupFrom ?? '',
    pickupTo: snapshot.pickupTo ?? '',
    deliveryFrom: snapshot.deliveryFrom ?? '',
    deliveryTo: snapshot.deliveryTo ?? '',
    billingBadge: snapshot.billingBadge ?? '',
    stage: snapshot.stage ?? '',
    ltlOnly: snapshot.ltlOnly ?? false,
    readyToBill: snapshot.readyToBill ?? false,
    missingBillingData: snapshot.missingBillingData ?? false,
    exceptionsOnly: snapshot.exceptionsOnly ?? false,
    blockedOnly: snapshot.blockedOnly ?? false,
  };
}
