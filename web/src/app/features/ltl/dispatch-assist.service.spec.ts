import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { RUNTIME_CONFIG } from '../../runtime-config';
import { DispatchAssembly, DispatchRecommendationsResponse } from './dispatch-assist.models';
import { DispatchAssistService } from './dispatch-assist.service';

describe('DispatchAssistService', () => {
  let service: DispatchAssistService;
  let http: HttpTestingController;
  const base = '/api/ltl/dispatch';

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
    service = TestBed.inject(DispatchAssistService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('GET recommendations forwards a loadId and top', () => {
    let result: DispatchRecommendationsResponse | undefined;
    service.recommendations({ loadId: 'L-1', top: 5 }).subscribe((v) => (result = v));

    const req = http.expectOne((r) => r.url === `${base}/recommendations`);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('loadId')).toBe('L-1');
    expect(req.request.params.get('top')).toBe('5');
    req.flush({
      target: { source: 'test' },
      candidates: [],
      truncated: false,
      alvysPosture: 'Read-only.',
    } as unknown as DispatchRecommendationsResponse);
    expect(result!.truncated).toBeFalse();
  });

  it('GET recommendations forwards ad-hoc lane params and omits absent ones', () => {
    service.recommendations({ originCity: 'Laredo', originState: 'TX' }).subscribe();
    const req = http.expectOne((r) => r.url === `${base}/recommendations`);
    expect(req.request.params.get('originCity')).toBe('Laredo');
    expect(req.request.params.get('originState')).toBe('TX');
    expect(req.request.params.has('loadId')).toBeFalse();
    expect(req.request.params.has('top')).toBeFalse();
    req.flush({} as DispatchRecommendationsResponse);
  });

  it('POST assemble sends the chosen driver/truck/trailer body', () => {
    let result: DispatchAssembly | undefined;
    service
      .assemble({ loadId: 'L-1', driverId: 'D-1', truckId: 'T-1', score: 88, reasons: ['preferred pairing'] })
      .subscribe((v) => (result = v));

    const req = http.expectOne(`${base}/assemble`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({
      loadId: 'L-1',
      driverId: 'D-1',
      truckId: 'T-1',
      score: 88,
      reasons: ['preferred pairing'],
    });
    req.flush({ id: 'asm-1', notify: { state: 'NotEnabled' } } as unknown as DispatchAssembly);
    expect(result!.id).toBe('asm-1');
  });

  it('retries a transient 5xx on recommendations then succeeds (no false "no data")', fakeAsync(() => {
    let result: DispatchRecommendationsResponse | undefined;
    let errored = false;
    service.recommendations({ originState: 'TX' }).subscribe({
      next: (v) => (result = v),
      error: () => (errored = true),
    });

    // First attempt hiccups (gateway 503) — the client retries after a short backoff.
    http.expectOne((r) => r.url === `${base}/recommendations`).flush('upstream busy', {
      status: 503,
      statusText: 'Service Unavailable',
    });
    tick(400);
    // Retry succeeds; the subscriber sees data, never the error branch.
    http
      .expectOne((r) => r.url === `${base}/recommendations`)
      .flush({ candidates: [], truncated: false } as unknown as DispatchRecommendationsResponse);

    expect(errored).toBeFalse();
    expect(result).toBeDefined();
  }));

  it('does NOT retry a 404 (unresolvable load) — surfaces the deterministic error at once', fakeAsync(() => {
    let status: number | undefined;
    service.recommendations({ loadId: 'nope' }).subscribe({
      next: () => undefined,
      error: (e) => (status = e.status),
    });

    http.expectOne((r) => r.url === `${base}/recommendations`).flush('not found', {
      status: 404,
      statusText: 'Not Found',
    });
    tick(1000);
    // No second request is issued (afterEach http.verify() would fail if one were pending).
    http.expectNone((r) => r.url === `${base}/recommendations`);
    expect(status).toBe(404);
  }));

  it('GET assemblies forwards the max and optional loadId', () => {
    service.assemblies('L-1', 25).subscribe();
    const req = http.expectOne((r) => r.url === `${base}/assemblies`);
    expect(req.request.params.get('loadId')).toBe('L-1');
    expect(req.request.params.get('max')).toBe('25');
    req.flush([]);
  });
});
