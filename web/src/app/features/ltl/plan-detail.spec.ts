import { PlanDetail } from './plan-detail';
import { LtlLoadSummary } from './ltl.models';
import { ConsolidationTrailerFit } from './consolidation.models';
import { LtlService } from './ltl.service';
import { ConsolidationService } from './consolidation.service';
import { TestBed } from '@angular/core/testing';
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
  function newComponent(navState: Record<string, unknown> | null = null): PlanDetail {
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
        { provide: ConsolidationService, useValue: {} },
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

  it('builds an Alvys deep-link from the load number', () => {
    const c = newComponent();
    expect(c.alvysLoadUrl(load({ loadNumber: 'L-100234' }))).toBe(
      'https://va336.alvys.com/loads/L-100234',
    );
  });

  it('falls back to id when the load number is absent', () => {
    const c = newComponent();
    expect(c.alvysLoadUrl(load({ id: 'abc-123', loadNumber: undefined }))).toBe(
      'https://va336.alvys.com/loads/abc-123',
    );
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
});
