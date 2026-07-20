import { PlanDetail } from './plan-detail';
import { LtlLoadSummary } from './ltl.models';
import { ConsolidationAuditRecord, ConsolidationTrailerFit } from './consolidation.models';
import { LtlService } from './ltl.service';
import { ConsolidationService } from './consolidation.service';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';

/**
 * Focused tests for the plan-detail projected-uplift threading (#77) and the Alvys deep-link
 * (#78). The component is constructed directly with stubbed route/router/service so no HTTP is
 * made — ngOnInit is not invoked; the parent/sibling signals are set to drive the computeds.
 */
function load(partial: Partial<LtlLoadSummary>): LtlLoadSummary {
  return { id: 'X', ...partial } as LtlLoadSummary;
}

describe('PlanDetail', () => {
  function newComponent(
    navState: Record<string, unknown> | null = null,
    consolidationStub: Partial<ConsolidationService> = {},
  ): PlanDetail {
    const routeStub = {
      snapshot: { paramMap: convertToParamMap({}), queryParamMap: convertToParamMap({}) },
    };
    const routerStub = {
      getCurrentNavigation: () => (navState ? { extras: { state: navState } } : null),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: ActivatedRoute, useValue: routeStub },
        { provide: Router, useValue: routerStub },
        { provide: LtlService, useValue: {} },
        { provide: ConsolidationService, useValue: consolidationStub },
      ],
    });
    return TestBed.runInInjectionContext(() => new PlanDetail());
  }

  function fit(partial: Partial<ConsolidationTrailerFit>): ConsolidationTrailerFit {
    return {
      verdict: 'Fits',
      rationale: '',
      estimatedFit: true,
      capacityExceeded: false,
      weightUnknown: false,
      ...partial,
    };
  }

  it('derives projected uplift as combined revenue minus parent linehaul', () => {
    const c = newComponent();
    c.parent.set(load({ id: 'P', loadNumber: 'L-1', revenue: 5000, mileage: 500 }));
    c.siblings.set([load({ id: 'S1', loadNumber: 'L-2', revenue: 3000 })]);

    // combined = 8000, parent revenue = 5000 → uplift = 3000
    expect(c.combinedRevenue()).toBe(8000);
    expect(c.projectedUplift()).toBe(3000);
  });

  it('prefers projected uplift carried via router state over the derived value', () => {
    const c = newComponent({ projectedUplift: 4250 });
    c.parent.set(load({ id: 'P', revenue: 5000 }));
    c.siblings.set([load({ id: 'S1', revenue: 3000 })]);

    // Derived would be 3000, but the exact queue-card figure (4250) wins.
    expect(c.projectedUplift()).toBe(4250);
  });

  it('sums sibling loaded miles as child miles, null when none report miles', () => {
    const c = newComponent();
    c.siblings.set([
      load({ id: 'S1', loadedMiles: 300 }),
      load({ id: 'S2', loadedMiles: 150 }),
    ]);
    expect(c.childLoadedMiles()).toBe(450);

    c.siblings.set([load({ id: 'S3', loadedMiles: null })]);
    expect(c.childLoadedMiles()).toBeNull();
  });

  it('builds an internal load-detail route from the load number', () => {
    const c = newComponent();
    expect(c.internalLoadUrl(load({ loadNumber: 'L-100234' }))).toEqual(['/ltl/loads', 'L-100234']);
  });

  it('falls back to id when the load number is absent', () => {
    const c = newComponent();
    expect(c.internalLoadUrl(load({ id: 'abc-123', loadNumber: undefined }))).toEqual([
      '/ltl/loads',
      'abc-123',
    ]);
  });

  describe('trailer-fit verdict (#76)', () => {
    it('flags a DoesNotFit verdict as a hard fail', () => {
      const c = newComponent();
      c.trailerFit.set(fit({ verdict: 'DoesNotFit', capacityExceeded: true }));
      expect(c.trailerFitFails()).toBe(true);
      expect(c.trailerFitOk()).toBe(false);
    });

    it('flags a Fits verdict as ok', () => {
      const c = newComponent();
      c.trailerFit.set(fit({ verdict: 'Fits' }));
      expect(c.trailerFitOk()).toBe(true);
      expect(c.trailerFitFails()).toBe(false);
    });

    it('treats an Unknown verdict as neither pass nor fail', () => {
      const c = newComponent();
      c.trailerFit.set(fit({ verdict: 'Unknown' }));
      expect(c.trailerFitOk()).toBe(false);
      expect(c.trailerFitFails()).toBe(false);
    });

    it('renders the combined weight with a "≥" prefix when weight is unknown', () => {
      const c = newComponent();
      expect(c.formatFitWeight(fit({ totalWeightLbs: 12000, weightUnknown: true }))).toBe(
        '≥ 12,000 lb',
      );
    });

    it('renders an exact combined weight when weight is known', () => {
      const c = newComponent();
      expect(c.formatFitWeight(fit({ totalWeightLbs: 22000, weightUnknown: false }))).toBe(
        '22,000 lb',
      );
    });

    it('shows an em dash for weight when no total is available', () => {
      const c = newComponent();
      expect(c.formatFitWeight(fit({ totalWeightLbs: undefined }))).toBe('—');
    });

    it('formats a utilization ratio as a whole percent, dash when unknown', () => {
      const c = newComponent();
      expect(c.formatPercent(0.62)).toBe('62%');
      expect(c.formatPercent(undefined)).toBe('—');
    });

    it('formats linear feet with a unit, dash when unknown', () => {
      const c = newComponent();
      expect(c.formatLinearFeet(24.5)).toBe('24.5 ft');
      expect(c.formatLinearFeet(undefined)).toBe('—');
    });
  });

  describe('loading-order labels (item 5)', () => {
    it('assigns mid to the first sibling and tail to the second', () => {
      const c = newComponent();
      expect(c.loadPosition(0)).toBe('mid');
      expect(c.loadPosition(1)).toBe('tail');
      expect(c.loadPosition(2)).toBe('mid');
    });
  });

  describe('individual RPM + uplift line (item 7)', () => {
    it('averages each load own RPM, skipping loads missing revenue or miles', () => {
      const c = newComponent();
      c.parent.set(load({ id: 'P', revenue: 2000, mileage: 1000 }));
      c.siblings.set([
        load({ id: 'S1', revenue: 3000, mileage: 1000 }),
        load({ id: 'S2', revenue: undefined, mileage: 1000 }),
      ]);
      // parent 2, S1 3, S2 skipped → (2+3)/2 = 2.5
      expect(c.individualRpm()).toBe(2.5);
    });

    it('summarizes dollars and RPM percent', () => {
      const c = newComponent();
      c.parent.set(load({ id: 'P', revenue: 5000, mileage: 1000 }));
      c.siblings.set([load({ id: 'S1', revenue: 3000, mileage: 1000 })]);
      // uplift $3000; combined RPM 8, individual (5+3)/2=4 → +100% RPM
      expect(c.upliftLine()).toContain('+$3,000 combined revenue');
      expect(c.upliftLine()).toContain('+100% RPM');
      expect(c.upliftLine()).toContain('vs individual dispatch');
    });

    it('degrades to a dash line when nothing is computable', () => {
      const c = newComponent();
      expect(c.upliftLine()).toBe('— vs individual dispatch');
    });
  });

  describe('delivered-example gating (queue honesty)', () => {
    it('flags the plan as a delivered example when a load status is Delivered', () => {
      const c = newComponent();
      c.parent.set(load({ id: 'P', status: 'Delivered' }));
      c.siblings.set([load({ id: 'S1', status: 'Delivered' })]);
      expect(c.deliveredExample()).toBeTrue();
    });

    it('flags a delivered example when any single load is closed, even if others are open', () => {
      const c = newComponent();
      c.parent.set(load({ id: 'P', status: 'Dispatched' }));
      c.siblings.set([load({ id: 'S1', status: 'Invoiced' })]);
      expect(c.deliveredExample()).toBeTrue();
    });

    it('treats open freight (Available/Dispatched) as dispatchable', () => {
      const c = newComponent();
      c.parent.set(load({ id: 'P', status: 'Available' }));
      c.siblings.set([load({ id: 'S1', status: 'Dispatched' })]);
      expect(c.deliveredExample()).toBeFalse();
    });

    it('does not record an audit for a delivered example', () => {
      let calls = 0;
      const c = newComponent(null, {
        recordPlanAudit: () => {
          calls++;
          return of({} as ConsolidationAuditRecord);
        },
      });
      c.parent.set(load({ id: 'P', status: 'Delivered' }));
      c.siblings.set([load({ id: 'S1', status: 'Delivered' })]);
      c.recordAudit();
      expect(calls).toBe(0);
      expect(c.auditRecord()).toBeNull();
    });
  });

  describe('recordAudit (item 7)', () => {
    function auditRecord(): ConsolidationAuditRecord {
      return {
        id: 'audit-1',
        corridorCode: 'LAREDO_TO_DALLAS',
        parentLoadId: 'P',
        siblingLoadIds: ['S1'],
        siblingLoadNumbers: ['L-2'],
        blockers: [],
        alvysWriteback: 'NotPerformed',
        recordedBy: 'demo.user',
        recordedAt: '2026-07-20T00:00:00Z',
      };
    }

    it('records the audit and stores the returned record', () => {
      const c = newComponent(null, { recordPlanAudit: () => of(auditRecord()) });
      c.parent.set(load({ id: 'P' }));
      c.siblings.set([load({ id: 'S1' })]);
      c.recordAudit();
      expect(c.auditRecord()?.id).toBe('audit-1');
      expect(c.recordingAudit()).toBeFalse();
      expect(c.auditError()).toBeNull();
    });

    it('surfaces an error and clears the recording flag on failure', () => {
      const c = newComponent(null, {
        recordPlanAudit: () => throwError(() => ({ message: 'boom' })),
      });
      c.parent.set(load({ id: 'P' }));
      c.siblings.set([load({ id: 'S1' })]);
      c.recordAudit();
      expect(c.auditRecord()).toBeNull();
      expect(c.auditError()).toBe('boom');
      expect(c.recordingAudit()).toBeFalse();
    });

    it('does not double-record once a record exists', () => {
      let calls = 0;
      const c = newComponent(null, {
        recordPlanAudit: () => {
          calls++;
          return of(auditRecord());
        },
      });
      c.parent.set(load({ id: 'P' }));
      c.siblings.set([load({ id: 'S1' })]);
      c.recordAudit();
      c.recordAudit();
      expect(calls).toBe(1);
    });
  });
});
