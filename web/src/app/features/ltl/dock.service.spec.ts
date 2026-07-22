import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DockService } from './dock.service';
import { RUNTIME_CONFIG } from '../../runtime-config';
import { DockCombineResponse, DockWarehousesResponse } from './dock.models';

describe('DockService', () => {
  let service: DockService;
  let http: HttpTestingController;
  const base = '/api/ltl/dock';

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
    service = TestBed.inject(DockService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('GET warehouses returns the configured yards', () => {
    let result: DockWarehousesResponse | undefined;
    service.getWarehouses().subscribe((v) => (result = v));

    const req = http.expectOne(`${base}/warehouses`);
    expect(req.request.method).toBe('GET');
    req.flush({ warehouses: [{ code: 'LAREDO', name: 'Laredo', state: 'TX', nearbyCities: [] }] });
    expect(result!.warehouses.length).toBe(1);
  });

  it('GET arrivals forwards the warehouse and optional date', () => {
    service.getArrivals('DALLAS', '2026-07-21').subscribe();
    const req = http.expectOne((r) => r.url === `${base}/arrivals`);
    expect(req.request.params.get('warehouse')).toBe('DALLAS');
    expect(req.request.params.get('date')).toBe('2026-07-21');
    req.flush({});
  });

  it('GET arrivals omits the date when not provided', () => {
    service.getArrivals('LAREDO').subscribe();
    const req = http.expectOne((r) => r.url === `${base}/arrivals`);
    expect(req.request.params.get('warehouse')).toBe('LAREDO');
    expect(req.request.params.has('date')).toBeFalse();
    req.flush({});
  });

  it('GET candidates forwards parentLoadId and the default corridor', () => {
    service.getCandidates('L-1').subscribe();
    const req = http.expectOne((r) => r.url === `${base}/candidates`);
    expect(req.request.params.get('parentLoadId')).toBe('L-1');
    expect(req.request.params.get('corridor')).toBe('LAREDO_TO_DALLAS');
    req.flush({});
  });

  it('POST combine sends the parent + sibling body', () => {
    let result: DockCombineResponse | undefined;
    service
      .combine({ parentLoadId: 'L-1', siblingLoadIds: ['L-2'], corridorCode: 'LAREDO_TO_DALLAS' })
      .subscribe((v) => (result = v));

    const req = http.expectOne(`${base}/combine`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({
      parentLoadId: 'L-1',
      siblingLoadIds: ['L-2'],
      corridorCode: 'LAREDO_TO_DALLAS',
    });
    req.flush({ plan: {}, audit: {} } as unknown as DockCombineResponse);
    expect(result).toBeDefined();
  });

  it('POST bol-packet.pdf requests a blob and returns the raw PDF bytes', () => {
    let result: Blob | undefined;
    service
      .downloadBolPacket({ parentLoadId: 'L-1', siblingLoadIds: ['L-2'], corridorCode: 'LAREDO_TO_DALLAS' })
      .subscribe((v) => (result = v));

    const req = http.expectOne(`${base}/bol-packet.pdf`);
    expect(req.request.method).toBe('POST');
    expect(req.request.responseType).toBe('blob');
    expect(req.request.body.parentLoadId).toBe('L-1');
    req.flush(new Blob(['%PDF-1.4'], { type: 'application/pdf' }));
    expect(result).toBeInstanceOf(Blob);
  });
});
