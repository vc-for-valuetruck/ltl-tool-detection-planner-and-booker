import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { LtlDocumentUpload } from './ltl-document-upload';
import { AlvysOpsService } from './alvys-ops.service';
import {
  AlvysOperationRecordView,
  AlvysOperationResponse,
  AlvysReadinessStatus,
} from './alvys-ops.models';

function record(partial: Partial<AlvysOperationRecordView> = {}): AlvysOperationRecordView {
  return {
    id: 'r1',
    operationCode: 'upload-load-document',
    channel: 'Execute',
    payloadHash: 'h',
    mode: 'Disabled',
    disposition: 'AuditOnly',
    status: 'Recorded',
    attemptCount: 1,
    correlationId: 'c1',
    reconciliationState: 'NotApplicable',
    createdAt: '2026-07-21T10:00:00Z',
    updatedAt: '2026-07-21T10:00:00Z',
    ...partial,
  };
}

describe('LtlDocumentUpload', () => {
  function build(stub: Partial<AlvysOpsService>): LtlDocumentUpload {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [{ provide: AlvysOpsService, useValue: stub }],
    });
    const c = TestBed.runInInjectionContext(() => new LtlDocumentUpload());
    c.loadNumber = 'L-1001';
    return c;
  }

  const status = (partial: Partial<AlvysReadinessStatus>): AlvysReadinessStatus =>
    ({ writebackMode: 'Disabled', operations: [], ...partial }) as AlvysReadinessStatus;

  it('reports willPush=false when sandbox execution is not configured', () => {
    const c = build({ status: () => of(status({ sandboxExecutionConfigured: false })) });
    c.ngOnInit();
    expect(c['willPush']()).toBeFalse();
  });

  it('reports willPush=true when sandbox execution is configured', () => {
    const c = build({ status: () => of(status({ sandboxExecutionConfigured: true })) });
    c.ngOnInit();
    expect(c['willPush']()).toBeTrue();
  });

  it('cannot submit without a file selected', () => {
    const c = build({ status: () => of(status({})) });
    c.ngOnInit();
    expect(c['canSubmit']()).toBeFalse();
  });

  it('uploads the selected file and surfaces the returned attachment metadata', () => {
    const uploadSpy = jasmine
      .createSpy('uploadLoadDocument')
      .and.returnValue(
        of({
          outcome: { disposition: 'SandboxExecuted' },
          record: record({
            disposition: 'SandboxExecuted',
            reconciliationState: 'Confirmed',
            resultReference: '/docs/att-77.pdf',
          }),
          replayed: false,
        } as AlvysOperationResponse),
      );
    const c = build({ status: () => of(status({})), uploadLoadDocument: uploadSpy });
    c.ngOnInit();

    const file = new File([new Uint8Array([1])], 'pod.pdf', { type: 'application/pdf' });
    c['selectedFile'].set(file);
    c['selectedType'].set('Proof of Delivery');
    expect(c['canSubmit']()).toBeTrue();

    c['submit']();

    expect(uploadSpy).toHaveBeenCalledWith('L-1001', 'Proof of Delivery', file, undefined);
    const r = c['result']()!;
    expect(r.resultReference).toBe('/docs/att-77.pdf');
    expect(c['pushed'](r)).toBeTrue();
    expect(c['reconciliationClass']('Confirmed')).toContain('pill-ok');
  });

  it('renders a Mismatch reconciliation as needing review, never coerced to confirmed', () => {
    const c = build({
      status: () => of(status({})),
      uploadLoadDocument: () =>
        of({
          outcome: { disposition: 'SandboxExecuted' },
          record: record({
            disposition: 'SandboxExecuted',
            reconciliationState: 'Mismatch',
            reconciliationDetail: 'not found on refetch — human review',
          }),
          replayed: false,
        } as AlvysOperationResponse),
    });
    c.ngOnInit();
    c['selectedFile'].set(new File([new Uint8Array([1])], 'pod.pdf', { type: 'application/pdf' }));
    c['submit']();

    const r = c['result']()!;
    expect(r.reconciliationState).toBe('Mismatch');
    expect(c['reconciliationClass']('Mismatch')).toContain('pill-danger');
  });

  it('treats a non-sandbox disposition as recorded-internally, not pushed', () => {
    const c = build({
      status: () => of(status({})),
      uploadLoadDocument: () =>
        of({
          outcome: { disposition: 'AuditOnly' },
          record: record({ disposition: 'AuditOnly' }),
          replayed: false,
        } as AlvysOperationResponse),
    });
    c.ngOnInit();
    c['selectedFile'].set(new File([new Uint8Array([1])], 'pod.pdf', { type: 'application/pdf' }));
    c['submit']();
    expect(c['pushed'](c['result']()!)).toBeFalse();
  });

  it('surfaces an upload error and clears submitting', () => {
    const c = build({
      status: () => of(status({})),
      uploadLoadDocument: () => throwError(() => ({ error: { error: 'bad type' } })),
    });
    c.ngOnInit();
    c['selectedFile'].set(new File([new Uint8Array([1])], 'pod.pdf', { type: 'application/pdf' }));
    c['submit']();
    expect(c['error']()).toBe('bad type');
    expect(c['submitting']()).toBeFalse();
  });
});
