import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { LtlService } from './ltl.service';
import { LtlInvoiceStudio } from './ltl-invoice-studio';
import { InvoiceSummary, InvoiceView } from './invoice-studio.models';

function summary(partial: Partial<InvoiceSummary> = {}): InvoiceSummary {
  return {
    id: 'inv-1',
    invoiceNumber: 'INV-1001',
    status: 'Draft',
    customerName: 'Acme Freight',
    parentLoadNumber: '100482',
    loadCount: 2,
    loadsMissingBolCount: 1,
    invoiceTotal: 3200,
    combinedRevenuePerMile: 2.5,
    updatedAt: '2026-07-23T10:00:00Z',
    alvysWriteback: 'NotPerformed',
    ...partial,
  };
}

function view(partial: Partial<InvoiceView> = {}): InvoiceView {
  return {
    id: 'inv-1',
    invoiceNumber: 'INV-1001',
    status: 'Draft',
    corridorCode: 'LRD-DAL',
    customerId: null,
    customerName: 'Acme Freight',
    parentLoadId: 'L-1',
    parentLoadNumber: '100482',
    notes: null,
    loads: [
      {
        loadId: 'L-1',
        loadNumber: '100482',
        isParent: true,
        customerName: 'Acme Freight',
        status: 'Delivered',
        alvysLoadUrl: null,
        bolPresent: true,
        bolArtifactId: null,
        loadedMiles: 500,
        driverTripRate: 900,
        charges: [{ id: 'c1', type: 'Linehaul', description: null, amount: 2000 }],
        lineTotal: 2000,
      },
      {
        loadId: 'L-2',
        loadNumber: '100483',
        isParent: false,
        customerName: 'Acme Freight',
        status: 'Delivered',
        alvysLoadUrl: null,
        bolPresent: false,
        bolArtifactId: null,
        loadedMiles: null,
        driverTripRate: null,
        charges: [{ id: 'c2', type: 'Accessorial', description: 'Detention', amount: 1200 }],
        lineTotal: 1200,
      },
    ],
    editHistory: [{ at: '2026-07-23T10:00:00Z', by: 'ops@vt.com', action: 'Assembled', detail: null }],
    invoiceTotal: 3200,
    combinedRevenue: 3200,
    combinedDriverTripValue: 900,
    driverLoadedMiles: 500,
    combinedRevenuePerMile: 6.4,
    loadsMissingBol: ['100483'],
    createdBy: 'ops@vt.com',
    createdAt: '2026-07-23T10:00:00Z',
    updatedBy: 'ops@vt.com',
    updatedAt: '2026-07-23T10:00:00Z',
    finalizedAt: null,
    finalizedBy: null,
    alvysWriteback: 'NotPerformed',
    ...partial,
  };
}

