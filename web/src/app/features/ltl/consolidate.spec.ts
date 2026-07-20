import { Consolidate } from './consolidate';
import {
  ConsolidationCandidate,
  ConsolidationCandidateResponse,
  ConsolidationPlanResponse,
} from './consolidation.models';
import { ConsolidationService } from './consolidation.service';
import { TestBed } from '@angular/core/testing';
import { HttpClientTestingModule } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { RUNTIME_CONFIG, RuntimeConfig } from '../../runtime-config';

const TEST_RUNTIME_CONFIG: RuntimeConfig = {
  tenantId: 'test-tenant',
  clientId: 'test-client',
  apiScope: 'api://test/.default',
  apiBaseUrl: '/api',
};

/**
 * Focused component tests for the Consolidate tab. We construct the component with a stubbed
 * ConsolidationService and drive it through the same states the operator will see. No live
 * HTTP calls are made — the network client shape stays the responsibility of the service tests.
 */
function makeCandidate(
  partial: Partial<ConsolidationCandidate>,
): ConsolidationCandidate {
  return {
    loadId: partial.loadId ?? 'L-1',
    loadNumber: partial.loadNumber,
    customerName: partial.customerName,
    originLabel: partial.originLabel,
    destinationLabel: partial.destinationLabel,
    scheduledPickupAt: partial.scheduledPickupAt,
    scheduledDeliveryAt: partial.scheduledDeliveryAt,
    revenue: partial.revenue,
    weightLbs: partial.weightLbs,
    corridorCode: partial.corridorCode ?? 'LAREDO_TO_DALLAS',
    factors: partial.factors ?? [],
    isBlocked: partial.isBlocked ?? false,
    customerTier: partial.customerTier ?? 'Allowed',
  };
}

