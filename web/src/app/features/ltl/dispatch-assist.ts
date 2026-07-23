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

/**
 * Dispatch Assist workbench — "inform and assemble the right driver and truck". A dispatcher enters a
 * load number (or an ad-hoc origin/destination lane), gets a ranked, explainable list of
 * driver+truck+trailer candidates, and one-taps **Assemble** to record the decision app-side. The
 * assemble call fires the notify step; its outcome (including the safe-override banner) is shown back.
 *
 * Read-only against Alvys: candidates come from live Alvys reads, and an assembly records an internal
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