describe('LtlInvoiceStudio', () => {
  function build(stub: Partial<LtlService>): LtlInvoiceStudio {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [{ provide: LtlService, useValue: stub }],
    });
    return TestBed.runInInjectionContext(() => new LtlInvoiceStudio());
  }

  it('loads the invoice list on init', () => {
    const c = build({ listInvoices: () => of([summary()]) });
    c.ngOnInit();
    expect(c['invoices']().length).toBe(1);
    expect(c['listLoading']()).toBeFalse();
    expect(c['listError']()).toBeNull();
  });

  it('surfaces a list error without fabricating rows', () => {
    const c = build({ listInvoices: () => throwError(() => ({ status: 500 })) });
    c.ngOnInit();
    expect(c['invoices']().length).toBe(0);
    expect(c['listError']()).toContain('Could not load');
  });

  it('opens an invoice detail on select', () => {
    const c = build({ listInvoices: () => of([]), getInvoice: () => of(view()) });
    c['select']('inv-1');
    expect(c['selected']()?.invoiceNumber).toBe('INV-1001');
    expect(c['detailLoading']()).toBeFalse();
  });

  it('seeds the assemble form with a parent and one sibling', () => {
    const c = build({ listInvoices: () => of([]) });
    c['startAssemble']();
    const loads = c['draftLoads']();
    expect(loads.length).toBe(2);
    expect(loads[0].isParent).toBeTrue();
    expect(loads[1].isParent).toBeFalse();
    expect(c['showForm']()).toBeTrue();
    expect(c['editingId']()).toBeNull();
  });

  it('computes line and invoice totals live over draft charges', () => {
    const c = build({ listInvoices: () => of([]) });
    c['startAssemble']();
    c['draftLoads']()[0].charges[0].amount = 1500;
    c['addLoad']();
    c['addCharge'](2);
    c['draftLoads']()[2].charges[0].amount = 400;
    expect(c['loadTotal'](c['draftLoads']()[0])).toBe(1500);
    expect(c['formTotal']()).toBe(1900);
  });

  it('flags draft loads missing a BOL', () => {
    const c = build({ listInvoices: () => of([]) });
    c['startAssemble']();
    c['draftLoads']()[0].loadNumber = '100482';
    c['draftLoads']()[0].bolPresent = true;
    c['draftLoads']()[1].loadNumber = '100483';
    c['draftLoads']()[1].bolPresent = false;
    expect(c['formMissingBol']()).toEqual(['100483']);
  });

  it('enforces a single consolidation parent', () => {
    const c = build({ listInvoices: () => of([]) });
    c['startAssemble']();
    c['setParent'](1);
    const loads = c['draftLoads']();
    expect(loads[0].isParent).toBeFalse();
    expect(loads[1].isParent).toBeTrue();
  });

  it('adds and removes charges on a load', () => {
    const c = build({ listInvoices: () => of([]) });
    c['startAssemble']();
    expect(c['draftLoads']()[0].charges.length).toBe(1);
    c['addCharge'](0);
    expect(c['draftLoads']()[0].charges.length).toBe(2);
    c['removeCharge'](0, 0);
    expect(c['draftLoads']()[0].charges.length).toBe(1);
  });

  it('assembles a new invoice via the service and selects the result', () => {
    const assemble = jasmine.createSpy('assembleInvoice').and.returnValue(of(view()));
    const c = build({ listInvoices: () => of([]), assembleInvoice: assemble });
    c['startAssemble']();
    c['draftLoads']()[0].charges[0].amount = 2000;
    c['save']();
    expect(assemble).toHaveBeenCalled();
    expect(c['showForm']()).toBeFalse();
    expect(c['selected']()?.id).toBe('inv-1');
    expect(c['saving']()).toBeFalse();
  });

  it('drops charges with a null amount from the request payload', () => {
    const assemble = jasmine.createSpy('assembleInvoice').and.returnValue(of(view()));
    const c = build({ listInvoices: () => of([]), assembleInvoice: assemble });
    c['startAssemble']();
    c['draftLoads']()[0].charges[0].amount = null;
    c['save']();
    const req = assemble.calls.mostRecent().args[0];
    expect(req.loads[0].charges.length).toBe(0);
  });

  it('blocks a save with no loads', () => {
    const assemble = jasmine.createSpy('assembleInvoice');
    const c = build({ listInvoices: () => of([]), assembleInvoice: assemble });
    c['startAssemble']();
    c['removeLoad'](0);
    c['removeLoad'](0);
    c['save']();
    expect(assemble).not.toHaveBeenCalled();
    expect(c['formError']()).toContain('At least one load');
  });

  it('shows a final-invoice conflict message on a 409 update', () => {
    const c = build({
      listInvoices: () => of([]),
      updateInvoice: () => throwError(() => ({ status: 409 })),
      getInvoice: () => of(view()),
    });
    c['select']('inv-1');
    c['startEdit']();
    c['save']();
    expect(c['formError']()).toContain('final');
    expect(c['saving']()).toBeFalse();
  });

  it('does not open the editor for a final invoice', () => {
    const c = build({ listInvoices: () => of([]), getInvoice: () => of(view({ status: 'Final' })) });
    c['select']('inv-1');
    c['startEdit']();
    expect(c['showForm']()).toBeFalse();
  });

  it('loads gated Alvys previews', () => {
    const preview = jasmine.createSpy('invoiceAlvysPreview').and.returnValue(
      of([
        {
          operationCode: 'create-customer-payment',
          title: 'Post customer payment',
          mode: 'Disabled',
          disposition: 'AuditOnly',
          executed: false,
          sandboxExecutionEligible: false,
          internalExecutionEligible: false,
          message: 'Preview only.',
          payload: null,
          validation: [],
          blockers: [],
          requiredToEnable: 'Enable writeback',
        },
      ]),
    );
    const c = build({ listInvoices: () => of([]), getInvoice: () => of(view()), invoiceAlvysPreview: preview });
    c['select']('inv-1');
    c['loadPreviews']();
    expect(preview).toHaveBeenCalled();
    expect(c['previews']()?.length).toBe(1);
    expect(c['previewLoading']()).toBeFalse();
  });

  it('tones dispositions for the preview panel', () => {
    const c = build({ listInvoices: () => of([]) });
    expect(c['dispositionTone']('AuditOnly')).toBe('muted');
    expect(c['dispositionTone']('Blocked')).toBe('bad');
    expect(c['dispositionTone']('SandboxExecuted')).toBe('ok');
    expect(c['dispositionTone']('InternalExecuted')).toBe('ok');
  });
});
