import { PlanDetail } from './plan-detail';
import { LtlLoadSummary } from './ltl.models';
import { LtlService } from './ltl.service';
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
      ],
    });
    return TestBed.runInInjectionContext(() => new PlanDetail());
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
});
