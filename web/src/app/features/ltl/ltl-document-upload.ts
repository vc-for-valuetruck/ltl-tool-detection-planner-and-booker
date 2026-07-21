import { Component, Input, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { AlvysOpsService } from './alvys-ops.service';
import {
  ALVYS_LOAD_DOCUMENT_TYPES,
  AlvysOperationRecordView,
  AlvysReadinessStatus,
} from './alvys-ops.models';

/**
 * Upload-document action for the Missing POD billing flow (Scope A). Lets a dispatcher attach a load
 * document (POD, BOL, etc.) through the sandbox-gated Public-API multipart endpoint
 * (`POST /api/alvys/ops/upload-load-document`).
 *
 * <p>Honesty is the whole point: the file bytes never enter JSON and are never persisted client-side;
 * the writeback posture is shown up-front (whether an upload will be pushed to the Alvys sandbox or
 * recorded internally only); and on success the component renders exactly what Alvys returned — the
 * attachment path, the upload time, and the post-write reconciliation state — never fabricated
 * metadata. A reconciliation Mismatch is surfaced for human review, never hidden.</p>
 */
@Component({
  selector: 'app-ltl-document-upload',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './ltl-document-upload.html',
  styleUrls: ['./ltl-document-upload.css'],
})
export class LtlDocumentUpload implements OnInit {
  private readonly ops = inject(AlvysOpsService);

  /** Target load number for the upload. Required. */
  @Input({ required: true }) loadNumber = '';

  protected readonly documentTypes = ALVYS_LOAD_DOCUMENT_TYPES;
  protected readonly selectedType = signal<string>('Proof of Delivery');
  protected readonly selectedFile = signal<File | null>(null);
  protected readonly reason = signal<string>('');

  protected readonly status = signal<AlvysReadinessStatus | null>(null);
  protected readonly submitting = signal(false);
  protected readonly result = signal<AlvysOperationRecordView | null>(null);
  protected readonly error = signal<string | null>(null);

  /** True once Alvys sandbox execution is fully configured — otherwise an upload is recorded internally only. */
  protected readonly willPush = computed(() => this.status()?.sandboxExecutionConfigured === true);

  protected readonly canSubmit = computed(
    () => !this.submitting() && this.selectedFile() !== null && this.selectedType().length > 0,
  );

  ngOnInit(): void {
    // Posture is advisory here; a failure just means we render the conservative "recorded internally" copy.
    this.ops.status().subscribe({
      next: (s) => this.status.set(s),
      error: () => this.status.set(null),
    });
  }

  protected onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.selectedFile.set(input.files?.[0] ?? null);
    // A new file invalidates any prior result so stale attachment metadata is never shown.
    this.result.set(null);
    this.error.set(null);
  }

  protected submit(): void {
    const file = this.selectedFile();
    if (!file || !this.loadNumber) return;

    this.submitting.set(true);
    this.error.set(null);
    this.result.set(null);

    const reason = this.reason().trim() || undefined;
    this.ops.uploadLoadDocument(this.loadNumber, this.selectedType(), file, reason).subscribe({
      next: (res) => {
        this.result.set(res.record ?? null);
        this.submitting.set(false);
      },
      error: (err) => {
        this.error.set(err?.error?.error ?? err?.error ?? err?.message ?? 'Upload failed.');
        this.submitting.set(false);
      },
    });
  }

  /** Did the upload actually reach the Alvys sandbox, or was it recorded internally only? */
  protected pushed(record: AlvysOperationRecordView): boolean {
    return record.disposition === 'SandboxExecuted';
  }

  protected reconciliationClass(state: string): string {
    switch (state) {
      case 'Confirmed':
        return 'pill pill-ok';
      case 'Mismatch':
        return 'pill pill-danger';
      case 'Pending':
        return 'pill pill-warn';
      default:
        return 'pill pill-muted';
    }
  }
}
