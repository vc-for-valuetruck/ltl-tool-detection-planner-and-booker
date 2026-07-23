import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import {
  DIRECTOR_DWELL_MS,
  DIRECTOR_SPEEDS,
  DIRECTOR_WAIT_MS,
  DirectorStep,
  DirectorStepStatus,
} from './demo-director.models';
import { DEMO_DIRECTOR_SCRIPT } from './demo-director.script';

/**
 * Drives the in-app, URL-triggered autonomous walkthrough (`/ltl/demo/director`).
 *
 * A singleton that programmatically navigates the real LTL workspace and interacts with the
 * real feature components against LIVE Alvys reads. It owns playback state (active / playing /
 * index / speed), the caption + spotlight the {@link DemoDirectorOverlay} renders, and a
 * token-guarded async runner that walks each {@link DirectorStep}: navigate → wait for the
 * live UI to be ready (retrying through Alvys latency) → optionally interact → spotlight →
 * dwell → advance.
 *
 * Honesty guarantees (mirrors CLAUDE.md safety principles):
 *  - No mock APIs, no synthetic data — the director only reads what the live components render.
 *  - Never fakes success: a gated write surfaces its {@link DirectorStep.posture} caption while
 *    the real (audit-only / no-op) flow runs; nothing is asserted to have reached Alvys.
 *  - Resilient: a missing precondition on an {@link DirectorStep.optional} step is skipped with an
 *    honest caption instead of stalling.
 */
@Injectable({ providedIn: 'root' })
export class DemoDirectorService {
  private readonly router = inject(Router);

  /** The immutable script. Exposed for the overlay's act outline + tests. */
  readonly steps: readonly DirectorStep[] = DEMO_DIRECTOR_SCRIPT;

  /** Overlay is mounted+visible. */
  readonly active = signal(false);
  /** Auto-advancing (vs paused on the current step). */
  readonly playing = signal(false);
  /** Current step index into {@link steps}. */
  readonly index = signal(0);
  /** Playback speed multiplier (scales dwell only, never the live-data wait budget). */
  readonly speed = signal(1);
  /** Status of the step currently on screen. */
  readonly status = signal<DirectorStepStatus>('idle');
  /** Transient live note (e.g. "Waiting on live Alvys data…", or a skip reason). */
  readonly note = signal<string | null>(null);
  /** Caption resolved for the current step (may be overridden by the step's resolveCaption). */
  readonly caption = signal<string>('');
  /** CSS selector the overlay should spotlight, or null for a full-workspace highlight. */
  readonly spotlight = signal<string | null>(null);
  /** Honest-state posture note for the current step, or null. */
  readonly posture = signal<string | null>(null);
  /** True once the final step has been presented. */
  readonly finished = signal(false);

  readonly total = computed(() => this.steps.length);
  readonly current = computed(() => this.steps[this.index()] ?? null);
  /** 1-based "3 / 27" counter for the caption bar. */
  readonly counter = computed(() => `${this.index() + 1} / ${this.total()}`);
  readonly speeds = DIRECTOR_SPEEDS;

  /** Monotonic guard so a late navigation/wait from a superseded step can't mutate newer state. */
  private runToken = 0;
  private advanceTimer: ReturnType<typeof setTimeout> | null = null;

  /**
   * Begins the walkthrough. `autostart` controls whether it plays immediately or waits paused on
   * step 1 for the operator to press play. Idempotent-ish: re-invoking restarts from step 0.
   */
  start(autostart: boolean): void {
    this.reset();
    this.active.set(true);
    this.playing.set(autostart);
    void this.enter(0);
  }

  /** Sets speed from a `?speed=` query param or the overlay control; clamps to the allowed set. */
  setSpeed(value: number): void {
    const allowed = DIRECTOR_SPEEDS as readonly number[];
    this.speed.set(allowed.includes(value) ? value : 1);
    // Re-arm the auto-advance so the new speed takes effect immediately while playing.
    if (this.playing() && this.status() === 'running') this.armAdvance();
  }

  play(): void {
    if (this.finished()) {
      this.replay();
      return;
    }
    this.playing.set(true);
    // Resume: if the current step already settled, just re-arm the timer; otherwise re-run it.
    if (this.status() === 'running' || this.status() === 'skipped') this.armAdvance();
    else void this.enter(this.index());
  }

  pause(): void {
    this.playing.set(false);
    this.clearAdvance();
  }

  next(): void {
    if (this.index() >= this.total() - 1) {
      this.finish();
      return;
    }
    this.index.update((i) => i + 1);
    void this.enter(this.index());
  }

  prev(): void {
    if (this.index() <= 0) return;
    this.finished.set(false);
    this.index.update((i) => i - 1);
    void this.enter(this.index());
  }

  replay(): void {
    this.reset();
    this.active.set(true);
    this.playing.set(true);
    void this.enter(0);
  }

  exit(): void {
    this.clearAdvance();
    this.runToken++;
    this.active.set(false);
    this.playing.set(false);
    this.finished.set(false);
    this.spotlight.set(null);
  }

  private reset(): void {
    this.clearAdvance();
    this.runToken++;
    this.index.set(0);
    this.finished.set(false);
    this.note.set(null);
    this.posture.set(null);
    this.spotlight.set(null);
    this.status.set('idle');
  }

