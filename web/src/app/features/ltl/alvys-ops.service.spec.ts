import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { AlvysOpsService } from './alvys-ops.service';
import { RUNTIME_CONFIG } from '../../runtime-config';
import { AlvysOperationOutcome, AlvysReadinessStatus } from './alvys-ops.models';

describe('AlvysOpsService', () => {
  let service: AlvysOpsService;
  let http: HttpTestingController;

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
    service = TestBed.inject(AlvysOpsService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads readiness status from the ops status route', () => {
    let result: AlvysReadinessStatus | undefined;
    service.status().subscribe((s) => (result = s));

    const req = http.expectOne('/api/alvys/ops/status');
    expect(req.request.method).toBe('GET');
    req.flush({ writebackMode: 'Disabled', operations: [] } as Partial<AlvysReadinessStatus>);

    expect(result?.writebackMode).toBe('Disabled');
  });

  it('posts a dry-run for an operation without executing', () => {
    let result: AlvysOperationOutcome | undefined;
    service.dryRun('create-load-note', { loadNumber: 'L1', noteText: 'hi' }).subscribe(
      (o) => (result = o),
    );

    const req = http.expectOne('/api/alvys/ops/create-load-note/dry-run');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ loadNumber: 'L1', noteText: 'hi' });
    req.flush({
      operationCode: 'create-load-note',
      disposition: 'Simulated',
      executed: false,
    } as Partial<AlvysOperationOutcome>);

    expect(result?.executed).toBeFalse();
    expect(result?.disposition).toBe('Simulated');
  });

  it('posts the read-sync probe to the probe route', () => {
    service.probe().subscribe();
    const req = http.expectOne('/api/alvys/ops/sync/probe');
    expect(req.request.method).toBe('POST');
    req.flush({ writebackMode: 'Disabled', operations: [] } as Partial<AlvysReadinessStatus>);
  });
});
