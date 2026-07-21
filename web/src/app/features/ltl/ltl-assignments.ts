import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { LtlService } from './ltl.service';
import { AssignmentAudit, AssignmentReasonType } from './ltl.models';
import { LtlNav } from './ltl-nav';

interface ReasonOption {
  value: AssignmentReasonType | '';
  label: string;
}

/**
 * Cross-load assignment-decision history (Phase 3). A filterable audit trail over the internal,
 * non-Alvys assignment store: filter by recording user, UTC day and/or typed override reason.
 * Every row is read-only and explicitly labeled "Not pushed to Alvys" — the assignment boundary
 * records decisions locally and never writes back upstream.
 */
@Component({
  selector: 'app-ltl-assignments',
  standalone: true,
  imports: [LtlNav, FormsModule],
  templateUrl: './ltl-assignments.html',
  styleUrls: ['./ltl-worklist.css'],
})
export class LtlAssignments implements OnInit {
  private readonly ltl = inject(LtlService);

  protected readonly rows = signal<AssignmentAudit[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected readonly user = signal('');
  protected readonly day = signal('');
  protected readonly reasonType = signal<AssignmentReasonType | ''>('');

  protected readonly hasRows = computed(() => this.rows().length > 0);

  /** Human labels for the typed taxonomy; the empty value means "any reason". */
  protected readonly reasonOptions: ReasonOption[] = [
    { value: '', label: 'Any reason' },
    { value: 'Unspecified', label: 'Unspecified' },
    { value: 'CustomerRequest', label: 'Customer request' },
    { value: 'ServiceRecovery', label: 'Service recovery' },
    { value: 'CapacityConstraint', label: 'Capacity constraint' },
    { value: 'EquipmentSubstitution', label: 'Equipment substitution' },
    { value: 'DriverAvailability', label: 'Driver availability' },
    { value: 'CostOptimization', label: 'Cost optimization' },
    { value: 'ComplianceReviewed', label: 'Compliance reviewed' },
    { value: 'Other', label: 'Other' },
  ];

  private readonly reasonLabels = new Map<AssignmentReasonType, string>(
    this.reasonOptions
      .filter((o): o is { value: AssignmentReasonType; label: string } => o.value !== '')
      .map((o) => [o.value, o.label]),
  );

  ngOnInit(): void {
    this.load();
  }

  protected reasonLabel(reason: AssignmentReasonType): string {
    return this.reasonLabels.get(reason) ?? reason;
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.ltl
      .assignmentHistory({
        user: this.user().trim() || undefined,
        day: this.day() || undefined,
        reasonType: this.reasonType() || undefined,
      })
      .subscribe({
        next: (audits) => {
          this.rows.set(audits ?? []);
          this.loading.set(false);
        },
        error: (err) => {
          this.error.set(err?.error?.error ?? err?.message ?? "Couldn't load assignment history.");
          this.loading.set(false);
        },
      });
  }

  protected clearFilters(): void {
    this.user.set('');
    this.day.set('');
    this.reasonType.set('');
    this.load();
  }

  protected formatTimestamp(value: string): string {
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
  }

  /** Compact equipment summary; missing ids read honestly as an em dash, never invented. */
  protected equipment(row: AssignmentAudit): string {
    const parts = [row.driverId, row.truckId, row.trailerId].filter((p): p is string => !!p);
    return parts.length > 0 ? parts.join(' · ') : '—';
  }
}