  /**
   * Presents step `i`: navigate, wait for the live UI, interact, spotlight, then (if playing)
   * arm the auto-advance. Guarded by {@link runToken} so a superseded run can't write stale state.
   */
  private async enter(i: number): Promise<void> {
    const token = ++this.runToken;
    this.clearAdvance();
    const step = this.steps[i];
    if (!step) return;

    this.note.set(null);
    this.posture.set(step.posture ?? null);
    this.caption.set(step.caption);
    this.status.set('navigating');

    if (step.route && !this.router.url.split('?')[0].startsWith(step.route)) {
      this.spotlight.set(null);
      await this.navigate(step.route);
      if (this.isStale(token)) return;
    }

    if (step.waitFor?.length) {
      this.status.set('waiting');
      this.note.set('Waiting on live data…');
      const found = await this.waitForAny(step.waitFor, step.waitMs ?? DIRECTOR_WAIT_MS, token);
      if (this.isStale(token)) return;
      if (!found) {
        if (step.optional) {
          this.skip(step, token);
          return;
        }
        this.note.set('Still waiting on live data — the live read is slow but not failed.');
      } else {
        this.note.set(null);
      }
    }

    // Let the step re-resolve its caption/target from what actually rendered (data-driven acts).
    const resolvedCaption = step.resolveCaption?.() ?? null;
    if (resolvedCaption) this.caption.set(resolvedCaption);

    if (step.action) {
      // A resolved-caption skip (e.g. Auto ejected, empty candidates) means "don't interact".
      if (!resolvedCaption || !this.captionSignalsSkip(resolvedCaption)) {
        await this.performAction(step, token);
        if (this.isStale(token)) return;
      }
    }

    const resolvedTarget = step.resolveTarget?.() ?? null;
    this.spotlight.set(resolvedTarget ?? step.target ?? null);
    this.status.set('running');

    if (this.playing()) this.armAdvance();
  }

  /** True when a data-driven resolved caption is describing an empty/skip state. */
  private captionSignalsSkip(caption: string): boolean {
    return /no |empty|ejected|skipping/i.test(caption);
  }

  private skip(step: DirectorStep, token: number): void {
    if (this.isStale(token)) return;
    const resolved = step.resolveCaption?.() ?? null;
    this.caption.set(
      resolved ??
        'Precondition not present on live data right now — skipping this step (nothing was faked).',
    );
    this.spotlight.set(null);
    this.status.set('skipped');
    this.note.set('Skipped — live data for this step is not available at the moment.');
    if (this.playing()) this.armAdvance();
  }

  /** Schedules the auto-advance to the next step, scaling dwell by the current speed. */
  private armAdvance(): void {
    this.clearAdvance();
    const step = this.current();
    const base = step?.dwellMs ?? DIRECTOR_DWELL_MS;
    const dwell = Math.max(600, Math.round(base / this.speed()));
    const token = this.runToken;
    if (typeof setTimeout !== 'function') return;
    this.advanceTimer = setTimeout(() => {
      if (this.isStale(token) || !this.playing()) return;
      this.next();
    }, dwell);
  }

  private clearAdvance(): void {
    if (this.advanceTimer !== null) {
      clearTimeout(this.advanceTimer);
      this.advanceTimer = null;
    }
  }

  private finish(): void {
    this.clearAdvance();
    this.playing.set(false);
    this.finished.set(true);
    this.status.set('done');
    this.spotlight.set(null);
    this.note.set(null);
  }

  private isStale(token: number): boolean {
    return token !== this.runToken || !this.active();
  }

  /** Router navigation as a promise; swallows a rejected/cancelled navigation so the tour continues. */
  private async navigate(route: string): Promise<void> {
    try {
      await this.router.navigateByUrl(route);
    } catch {
      // A guard cancel / parallel navigation shouldn't crash the walkthrough.
    }
  }

  /**
   * Polls for the first of `selectors` to be present AND visible, up to `timeoutMs`. Returns the
   * matched selector or null on timeout. Poll-based (not MutationObserver) to survive Angular's
   * async change detection + live HTTP latency without racing a single observation.
   */
  private async waitForAny(
    selectors: readonly string[],
    timeoutMs: number,
    token: number,
  ): Promise<string | null> {
    if (typeof document === 'undefined') return null;
    const deadline = Date.now() + timeoutMs;
    // eslint-disable-next-line no-constant-condition
    while (true) {
      if (this.isStale(token)) return null;
      for (const sel of selectors) {
        if (this.isVisible(sel)) return sel;
      }
      if (Date.now() >= deadline) return null;
      await this.delay(150);
    }
  }

  private isVisible(selector: string): boolean {
    const el = document.querySelector(selector) as HTMLElement | null;
    if (!el) return false;
    // offsetParent is null for display:none; getClientRects covers position:fixed elements too.
    return el.offsetParent !== null || el.getClientRects().length > 0;
  }

  private async performAction(step: DirectorStep, token: number): Promise<void> {
    if (typeof document === 'undefined') return;
    const kind = step.action;
    if (kind === 'fillMany') {
      for (const f of step.fields ?? []) this.fill(f.selector, f.value);
      return;
    }
    const selector = step.actionSelector ?? step.target;
    if (!selector) return;
    if (kind === 'fill') {
      this.fill(selector, step.fillValue ?? '');
      return;
    }
    if (kind === 'check') {
      const el = document.querySelector(selector) as HTMLInputElement | null;
      if (el && !el.checked) {
        el.checked = true;
        el.dispatchEvent(new Event('change', { bubbles: true }));
      }
      return;
    }
    if (kind === 'click') {
      const el = document.querySelector(selector) as HTMLElement | null;
      el?.click();
      // Give Angular a beat to react to the click before the step spotlights/advances.
      await this.delay(120);
      void token;
    }
  }

  /** Sets an input's value and fires input+change so Angular's ngModel/forms pick it up. */
  private fill(selector: string, value: string): void {
    const el = document.querySelector(selector) as HTMLInputElement | null;
    if (!el) return;
    el.value = value;
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
  }

  private delay(ms: number): Promise<void> {
    return new Promise((resolve) => {
      if (typeof setTimeout !== 'function') resolve();
      else setTimeout(resolve, ms);
    });
  }
}
