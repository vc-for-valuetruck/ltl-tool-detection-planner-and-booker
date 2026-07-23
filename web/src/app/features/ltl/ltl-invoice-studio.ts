import { CommonModule, DatePipe, DecimalPipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { LtlService } from './ltl.service';
import {
  AlvysOperationOutcome,
  AssembleInvoiceRequest,
  INVOICE_CHARGE_TYPES,
  InvoiceChargeInput,
  InvoiceChargeType,
  InvoiceLoadInput,
  InvoiceStatus,
  InvoiceSummary,
  InvoiceView,
  UpdateInvoiceRequest,
} from './invoice-studio.models';

/** An editable load row in the assemble/edit form (mutable working copy of InvoiceLoadInput). */
interface DraftLoad {
  loadNumber: string;
  isParent: boolean;
  customerName: string;
  status: string;
  alvysLoadUrl: string;
  bolPresent: boolean;
  loadedMiles: number | null;
  driverTripRate: number | null;
  charges: DraftCharge[];
}

interface DraftCharge {
  type: InvoiceChargeType;
  description: string;
  amount: number | null;
}

/**
 * Invoice Studio — the Wave 2 billing headline. Assembles a customer invoice from a consolidation
 * (parent + sibling loads), keeps per-load charges editable, computes totals + combined driver-RPM
 * live, tracks BOL presence (flagging siblings missing a BOL), downloads a professional PDF, and
 * previews the exact contracted Alvys write payloads.
 *
 * Alvys posture: read-only. Invoices persist app-side only; every Alvys payload is a gated preview
 * (AlvysWriteback = "NotPerformed"). Missing values render as "—", never fabricated.
 */
@Component({
  selector: 'app-ltl-invoice-studio',
  standalone: true,
  imports: [CommonModule, FormsModule, DatePipe, DecimalPipe],
  templateUrl: './ltl-invoice-studio.html',
  styleUrls: ['./ltl-invoice-studio.css'],
})
export class LtlInvoiceStudio implements OnInit {
  private readonly ltl = inject(LtlService);

  protected readonly chargeTypes = INVOICE_CHARGE_TYPES;

  // ---- list ------------------------------------------------------------------------------------
  protected readonly invoices = signal<InvoiceSummary[]>([]);
  protected readonly listLoading = signal(false);
  protected readonly listError = signal<string | null>(null);
  protected readonly statusFilter = signal<'' | InvoiceStatus>('');

  // ---- selected invoice ------------------------------------------------------------------------
  protected readonly selected = signal<InvoiceView | null>(null);
  protected readonly detailLoading = signal(false);
  protected readonly detailError = signal<string | null>(null);

  // ---- assemble / edit form --------------------------------------------------------------------
  protected readonly showForm = signal(false);
  protected readonly editingId = signal<string | null>(null);
  protected readonly formInvoiceNumber = signal('');
  protected readonly formCorridor = signal('');
  protected readonly formCustomerName = signal('');
  protected readonly formNotes = signal('');
  protected readonly draftLoads = signal<DraftLoad[]>([]);
  protected readonly saving = signal(false);
  protected readonly formError = signal<string | null>(null);

  // ---- Alvys preview ---------------------------------------------------------------------------
  protected readonly previews = signal<AlvysOperationOutcome[] | null>(null);
  protected readonly previewLoading = signal(false);
  protected readonly parentEtag = signal('');

  /**
   * Live invoice total across the draft form's loads/charges (mirrors the server computation).
   * A plain method, not a computed signal: charge amounts are edited via in-place mutation of the
   * draft objects, which wouldn't notify a computed's signal dependency — a method re-runs each
   * change-detection cycle, so the headline total updates as you type.
   */
  protected formTotal(): number {
    return this.draftLoads().reduce((sum, l) => sum + this.loadTotal(l), 0);
  }

  /** Draft loads whose BOL is not on file — the sibling-tracking flag billing acts on. */
  protected formMissingBol(): string[] {
    return this.draftLoads()
      .filter((l) => !l.bolPresent)
      .map((l) => l.loadNumber || '(unnamed load)');
  }

  ngOnInit(): void {
    this.refresh();
  }

  protected refresh(): void {
    this.listLoading.set(true);
    this.listError.set(null);
    const status = this.statusFilter();
    this.ltl.listInvoices(status ? { status } : {}).subscribe({
      next: (rows) => {
        this.invoices.set(rows);
        this.listLoading.set(false);
      },
      error: () => {
        this.listError.set('Could not load invoices. Please retry.');
        this.listLoading.set(false);
      },
    });
  }

  protected onStatusFilterChange(value: string): void {
    this.statusFilter.set(value as '' | InvoiceStatus);
    this.refresh();
  }

  protected select(id: string): void {
    this.detailLoading.set(true);
    this.detailError.set(null);
    this.previews.set(null);
    this.ltl.getInvoice(id).subscribe({
      next: (view) => {
        this.selected.set(view);
        this.detailLoading.set(false);
      },
      error: () => {
        this.detailError.set('Could not open the invoice.');
        this.detailLoading.set(false);
      },
    });
  }

  // ---- form control ----------------------------------------------------------------------------

  protected startAssemble(): void {
    this.editingId.set(null);
    this.formInvoiceNumber.set('');
    this.formCorridor.set('');
    this.formCustomerName.set('');
    this.formNotes.set('');
    this.draftLoads.set([this.newLoad(true), this.newLoad(false)]);
    this.formError.set(null);
    this.showForm.set(true);
  }

  /** Load the selected draft invoice into the editable form (Final invoices are read-only). */
  protected startEdit(): void {
    const inv = this.selected();
    if (!inv || inv.status === 'Final') return;
    this.editingId.set(inv.id);
    this.formInvoiceNumber.set(inv.invoiceNumber);
    this.formCorridor.set(inv.corridorCode ?? '');
    this.formCustomerName.set(inv.customerName ?? '');
    this.formNotes.set(inv.notes ?? '');
    this.draftLoads.set(
      inv.loads.map((l) => ({
        loadNumber: l.loadNumber ?? '',
        isParent: l.isParent,
        customerName: l.customerName ?? '',
        status: l.status ?? '',
        alvysLoadUrl: l.alvysLoadUrl ?? '',
        bolPresent: l.bolPresent,
        loadedMiles: l.loadedMiles ?? null,
        driverTripRate: l.driverTripRate ?? null,
        charges: l.charges.map((c) => ({
          type: c.type,
          description: c.description ?? '',
          amount: c.amount,
        })),
      })),
    );
    this.formError.set(null);
    this.showForm.set(true);
  }

  protected cancelForm(): void {
    this.showForm.set(false);
    this.formError.set(null);
  }

  private newLoad(isParent: boolean): DraftLoad {
    return {
      loadNumber: '',
      isParent,
      customerName: '',
      status: '',
      alvysLoadUrl: '',
      bolPresent: false,
      loadedMiles: null,
      driverTripRate: null,
      charges: isParent ? [{ type: 'Linehaul', description: '', amount: null }] : [],
    };
  }

  protected addLoad(): void {
    this.draftLoads.update((loads) => [...loads, this.newLoad(false)]);
  }

  protected removeLoad(index: number): void {
    this.draftLoads.update((loads) => loads.filter((_, i) => i !== index));
  }

  /** Only one load may be the consolidation parent; picking one clears the flag on the others. */
  protected setParent(index: number): void {
    this.draftLoads.update((loads) => loads.map((l, i) => ({ ...l, isParent: i === index })));
  }

  protected addCharge(loadIndex: number): void {
    this.draftLoads.update((loads) =>
      loads.map((l, i) =>
        i === loadIndex
          ? { ...l, charges: [...l.charges, { type: 'Other', description: '', amount: null }] }
          : l,
      ),
    );
  }

  protected removeCharge(loadIndex: number, chargeIndex: number): void {
    this.draftLoads.update((loads) =>
      loads.map((l, i) =>
        i === loadIndex
          ? { ...l, charges: l.charges.filter((_, c) => c !== chargeIndex) }
          : l,
      ),
    );
  }

  protected loadTotal(load: DraftLoad): number {
    return load.charges.reduce((sum, c) => sum + (c.amount ?? 0), 0);
  }

  protected save(): void {
    const loads = this.draftLoads();
    if (loads.length === 0) {
      this.formError.set('At least one load is required.');
      return;
    }
    this.saving.set(true);
    this.formError.set(null);

    const loadInputs: InvoiceLoadInput[] = loads.map((l) => ({
      loadNumber: l.loadNumber.trim() || null,
      isParent: l.isParent,
      customerName: l.customerName.trim() || null,
      status: l.status.trim() || null,
      alvysLoadUrl: l.alvysLoadUrl.trim() || null,
      bolPresent: l.bolPresent,
      loadedMiles: l.loadedMiles,
      driverTripRate: l.driverTripRate,
      charges: l.charges
        .filter((c) => c.amount !== null)
        .map<InvoiceChargeInput>((c) => ({
          type: c.type,
          description: c.description.trim() || null,
          amount: c.amount ?? 0,
        })),
    }));

    const editing = this.editingId();
    if (editing) {
      const req: UpdateInvoiceRequest = { notes: this.formNotes().trim() || null, loads: loadInputs };
      this.ltl.updateInvoice(editing, req).subscribe({
        next: (view) => this.onSaved(view),
        error: (err) =>
          this.onSaveError(
            err?.status === 409 ? 'This invoice is final and can no longer be edited.' : null,
          ),
      });
    } else {
      const req: AssembleInvoiceRequest = {
        invoiceNumber: this.formInvoiceNumber().trim() || null,
        corridorCode: this.formCorridor().trim() || null,
        customerName: this.formCustomerName().trim() || null,
        notes: this.formNotes().trim() || null,
        loads: loadInputs,
      };
      this.ltl.assembleInvoice(req).subscribe({
        next: (view) => this.onSaved(view),
        error: () => this.onSaveError(null),
      });
    }
  }

  private onSaved(view: InvoiceView): void {
    this.saving.set(false);
    this.showForm.set(false);
    this.selected.set(view);
    this.previews.set(null);
    this.refresh();
  }

  private onSaveError(message: string | null): void {
    this.saving.set(false);
    this.formError.set(message ?? 'Could not save the invoice. Please retry.');
  }

  // ---- lifecycle actions -----------------------------------------------------------------------

  protected finalize(): void {
    const inv = this.selected();
    if (!inv || inv.status === 'Final') return;
    this.ltl.finalizeInvoice(inv.id).subscribe({
      next: (view) => {
        this.selected.set(view);
        this.refresh();
      },
      error: () => this.detailError.set('Could not finalize the invoice.'),
    });
  }

  protected downloadPdf(): void {
    const inv = this.selected();
    if (!inv) return;
    this.ltl.invoicePdf(inv.id).subscribe({
      next: (blob) => {
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `invoice-${inv.invoiceNumber}.pdf`;
        a.click();
        URL.revokeObjectURL(url);
      },
      error: () => this.detailError.set('Could not generate the PDF.'),
    });
  }

  protected loadPreviews(): void {
    const inv = this.selected();
    if (!inv) return;
    this.previewLoading.set(true);
    this.ltl.invoiceAlvysPreview(inv.id, this.parentEtag().trim() || undefined).subscribe({
      next: (rows) => {
        this.previews.set(rows);
        this.previewLoading.set(false);
      },
      error: () => {
        this.detailError.set('Could not build the Alvys preview.');
        this.previewLoading.set(false);
      },
    });
  }

  /** Pretty-prints a payload body for the preview panel. */
  protected formatBody(body: Record<string, unknown>): string {
    return JSON.stringify(body, null, 2);
  }

  protected dispositionTone(disposition: string): string {
    switch (disposition) {
      case 'AuditOnly':
      case 'Simulated':
        return 'muted';
      case 'Blocked':
        return 'bad';
      case 'SandboxExecuted':
      case 'InternalExecuted':
        return 'ok';
      default:
        return 'muted';
    }
  }
}
