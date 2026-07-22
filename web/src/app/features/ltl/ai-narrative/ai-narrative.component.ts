import { Component, computed, inject, input, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { HttpResponse } from '@angular/common/http';
import { Observable, catchError, map, of, startWith, switchMap, timeout } from 'rxjs';
import { AiNarrative, AiNarrativeService } from './ai-narrative.service';

/** Render phases. `hidden` collapses the component to nothing (disabled / not-found / error). */
type NarrativePhase = 'loading' | 'ready' | 'hidden';

/**
 * `<ai-narrative>` — AI consolidation-review narrative on the plan detail card (issue #151).
 *
 * On mount (and whenever {@link planId} changes) it calls the narrative endpoint and renders three
 * labelled review rows plus a citation chip row. It is strictly non-blocking: any non-200 — 404
 * `disabled` / `plan-not-found`, 503 `ai-unavailable`, a network error, or a >2s timeout — collapses
 * the component to nothing and logs a `console.debug` diagnostic. It never emits a toast and never
 * blocks the parent plan detail from rendering.
 */
@Component({
  selector: 'ai-narrative',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './ai-narrative.component.html',
  styleUrls: ['./ai-narrative.component.css'],
})
export class AiNarrativeComponent {
  private readonly api = inject(AiNarrativeService);

  /** Server plan id whose narrative to fetch. */
  readonly planId = input.required<string>();

  private readonly phase = signal<NarrativePhase>('loading');
  readonly narrative = signal<AiNarrative | null>(null);

  /** Provenance from the 200 response headers — surfaced as a subtle footnote, never fabricated. */
  readonly source = signal<string | null>(null);
  readonly cached = signal(false);

  readonly loading = computed(() => this.phase() === 'loading');
  readonly ready = computed(() => this.phase() === 'ready' && this.narrative() !== null);

  /** Skeleton placeholder rows — three review rows to mirror the populated layout. */
  readonly skeletonRows = [0, 1, 2];

  constructor() {
    toObservable(this.planId)
      .pipe(
        switchMap((planId) => this.fetch(planId)),
        takeUntilDestroyed(),
      )
      .subscribe((update) => update());
  }

  /**
   * Fetches the narrative for one plan id. Emits a loading update immediately, then a terminal
   * update. Never errors: a 2s timeout and every HTTP/network failure are caught here and mapped to
   * the hidden phase so the stream stays alive across subsequent {@link planId} changes.
   */
  private fetch(planId: string): Observable<() => void> {
    return this.api.narrative(planId).pipe(
      timeout(2000),
      map((response: HttpResponse<AiNarrative>) => () => this.applyReady(response)),
      catchError((err) => {
        console.debug('[ai-narrative] narrative unavailable, collapsing', err);
        return of(() => this.applyHidden());
      }),
      startWith(() => this.applyLoading()),
    );
  }

  private applyLoading(): void {
    this.phase.set('loading');
    this.narrative.set(null);
    this.source.set(null);
    this.cached.set(false);
  }

  private applyReady(response: HttpResponse<AiNarrative>): void {
    const body = response.body;
    if (!body) {
      this.applyHidden();
      return;
    }
    this.narrative.set(body);
    this.source.set(response.headers.get('X-Ai-Source'));
    this.cached.set(response.headers.get('X-Ai-Cached') === 'true');
    this.phase.set('ready');
  }

  private applyHidden(): void {
    this.phase.set('hidden');
    this.narrative.set(null);
  }
}