describe('Consolidate component', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HttpClientTestingModule],
      providers: [
        provideRouter([]),
        { provide: RUNTIME_CONFIG, useValue: TEST_RUNTIME_CONFIG },
      ],
    });
  });

  function newComponent(): Consolidate {
    return TestBed.runInInjectionContext(() => new Consolidate());
  }

  it('starts with no candidates and disables Build plan', () => {
    const c = newComponent();
    expect(c.candidateResponse()).toBeNull();
    expect(c.canBuildPlan()).toBeFalse();
    expect(c.hasSelection()).toBeFalse();
  });

  it('toggles siblings on and off (non-blocked candidates)', () => {
    const c = newComponent();
    c.candidateResponse.set({
      corridorCode: 'LAREDO_TO_DALLAS',
      seed: {
        id: 'SEED',
        loadNumber: 'L-100234',
        customerName: 'Verdef',
      } as any,
      candidates: [makeCandidate({ loadId: 'S1' })],
      scanTruncated: false,
    } as ConsolidationCandidateResponse);

    c.toggleSibling(makeCandidate({ loadId: 'S1' }));
    expect(c.isSelected('S1')).toBeTrue();
    c.toggleSibling(makeCandidate({ loadId: 'S1' }));
    expect(c.isSelected('S1')).toBeFalse();
  });

  it('does not select a blocked candidate', () => {
    const c = newComponent();
    c.candidateResponse.set({
      corridorCode: 'LAREDO_TO_DALLAS',
      seed: { id: 'SEED' } as any,
      candidates: [makeCandidate({ loadId: 'BLK', isBlocked: true })],
      scanTruncated: false,
    } as ConsolidationCandidateResponse);

    c.toggleSibling(makeCandidate({ loadId: 'BLK', isBlocked: true }));
    expect(c.isSelected('BLK')).toBeFalse();
  });

  it('canRecordAudit is false while the plan has blockers', () => {
    const c = newComponent();
    const blocked: ConsolidationPlanResponse = {
      previewId: 'preview-1',
      corridorCode: 'LAREDO_TO_DALLAS',
      parent: { id: 'SEED' } as any,
      siblings: [],
      combinedRevenue: 0,
      linehaulMiles: 0,
      driverLoadedMiles: 0,
      combinedDriverTripValue: 0,
      combinedRevenuePerMile: 0,
      clickCard: {
        plainText: 'x',
        tripReferenceValue: 'LTL=X',
        mainLoadIdReferenceValue: 'X',
      },
      blockers: ['Sibling not consolidation-eligible.'],
    };
    c.plan.set(blocked);
    expect(c.canRecordAudit()).toBeFalse();
  });

  it('canRecordAudit is true for a clean plan', () => {
    const c = newComponent();
    const clean: ConsolidationPlanResponse = {
      previewId: 'preview-1',
      corridorCode: 'LAREDO_TO_DALLAS',
      parent: { id: 'SEED' } as any,
      siblings: [],
      combinedRevenue: 8200,
      linehaulMiles: 1072,
      driverLoadedMiles: 1050,
      combinedDriverTripValue: 7900,
      combinedRevenuePerMile: 7.65,
      clickCard: {
        plainText: 'x',
        tripReferenceValue: 'LTL=X',
        mainLoadIdReferenceValue: 'X',
      },
      blockers: [],
    };
    c.plan.set(clean);
    expect(c.canRecordAudit()).toBeTrue();
  });

  it('maps fit → chip class', () => {
    const c = newComponent();
    expect(c.chipClass('Good')).toBe('chip chip-good');
    expect(c.chipClass('Tight')).toBe('chip chip-tight');
    expect(c.chipClass('Blocked')).toBe('chip chip-blocked');
    expect(c.chipClass('Unknown')).toBe('chip chip-unknown');
  });

  it('formats currency and numbers with dash fallbacks', () => {
    const c = newComponent();
    expect(c.formatCurrency(1234.5)).toBe('$1,234.50');
    expect(c.formatCurrency(undefined)).toBe('—');
    expect(c.formatNumber(1072)).toBe('1,072');
    expect(c.formatNumber(undefined)).toBe('—');
  });

  it('formats whole-dollar money and RPM with dash fallbacks', () => {
    const c = newComponent();
    expect(c.formatMoney0(1234.5)).toBe('$1,235');
    expect(c.formatMoney0(null)).toBe('—');
    expect(c.formatRpm(1.849)).toBe('$1.85 / mi');
    expect(c.formatRpm(undefined)).toBe('—');
  });

  it('computes combined RPM from plan revenue ÷ parent linehaul miles', () => {
    const c = newComponent();
    c.plan.set({
      combinedRevenue: 8000,
      linehaulMiles: 1000,
      siblings: [],
    } as unknown as ConsolidationPlanResponse);
    expect(c.combinedRpm()).toBe(8);
  });

  it('combined RPM is null when miles are missing (never guessed)', () => {
    const c = newComponent();
    c.plan.set({
      combinedRevenue: 8000,
      linehaulMiles: null,
      siblings: [],
    } as unknown as ConsolidationPlanResponse);
    expect(c.combinedRpm()).toBeNull();
  });

  it('individual RPM averages real per-load RPMs, skipping loads missing inputs', () => {
    const c = newComponent();
    c.candidateResponse.set({
      seed: { id: 'SEED', revenue: 2000 },
      candidates: [],
    } as unknown as ConsolidationCandidateResponse);
    c.plan.set({
      combinedRevenue: 5000,
      linehaulMiles: 1000,
      siblings: [
        { loadId: 'S1', revenue: 3000, loadedMiles: 1000 },
        { loadId: 'S2', revenue: null, loadedMiles: 1000 },
      ],
    } as unknown as ConsolidationPlanResponse);
    // seed: 2000/1000=2 ; S1: 3000/1000=3 ; S2 skipped → avg (2+3)/2 = 2.5
    expect(c.individualRpm()).toBe(2.5);
  });

  it('projected uplift dollars = combined − parent revenue', () => {
    const c = newComponent();
    c.candidateResponse.set({
      seed: { id: 'SEED', revenue: 2000 },
      candidates: [],
    } as unknown as ConsolidationCandidateResponse);
    c.plan.set({
      combinedRevenue: 5000,
      linehaulMiles: 1000,
      siblings: [],
    } as unknown as ConsolidationPlanResponse);
    expect(c.projectedUpliftDollars()).toBe(3000);
  });

  it('upliftText degrades to a dash line when nothing is computable', () => {
    const c = newComponent();
    c.plan.set({
      combinedRevenue: null,
      linehaulMiles: null,
      siblings: [],
    } as unknown as ConsolidationPlanResponse);
    expect(c.upliftText()).toContain('—');
  });
});

