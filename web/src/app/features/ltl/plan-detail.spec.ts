import { PlanDetail } from './plan-detail';
import { LaneRateContext, LtlLoadSummary } from './ltl.models';
import {
  ConsolidationAccessorialPreCheck,
  ConsolidationAuditRecord,
  ConsolidationPlanSibling,
  ConsolidationRpmWarning,
  ConsolidationTrailerFit,
} from './consolidation.models';
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
      navigate: () => Promise.resolve(true),
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

    it('labels a Fits verdict PASS (Phase 7.1)', () => {
      const c = newComponent();
      c.trailerFit.set(fit({ verdict: 'Fits' }));
      expect(c.fitVerdictLabel()).toBe('PASS');
    });

    it('labels a capacity-exceeded DoesNotFit as OVER', () => {
      const c = newComponent();
      c.trailerFit.set(fit({ verdict: 'DoesNotFit', capacityExceeded: true }));
      expect(c.fitVerdictLabel()).toBe('OVER');
    });

    it('labels a geometry-only DoesNotFit as DOES NOT FIT', () => {
      const c = newComponent();
      c.trailerFit.set(fit({ verdict: 'DoesNotFit', capacityExceeded: false }));
      expect(c.fitVerdictLabel()).toBe('DOES NOT FIT');
    });

    it('labels an Unknown verdict UNVERIFIED', () => {
      const c = newComponent();
      c.trailerFit.set(fit({ verdict: 'Unknown' }));
      expect(c.fitVerdictLabel()).toBe('UNVERIFIED');
    });

    it('labels a missing fit (engine off) UNVERIFIED', () => {
      const c = newComponent();
      c.trailerFit.set(null);
      expect(c.fitVerdictLabel()).toBe('UNVERIFIED');
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

  describe('lane-rate + customer-policy context (Phase 7.4)', () => {
    function sibling(partial: Partial<ConsolidationPlanSibling>): ConsolidationPlanSibling {
      return {
        loadId: 'S',
        customerTier: 'Unknown',
        customerPolicySource: 'None',
        cautions: [],
        ...partial,
      } as ConsolidationPlanSibling;
    }
    function laneRate(partial: Partial<LaneRateContext>): LaneRateContext {
      return {
        originState: 'TX',
        destinationState: 'IL',
        sampleSize: 0,
        medianRpm: null,
        minRpm: null,
        maxRpm: null,
        basis: 'Recent tenant history, not a market rate.',
        generatedAt: '2026-07-20T00:00:00Z',
        ...partial,
      };
    }

    it('flattens and de-duplicates server sibling cautions', () => {
      const c = newComponent();
      c.planSiblings.set([
        sibling({ loadId: 'S1', cautions: ['Notify customer first', 'Weight missing'] }),
        sibling({ loadId: 'S2', cautions: ['Notify customer first'] }),
      ]);
      expect(c.planCautions()).toEqual(['Notify customer first', 'Weight missing']);
    });

    it('derives one policy chip per distinct customer with its tier and source', () => {
      const c = newComponent();
      c.planSiblings.set([
        sibling({
          loadId: 'S1',
          customerName: 'Masonite',
          customerTier: 'NotifyRequired',
          customerPolicySource: 'DefaultPolicy',
        }),
        sibling({
          loadId: 'S2',
          customerName: 'Masonite',
          customerTier: 'NotifyRequired',
          customerPolicySource: 'DefaultPolicy',
        }),
        sibling({
          loadId: 'S3',
          customerName: 'Acme',
          customerTier: 'Allowed',
          customerPolicySource: 'CustomerNote',
        }),
      ]);
      const policies = c.customerPolicies();
      expect(policies.length).toBe(2);
      expect(policies[0]).toEqual({
        customerName: 'Masonite',
        tier: 'NotifyRequired',
        source: 'DefaultPolicy',
      });
      expect(policies[1]).toEqual({
        customerName: 'Acme',
        tier: 'Allowed',
        source: 'CustomerNote',
      });
    });

    it('reports a computable lane-rate range only when a median is present', () => {
      const c = newComponent();
      c.laneRate.set(laneRate({ sampleSize: 4, medianRpm: 5, minRpm: 4, maxRpm: 6 }));
      expect(c.laneRateHasRange()).toBeTrue();

      c.laneRate.set(laneRate({ sampleSize: 1, medianRpm: null }));
      expect(c.laneRateHasRange()).toBeFalse();
    });

    it('maps each consolidation tier to a dispatcher-facing label', () => {
      const c = newComponent();
      expect(c.tierLabel('Allowed')).toBe('Consolidation allowed');
      expect(c.tierLabel('NotifyRequired')).toBe('Notify customer first');
      expect(c.tierLabel('Never')).toBe('Consolidation not allowed');
      expect(c.tierLabel('Unknown')).toBe('No policy on file');
    });

    it('badges the policy source, hiding the badge when nothing is on file', () => {
      const c = newComponent();
      expect(c.policySourceLabel('CustomerNote')).toBe('from customer note');
      expect(c.policySourceLabel('DefaultPolicy')).toBe('default policy — no customer note');
      expect(c.policySourceLabel('None')).toBeNull();
    });
  });

  describe('EDI-tender enrichment helpers (Phase 7.2)', () => {
    it('labels a tender-derived pallet estimate explicitly "(est.)"', () => {
      const c = newComponent();
      const l = load({
        ediEnrichment: {
          source: 'EDI tender',
          tenderShipmentId: 'S1',
          matchedOn: 'load OrderNumber = tender ShipmentId',
          pieceCount: 1843,
          weightLbs: 42360,
          volume: 1273,
          palletEstimate: 14,
          palletBasis: '1,273 cu ft ÷ 96 ≈ 14 pallets (est.)',
        },
      });
      expect(c.palletLabel(l)).toBe('~14 pallets (est.)');
      expect(c.palletTitle(l)).toContain('(est.)');
      expect(c.ediSourced(l)).toBeTrue();
      expect(c.pieceLabel(l)).toBe('1,843 pcs');
    });

    it('keeps the honest "— pallets" + dock-verify caveat when no tender matched', () => {
      const c = newComponent();
      const l = load({ ediEnrichment: null });
      expect(c.palletLabel(l)).toBe('— pallets');
      expect(c.palletTitle(l)).toContain('visual verify at dock');
      expect(c.ediSourced(l)).toBeFalse();
      expect(c.pieceLabel(l)).toBeNull();
    });

    it('falls back to a matched tender weight when the load carries none', () => {
      const c = newComponent();
      expect(c.weightLabel(load({ weightLbs: 5000 }))).toBe('5,000 lb');
      expect(
        c.weightLabel(
          load({ weightLbs: null, ediEnrichment: { weightLbs: 4200 } as never }),
        ),
      ).toBe('4,200 lb');
      expect(c.weightLabel(load({ weightLbs: null, ediEnrichment: null }))).toBe('— lb');
    });

    it('marks the pallet gap green when enriched, amber when missing', () => {
      const c = newComponent();
      c.parent.set(
        load({ id: 'P', loadNumber: 'L-1', ediEnrichment: { palletEstimate: 8 } as never }),
      );
      c.siblings.set([load({ id: 'S1', loadNumber: 'L-2', ediEnrichment: null })]);
      const gaps = c.honestGaps();
      const enriched = gaps.find((g) => g.text.includes('L-1'));
      const missing = gaps.find((g) => g.text.includes('L-2'));
      expect(enriched?.tone).toBe('green');
      expect(enriched?.text).toContain('est. from EDI tender');
      expect(missing?.tone).toBe('amber');
      expect(missing?.text).toContain('pallet count is missing');
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

  describe('red-RPM warning chip (Phase 4)', () => {
    function warning(partial: Partial<ConsolidationRpmWarning>): ConsolidationRpmWarning {
      return {
        status: 'Ok',
        thresholdPerMile: 1.5,
        rpmPerMile: 2,
        message: '',
        ...partial,
      };
    }

    it('prefers the server driver-math RPM over the client billing RPM for display', () => {
      const c = newComponent();
      c.parent.set(load({ id: 'P', revenue: 5000, mileage: 500 }));
      // Client-side combined RPM would be 5000 / 500 = 10.
      expect(c.combinedRpm()).toBe(10);
      c.serverCombinedRpm.set(1.85);
      // Once the server driver-math RPM arrives it wins.
      expect(c.displayRpm()).toBe(1.85);
    });

    it('falls back to the client billing RPM until the server RPM resolves', () => {
      const c = newComponent();
      c.parent.set(load({ id: 'P', revenue: 4000, mileage: 1000 }));
      expect(c.serverCombinedRpm()).toBeNull();
      expect(c.displayRpm()).toBe(4);
    });

    it('flags below-floor and clears the ok/unavailable flags', () => {
      const c = newComponent();
      c.rpmWarning.set(warning({ status: 'Below', rpmPerMile: 1.1 }));
      expect(c.rpmBelowFloor()).toBeTrue();
      expect(c.rpmUnavailable()).toBeFalse();
    });

    it('flags unavailable (gray) when RPM inputs are missing', () => {
      const c = newComponent();
      c.rpmWarning.set(warning({ status: 'Unavailable', rpmPerMile: undefined }));
      expect(c.rpmUnavailable()).toBeTrue();
      expect(c.rpmBelowFloor()).toBeFalse();
    });

    it('treats an Ok status as neither below nor unavailable', () => {
      const c = newComponent();
      c.rpmWarning.set(warning({ status: 'Ok' }));
      expect(c.rpmBelowFloor()).toBeFalse();
      expect(c.rpmUnavailable()).toBeFalse();
    });
  });

  describe('accessorial pre-checks (Phase 4)', () => {
    function preCheck(
      partial: Partial<ConsolidationAccessorialPreCheck>,
    ): ConsolidationAccessorialPreCheck {
      return {
        loadId: 'L',
        isParent: false,
        evaluated: true,
        candidates: [],
        ...partial,
      };
    }

    it('keeps only evaluated pre-checks that carry at least one candidate', () => {
      const c = newComponent();
      c.accessorialPreChecks.set([
        preCheck({
          loadId: 'P',
          isParent: true,
          candidates: [
            {
              type: 'Detention',
              status: 'Likely',
              reason: 'Long dwell at stop',
              evidence: 'Arrival to departure 4h12m',
              sourceId: null,
              sourceType: null,
            },
          ],
        }),
        preCheck({ loadId: 'S1', evaluated: true, candidates: [] }),
        preCheck({ loadId: 'S2', evaluated: false, candidates: [] }),
      ]);
      const shown = c.accessorialPreChecksWithCandidates();
      expect(shown.length).toBe(1);
      expect(shown[0].loadId).toBe('P');
    });
  });

  describe('openClickCard effectiveness metric (Phase 4)', () => {
    it('fires a status-only click-card-copied signal with the plan corridor and sibling count', () => {
      const calls: Array<{ corridor: string; siblings: number }> = [];
      const c = newComponent(null, {
        recordClickCardCopied: (corridor: string, siblings: number) => {
          calls.push({ corridor, siblings });
          return of(void 0);
        },
      });
      c.parent.set(load({ id: 'P', status: 'Available' }));
      c.siblings.set([load({ id: 'S1', status: 'Available' })]);
      c.planCorridorCode.set('LAREDO_TO_DALLAS');
      c.openClickCard();
      expect(calls).toEqual([{ corridor: 'LAREDO_TO_DALLAS', siblings: 1 }]);
    });

    it('does not fire the metric for a delivered example', () => {
      let calls = 0;
      const c = newComponent(null, {
        recordClickCardCopied: () => {
          calls++;
          return of(void 0);
        },
      });
      c.parent.set(load({ id: 'P', status: 'Delivered' }));
      c.siblings.set([load({ id: 'S1', status: 'Delivered' })]);
      c.openClickCard();
      expect(calls).toBe(0);
    });
  });
});
