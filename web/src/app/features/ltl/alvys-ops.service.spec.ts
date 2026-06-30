import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { AlvysOpsService } from './alvys-ops.service';
import { RUNTIME_CONFIG } from '../../runtime-config';
import {
  AlvysOperationRecordView,
  AlvysOperationResponse,
  AlvysReadinessStatus,
} from './alvys-ops.models';

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
    let result: AlvysOperationResponse | undefined;
    service.dryRun('create-load-note', { loadNumber: 'L1', noteText: 'hi' }).subscribe(
      (r) => (result = r),
    );

    const req = http.expectOne('/api/alvys/ops/create-load-note/dry-run');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ loadNumber: 'L1', noteText: 'hi' });
    req.flush({
      outcome: { operationCode: 'create-load-note', disposition: 'Simulated', executed: false },
      replayed: false,
    } as Partial<AlvysOperationResponse>);

    expect(result?.outcome.executed).toBeFalse();
    expect(result?.outcome.disposition).toBe('Simulated');
  });

  it('sends the idempotency key as a header on execute', () => {
    service
      .execute('create-load-note', { loadNumber: 'L1', noteText: 'hi' }, 'idem-1')
      .subscribe();

    const req = http.expectOne('/api/alvys/ops/create-load-note/execute');
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('Idempotency-Key')).toBe('idem-1');
    req.flush({
      outcome: { operationCode: 'create-load-note', disposition: 'AuditOnly', executed: false },
      replayed: false,
    } as Partial<AlvysOperationResponse>);
  });

  it('reads owner operation history with a limit', () => {
    let result: AlvysOperationRecordView[] | undefined;
    service.history(25).subscribe((r) => (result = r));

    const req = http.expectOne('/api/alvys/ops/history?limit=25');
    expect(req.request.method).toBe('GET');
    req.flush([{ id: 'r1', operationCode: 'create-load-note' }] as Partial<AlvysOperationRecordView>[]);

    expect(result?.length).toBe(1);
    expect(result?.[0].id).toBe('r1');
  });

  it('posts the read-sync probe to the probe route', () => {
    service.probe().subscribe();
    const req = http.expectOne('/api/alvys/ops/sync/probe');
    expect(req.request.method).toBe('POST');
    req.flush({ writebackMode: 'Disabled', operations: [] } as Partial<AlvysReadinessStatus>);
  });
});
