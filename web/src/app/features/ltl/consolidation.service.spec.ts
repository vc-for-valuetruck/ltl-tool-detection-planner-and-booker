import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ConsolidationService } from './consolidation.service';
import { RUNTIME_CONFIG } from '../../runtime-config';
import {
  CombinedPlanBillingView,
  LaneTemplateView,
} from './consolidation.models';

describe('ConsolidationService — Phase 4 endpoints', () => {
  let service: ConsolidationService;
  let http: HttpTestingController;
  const base = '/api/ltl/consolidation';

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: RUNTIME_CONFIG,
          useValue: { tenantId: '', clientId: '', apiScope: '', apiBaseUrl: '/api' },
        },
      ],
    });
    service = TestBed.inject(ConsolidationService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('GET plan/combined-rpm passes the loadId and maps the billing view', () => {
    let result: CombinedPlanBillingView | undefined;
    service.getCombinedRpm('LTL-42').subscribe((v) => (result = v));

    const req = http.expectOne((r) => r.url === `${base}/plan/combined-rpm`);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('loadId')).toBe('LTL-42');

    const fake: CombinedPlanBillingView = {
      found: true,
      auditId: 'audit-1',
      parentLoadId: 'P',
      siblingLoadNumbers: ['L-2'],
      combinedRevenuePerMile: 1.85,
    };
    req.flush(fake);
    expect(result!.found).toBeTrue();
    expect(result!.combinedRevenuePerMile).toBe(1.85);
  });

  it('POST plan/templates sends the request body and returns the saved view', () => {
    let result: LaneTemplateView | undefined;
    service
      .saveLaneTemplate({ name: 'Weekly Laredo', corridorCode: 'LAREDO_TO_DALLAS' })
      .subscribe((v) => (result = v));

    const req = http.expectOne(`${base}/plan/templates`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ name: 'Weekly Laredo', corridorCode: 'LAREDO_TO_DALLAS' });

    const fake: LaneTemplateView = {
      id: 'lane-1',
      name: 'Weekly Laredo',
      corridorCode: 'LAREDO_TO_DALLAS',
      createdBy: 'demo.user',
      createdAt: '2026-07-21T00:00:00Z',
      updatedAt: '2026-07-21T00:00:00Z',
    };
    req.flush(fake);
    expect(result!.id).toBe('lane-1');
  });

  it('GET plan/templates forwards corridor and customer filters', () => {
    service.getLaneTemplates('LAREDO_TO_DALLAS', 'Masonite').subscribe();
    const req = http.expectOne((r) => r.url === `${base}/plan/templates`);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('corridorCode')).toBe('LAREDO_TO_DALLAS');
    expect(req.request.params.get('customerName')).toBe('Masonite');
    req.flush([]);
  });

  it('GET plan/templates omits absent filters', () => {
    service.getLaneTemplates().subscribe();
    const req = http.expectOne((r) => r.url === `${base}/plan/templates`);
    expect(req.request.params.has('corridorCode')).toBeFalse();
    expect(req.request.params.has('customerName')).toBeFalse();
    req.flush([]);
  });

  it('DELETE plan/templates/{id} URL-encodes the id', () => {
    service.deleteLaneTemplate('lane/42').subscribe();
    const req = http.expectOne(`${base}/plan/templates/lane%2F42`);
    expect(req.request.method).toBe('DELETE');
    req.flush(null);
  });

  it('POST plan/metrics/click-card-copied sends a status-only body', () => {
    service.recordClickCardCopied('LAREDO_TO_DALLAS', 2).subscribe();
    const req = http.expectOne(`${base}/plan/metrics/click-card-copied`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ corridorCode: 'LAREDO_TO_DALLAS', siblingCount: 2 });
    req.flush(null);
  });
});
