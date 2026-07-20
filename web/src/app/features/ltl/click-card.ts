import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { RUNTIME_CONFIG } from '../../runtime-config';

interface ConsolidationAuditResponse {
  auditId: string;
  recordedAt: string;
  recordedBy: string;
  parentLoadNumber: string;
  siblingLoadNumbers: string[];
  combinedRevenue?: number | null;
  combinedRpm?: number | null;
}

@Component({
  selector: 'app-click-card',
  standalone: true,
  imports: [CommonModule, RouterLink, DatePipe],
  templateUrl: './click-card.html',
  styleUrls: ['./click-card.css'],
})
export class ClickCard implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(RUNTIME_CONFIG).apiBaseUrl}/ltl`;

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly missingContext = signal(false);
  readonly copyMessage = signal<string | null>(null);
  readonly auditRecord = signal<ConsolidationAuditResponse | null>(null);

  readonly parentLoadNumber = signal<string | null>(null);
  readonly siblingLoadNumbers = signal<string[]>([]);
  readonly combinedRevenue = signal<number | null>(null);
  readonly combinedRpm = signal<number | null>(null);
  readonly planDetailQueryParams = signal<Record<string, string>>({});

  readonly cardText = computed(() => {
    const parent = this.parentLoadNumber();
    const siblings = this.siblingLoadNumbers();
    if (!parent || siblings.length === 0) return '';

    const siblingList = siblings.join(', ');
    return [
      'LTL CONSOLIDATION PLAN',
      `Parent load: ${parent}`,
      `Sibling loads: ${siblingList}`,
      '',
      'Operator steps (do in Alvys, after dock verification):',
      '',
      `Step 1 — On the PARENT trip (${parent}), open Trip References and set:`,
      '          • LTL = true',
      `          • Main Load Id = ${parent}`,
      '',
      `Step 2 — On EACH sibling (${siblingList}), open Trip References and set:`,
      '          • LTL = true',
      `          • Main Load Id = ${parent}`,
      '          • Loaded miles = 0  (child miles ride the parent linehaul — do not pay twice)',
      '',
      'Step 3 — Keep the parent trip as the absorbing trip. Do not delete the sibling',
      '          loads; they stay in Alvys linked to the parent for billing + audit.',
      '',
      `Projected combined revenue: ${this.formatCurrency(this.combinedRevenue())}`,
      `Projected combined RPM: ${this.formatRpm(this.combinedRpm())}`,
    ].join('\n');
  });

  ngOnInit(): void {
    const qp = this.route.snapshot.queryParamMap;
    const parent = qp.get('parent');
    const siblingsParam = qp.get('siblings');
    const combinedRevenue = this.parseNumber(qp.get('combinedRevenue'));
    const combinedRpm = this.parseNumber(qp.get('combinedRpm'));

    if (!parent || !siblingsParam) {
      this.missingContext.set(true);
      this.loading.set(false);
      return;
    }

    const siblings = siblingsParam.split(',').map((s) => s.trim()).filter(Boolean);
    this.parentLoadNumber.set(parent);
    this.siblingLoadNumbers.set(siblings);
    this.combinedRevenue.set(combinedRevenue);
    this.combinedRpm.set(combinedRpm);
    this.planDetailQueryParams.set({ parent, siblings: siblingsParam });

    this.http
      .post<ConsolidationAuditResponse>(`${this.base}/consolidation/audit`, {
        parentLoadNumber: parent,
        siblingLoadNumbers: siblings,
        combinedRevenue,
        combinedRpm,
      })
      .subscribe({
        next: (record) => {
          this.auditRecord.set(record);
          this.loading.set(false);
        },
        error: (err) => {
          this.error.set(err?.error?.error ?? err?.message ?? 'Failed to record consolidation audit.');
          this.loading.set(false);
        },
      });
  }

  async copyToClipboard(): Promise<void> {
    const text = this.cardText();
    if (!text) return;
    try {
      if (navigator?.clipboard?.writeText) {
        await navigator.clipboard.writeText(text);
        this.copyMessage.set('Copied to clipboard.');
      } else {
        this.copyMessage.set('Clipboard not available — select the card and copy manually.');
      }
    } catch {
      this.copyMessage.set('Copy failed — select the card and copy manually.');
    }
  }

  formatCurrency(value: number | null | undefined): string {
    if (value === null || value === undefined || Number.isNaN(value)) return '—';
    return `$${value.toLocaleString(undefined, { minimumFractionDigits: 0, maximumFractionDigits: 0 })}`;
  }

  formatRpm(value: number | null | undefined): string {
    if (value === null || value === undefined || Number.isNaN(value)) return '—';
    return `$${value.toFixed(2)} / mi`;
  }

  private parseNumber(value: string | null): number | null {
    if (value === null || value.trim() === '') return null;
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }
}