/**
 * Regression guard for the config-gating bug: the pilot queue must populate BY DEFAULT on
 * tab-load — no manual seed required — so the corridor banner, fit chips, and Current plan
 * panel are all visible without any app-settings. These tests drive ngOnInit with a stubbed
 * service that returns a corridor whose health carries a seed load.
 */
describe('Consolidate default auto-seed', () => {
  function makeStubService(overrides: Partial<ConsolidationService> = {}): ConsolidationService {
    const seededCandidate = makeCandidate({ loadId: 'S1', loadNumber: 'L-2' });
    return {
      getCorridors: () =>
        of([
          {
            code: 'LAREDO_TO_DALLAS',
            origin: { code: 'LRD', name: 'Laredo', state: 'TX', nearbyCities: [] },
            destination: { code: 'DAL', name: 'Dallas', state: 'TX', nearbyCities: [] },
            pickupWindowDays: 3,
            deliveryWindowDays: 3,
          },
        ]),
      getCorridorHealth: () =>
        of([
          {
            code: 'LAREDO_TO_DALLAS',
            openLoadCount: 4,
            truncated: false,
            originCity: 'Laredo',
            destinationCity: 'Dallas',
            seedLoadId: 'SEED',
            seedLoadNumber: 'L-100234',
          },
        ]),
      getCandidates: () =>
        of({
          corridorCode: 'LAREDO_TO_DALLAS',
          seed: { id: 'SEED', loadNumber: 'L-100234' } as any,
          candidates: [seededCandidate],
          scanTruncated: false,
        } as ConsolidationCandidateResponse),
      buildPlan: () =>
        of({
          previewId: 'preview-1',
          corridorCode: 'LAREDO_TO_DALLAS',
          parent: { id: 'SEED' } as any,
          siblings: [{ loadId: 'S1' }],
          combinedRevenue: 8000,
          linehaulMiles: 1000,
          clickCard: { plainText: 'x', tripReferenceValue: 'LTL=X', mainLoadIdReferenceValue: 'X' },
          blockers: [],
        } as unknown as ConsolidationPlanResponse),
      ...overrides,
    } as unknown as ConsolidationService;
  }

  function componentWith(stub: ConsolidationService): Consolidate {
    TestBed.configureTestingModule({
      imports: [HttpClientTestingModule],
      providers: [
        provideRouter([]),
        { provide: RUNTIME_CONFIG, useValue: TEST_RUNTIME_CONFIG },
        { provide: ConsolidationService, useValue: stub },
      ],
    });
    return TestBed.runInInjectionContext(() => new Consolidate());
  }

  it('auto-seeds the queue and selects the first sibling on tab-load (no manual seed)', () => {
    const c = componentWith(makeStubService());
    c.ngOnInit();
    expect(c.seedInput()).toBe('L-100234');
    expect(c.candidateResponse()?.seed?.id).toBe('SEED');
    expect(c.isSelected('S1')).toBeTrue();
    expect(c.hasSelection()).toBeTrue();
  });

  it('does not auto-seed when the corridor lane is empty (seed absent)', () => {
    const stub = makeStubService({
      getCorridorHealth: () =>
        of([
          {
            code: 'LAREDO_TO_DALLAS',
            openLoadCount: 0,
            truncated: false,
            originCity: 'Laredo',
            destinationCity: 'Dallas',
            seedLoadId: null,
            seedLoadNumber: null,
          },
        ]) as any,
    });
    const c = componentWith(stub);
    c.ngOnInit();
    expect(c.seedInput()).toBe('');
    expect(c.candidateResponse()).toBeNull();
    // With corridors loaded but no seed, the honest empty state drives the UI instead of a
    // blank screen — banner/picker/footer stay visible; the queue shows "nothing to plan today".
    expect(c.corridorReadyNoQueue()).toBeTrue();
    expect(c.selectedCorridorLabel()).toBe('Laredo → Dallas');
  });

  it('clears the empty-state flag once the queue auto-seeds', () => {
    const c = componentWith(makeStubService());
    c.ngOnInit();
    expect(c.candidateResponse()).not.toBeNull();
    expect(c.corridorReadyNoQueue()).toBeFalse();
  });
});
