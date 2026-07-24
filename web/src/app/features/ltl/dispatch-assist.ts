import { CommonModule, DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import {
  DispatchAssembly,
  DispatchCandidate,
  DispatchRecommendationsResponse,
} from './dispatch-assist.models';
import { DispatchAssistService } from './dispatch-assist.service';
import { LtlService } from './ltl.service';
import { AssignmentAudit, AssignmentValidationResult } from './ltl.models';

/**
 * Dispatch Assist workbench — "inform and assemble the right driver and truck". A dispatcher enters a
 * load number (or an ad-hoc origin/destination lane) and gets a ranked, explainable list of
 * driver+truck+trailer candidates. Two distinct actions are available per row:
 *
 * <ul>
 *   <li><b>Assemble</b> — records the picked candidate as a lightweight decision log and fires the
 *   notify step (email the driver/dispatcher). No hard-rule validation runs.</li>
 *   <li><b>Assign</b> — runs the full hard-rule validation (no/terminated driver, expired credentials,
 *   over-capacity, equipment mismatch, yard-presence hold, etc.) via the load-scoped <c>/assign</c>
 *   endpoint and, when clean, records an <c>AssignmentAudit</c> row. This is the action that backs the
 *   <c>/ltl/assignments</c> history page — previously nothing in the UI called it, so that page always
 *   read empty even after dispatchers used Assemble.</li>
 * </ul>
 *
 * Read-only against Alvys: candidates come from live Alvys reads, and both actions record an internal
 * audit only (`alvysWriteback = "NotPerformed"`) — nothing is written to Alvys from here. Missing data
 * renders as "—", never fabricated.
 */
@Component({
  selector: 'app-dispatch-assist',
  standalone: true,
  imports: [CommonModule, FormsModule, DatePipe],
  templateUrl: './dispatch-assist.html',
  styleUrls: ['./dispatch-assist.css'],
})
export class DispatchAssist implements OnInit {
  private readonly svc = inject(DispatchAssistService);
  private readonly route = inject(ActivatedRoute);

  /** Query inputs. A load number resolves the target from Alvys; else the ad-hoc lane is used. */
  protected readonly loadId = signal('');
  protected readonly originCity = signal('');
  protected readonly originState = signal('');
  protected readonly destinationCity = signal('');
  protected readonly destinationState = signal('');

  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly result = signal<DispatchRecommendationsResponse | null>(null);

  /** Per-row assemble state so only the acted row shows a spinner / result. */
  protected readonly assemblingId = signal<string | null>(null);
  protected readonly lastAssembly = signal<DispatchAssembly | null>(null);
  protected readonly assembleError = signal<string | null>(null);

  /**
   * Per-row Assign state. Distinct from Assemble: Assign runs the hard-rule validation
   * (AssignmentValidationService — no driver, terminated/expired credentials, over-capacity,
   * etc.) and, when clean, records an AssignmentAudit row that feeds the /ltl/assignments
   * history page. Assemble alone never wrote to that store, so Assignments always read empty
   * even after a dispatcher picked a candidate here.
   */
  protected readonly assigningId = signal<string | null>(null);
  protected readonly lastAssignment = signal<AssignmentAudit | null>(null);
  protected readonly assignBlockers = signal<AssignmentValidationResult | null>(null);
  protected readonly assignError = signal<string | null>(null);
  private readonly ltl = inject(LtlService);

  protected readonly candidates = computed(() => this.result()?.candidates ?? []);
  protected readonly target = computed(() => this.result()?.target ?? null);

  ngOnInit(): void {
    // Deep-link support: /ltl/dispatch?loadId=123 prefills and runs the search.
    const preset = this.route.snapshot.queryParamMap.get('loadId');
    if (preset) {
      this.loadId.set(preset);
      this.search();
    }
  }

  protected canSearch(): boolean {
    return (
      this.loadId().trim().length > 0 ||
      this.originState().trim().length > 0 ||
      this.originCity().trim().length > 0
    );
  }

  protected search(): void {
    if (!this.canSearch() || this.loading()) return;
    this.loading.set(true);
    this.error.set(null);
    this.result.set(null);
    this.lastAssembly.set(null);
    this.assembleError.set(null);

    this.svc
      .recommendations({
        loadId: this.loadId().trim() || undefined,
        originCity: this.originCity().trim() || undefined,
        originState: this.originState().trim() || undefined,
        destinationCity: this.destinationCity().trim() || undefined,
        destinationState: this.destinationState().trim() || undefined,
      })
      .subscribe({
        next: (res) => {
          this.result.set(res);
          this.loading.set(false);
        },
        error: (err) => {
          this.error.set(
            err?.status === 404
              ? `Load "${this.loadId().trim()}" could not be resolved in Alvys.`
              : 'Could not load recommendations. Please retry.',
          );
          this.loading.set(false);
        },
      });
  }

  /** Records the chosen candidate app-side and fires the notify step. Never writes to Alvys. */
  protected assemble(candidate: DispatchCandidate): void {
    if (this.assemblingId()) return;
    const rowKey = this.rowKey(candidate);
    this.assemblingId.set(rowKey);
    this.assembleError.set(null);
    this.lastAssembly.set(null);

    const t = this.target();
    this.svc
      .assemble({
        loadId: t?.loadId ?? (this.loadId().trim() || null),
        loadNumber: t?.loadNumber ?? null,
        driverId: candidate.driverId,
        truckId: candidate.truckId,
        trailerId: candidate.trailerId,
        score: candidate.score,
        reasons: candidate.reasons,
      })
      .subscribe({
        next: (assembly) => {
          this.lastAssembly.set(assembly);
          this.assemblingId.set(null);
        },
        error: () => {
          this.assembleError.set('Could not record the assembly. Please retry.');
          this.assemblingId.set(null);
        },
      });
  }

  /**
   * Commits the chosen candidate as a real assignment: runs hard-rule validation and, when
   * clean, records the AssignmentAudit that backs the /ltl/assignments history page. A load
   * number is required to call the load-scoped /assign endpoint — an ad-hoc lane search with no
   * resolved load has nothing to assign against. Never writes to Alvys
   * (AssignmentAudit.alvysWriteback = "NotPerformed").
   */
  protected assign(candidate: DispatchCandidate): void {
    if (this.assigningId()) return;
    const t = this.target();
    const loadIdOrNumber = t?.loadNumber ?? t?.loadId ?? this.loadId().trim();
    if (!loadIdOrNumber) {
      this.assignError.set('Assign needs a resolved load number — search by Load # first.');
      return;
    }

    const rowKey = this.rowKey(candidate);
    this.assigningId.set(rowKey);
    this.assignError.set(null);
    this.assignBlockers.set(null);
    this.lastAssignment.set(null);

    this.ltl
      .assign(loadIdOrNumber, {
        driverId: candidate.driverId ?? undefined,
        truckId: candidate.truckId ?? undefined,
        trailerId: candidate.trailerId ?? undefined,
        matchScore: candidate.score,
        matchLabel: this.scoreBand(candidate.score),
      })
      .subscribe({
        next: (audit) => {
          this.lastAssignment.set(audit);
          this.assigningId.set(null);
        },
        error: (err) => {
          if (err?.status === 422 && err?.error) {
            this.assignBlockers.set(err.error as AssignmentValidationResult);
          } else {
            this.assignError.set('Could not record the assignment. Please retry.');
          }
          this.assigningId.set(null);
        },
      });
  }

  /** A stable-enough key per row for the per-row spinner (candidates carry no id of their own). */
  protected rowKey(candidate: DispatchCandidate): string {
    return [candidate.driverId, candidate.truckId, candidate.trailerId].join('|');
  }

  /** Excellent / Good / Possible / Review — a human band over the 0–100 score for quick scanning. */
  protected scoreBand(score: number): string {
    if (score >= 80) return 'excellent';
    if (score >= 60) return 'good';
    if (score >= 40) return 'possible';
    return 'review';
  }

  /** Whether the just-recorded assembly's notify step actually reached a mailbox. */
  protected notifyTone(state: string): string {
    switch (state) {
      case 'Sent':
        return 'ok';
      case 'Failed':
        return 'bad';
      default:
        return 'muted';
    }
  }
}
