import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { LtlService } from '../ltl.service';
import {
  DIRECTOR_DWELL_MS,
  DIRECTOR_SPEEDS,
  DIRECTOR_WAIT_MS,
  DemoContext,
  DirectorStep,
  DirectorStepStatus,
} from './demo-director.models';
import { LtlLoadSummary } from '../ltl.models';
import { DemoLane, DemoOriginHotspot } from './demo-director.models';
import { DEMO_DIRECTOR_SCRIPT } from './demo-director.script';
import {
  DIRECTOR_DEFAULT_VOICE,
  DIRECTOR_NARRATION_KEY,
  DIRECTOR_SPEECH_CAP_MS,
  DIRECTOR_VOICE_KEY,
  DIRECTOR_VOICE_OPTIONS,
  DemoDirectorNarrator,
  DirectorVoicePreset,
} from './demo-director.speech';

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
  private readonly ltl = inject(LtlService);

  /** The immutable script. Exposed for the overlay's act outline + tests. */
  readonly steps: readonly DirectorStep[] = DEMO_DIRECTOR_SCRIPT;

  /**
   * A real load pulled from live Alvys at the start of a run, so data-driven acts (Dispatch lane,
   * Consolidate seed) use a record that actually exists on this tenant instead of a hardcoded id /
   * lane that can 404 or return empty. Null until resolved (or when the live read is unavailable),
   * in which case those steps fall back to their static demo values.
   */
  private readonly demoContext = signal<DemoContext | null>(null);

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

  /** Voice narration of captions is enabled (persisted). Defaults ON when speech is available. */
  readonly narrationEnabled = signal<boolean>(this.loadNarrationPref());
  /** Selected narrator persona (persisted). Defaults to the Australian-female house voice. */
  readonly voicePreset = signal<DirectorVoicePreset>(this.loadVoicePref());
  /** Options for the control-bar voice picker. */
  readonly voiceOptions = DIRECTOR_VOICE_OPTIONS;
  /** Whether this platform can synthesise speech at all (drives whether the toggle is shown). */
  get narrationAvailable(): boolean {
    return this.narrator.available;
  }

  readonly total = computed(() => this.steps.length);
  readonly current = computed(() => this.steps[this.index()] ?? null);
  /** 1-based "3 / 27" counter for the caption bar. */
  readonly counter = computed(() => `${this.index() + 1} / ${this.total()}`);
  readonly speeds = DIRECTOR_SPEEDS;

  /** Monotonic guard so a late navigation/wait from a superseded step can't mutate newer state. */
  private runToken = 0;
  private advanceTimer: ReturnType<typeof setTimeout> | null = null;

  /** Web Speech API wrapper for caption narration (safe no-op when unavailable). */
  private readonly narrator = new DemoDirectorNarrator();

  constructor() {
    // Apply the persisted voice persona to the narrator before the first utterance.
    this.narrator.setPreset(this.voicePreset());
  }

  /**
   * Begins the walkthrough. `autostart` controls whether it plays immediately or waits paused on
   * step 1 for the operator to press play. Idempotent-ish: re-invoking restarts from step 0.
   */
  start(autostart: boolean): void {
    this.reset();
    this.resolveCast();
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
    this.narrator.cancel();
  }

  /** Switches the narrator persona, persists it, and re-speaks the current caption in the new voice. */
  setVoicePreset(preset: DirectorVoicePreset): void {
    this.voicePreset.set(preset);
    this.narrator.setPreset(preset);
    this.saveVoicePref(preset);
    // Re-narrate the caption already on screen so the operator hears the new voice immediately.
    if (this.narrationEnabled() && this.active() && this.status() === 'running') this.narrate();
  }

  /** Toggles caption narration, persists the choice, and stops any in-flight speech when turning off. */
  toggleNarration(): void {
    const next = !this.narrationEnabled();
    this.narrationEnabled.set(next);
    this.saveNarrationPref(next);
    if (next) {
      // Turning it on mid-step: speak the caption already on screen.
      if (this.active() && this.status() === 'running') this.narrate();
    } else {
      this.narrator.cancel();
    }
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
    this.resolveCast();
    this.active.set(true);
    this.playing.set(true);
    void this.enter(0);
  }

  /**
   * Assembles the run's live "cast" at start: sweeps the app's own authenticated open-loads feed
   * (through {@link LtlService} — the same bearer-token path as every page), groups the loads by
   * lane, picks the busiest lanes, and chooses a real anchor load on the busiest lane. The
   * data-driven acts (Consolidate seed, Dispatch Assist lane) then drive a record that actually
   * exists on this tenant, and the money-first narration reports live figures ("432 loads moving
   * right now; your busiest lane has 10 waiting to be combined").
   *
   * Fire-and-forget and never fabricates: on any failure or empty result the cast stays null and
   * the affected steps fall back to their static demo values or skip-with-caption. The walkthrough
   * never blocks on it. Numbers are derived here at runtime — never baked into the script.
   */
  private resolveCast(): void {
    this.demoContext.set(null);
    try {
      // A bounded LTL-only sweep, revenue-first so the anchor leads with a meaningful load.
      this.ltl
        .search({ pageSize: 100, ltlOnly: true, sort: 'Revenue', sortDescending: true })
        .subscribe({
          next: (res) => {
            const items = res?.items ?? [];
            if (items.length === 0) return;
            const topLanes = this.rankLanes(items);
            const anchor = this.pickAnchor(items, topLanes);
            if (!anchor) return;
            const busiest = topLanes[0] ?? null;
            this.demoContext.set({
              loadNumber: anchor.loadNumber ?? null,
              loadId: anchor.id ?? null,
              originCity: anchor.origin?.city ?? null,
              originState: anchor.origin?.state ?? null,
              destinationCity: anchor.destination?.city ?? null,
              destinationState: anchor.destination?.state ?? null,
              laneLabel: this.laneLabel(anchor),
              laneOpenCount: this.laneCount(anchor, topLanes),
              totalOpen: res?.total ?? items.length,
              topLanes,
              customerName: anchor.customerName ?? null,
              originHotspots: this.rankOriginHotspots(items),
              anchorCandidates: this.pickAnchorCandidates(items, anchor),
            });
          },
          error: () => {
            // Leave the cast null — data-driven steps fall back to their static demo values.
          },
        });
    } catch {
      // Injection/observable construction should never break the walkthrough.
    }
  }

  /** Stable lane key for grouping: origin city+state → destination city+state, upper-cased. */
  private laneKey(l: LtlLoadSummary): string | null {
    const oc = l.origin?.city?.trim();
    const os = l.origin?.state?.trim();
    const dc = l.destination?.city?.trim();
    const ds = l.destination?.state?.trim();
    if (!oc || !os || !dc || !ds) return null;
    return `${oc}|${os}|${dc}|${ds}`.toUpperCase();
  }

  /** Groups loads by lane and returns the busiest lanes, most open freight first. */
  private rankLanes(items: LtlLoadSummary[]): DemoLane[] {
    const groups = new Map<string, { sample: LtlLoadSummary; count: number }>();
    for (const l of items) {
      const key = this.laneKey(l);
      if (!key) continue;
      const g = groups.get(key);
      if (g) g.count++;
      else groups.set(key, { sample: l, count: 1 });
    }
    return [...groups.values()]
      .map(({ sample, count }) => ({
        label: this.laneLabel(sample) ?? '—',
        originCity: sample.origin?.city ?? null,
        originState: sample.origin?.state ?? null,
        destinationCity: sample.destination?.city ?? null,
        destinationState: sample.destination?.state ?? null,
        openLoadCount: count,
      }))
      .sort((a, b) => b.openLoadCount - a.openLoadCount);
  }

  /**
   * Picks the anchor load: prefer the first complete-lane load on the busiest lane (so Consolidate
   * and Dispatch drive the highest-volume corridor), then any complete-lane load, then any load
   * with a number. Never invents one.
   */
  private pickAnchor(items: LtlLoadSummary[], topLanes: DemoLane[]): LtlLoadSummary | null {
    const busiestKey = topLanes.length > 0
      ? `${topLanes[0].originCity}|${topLanes[0].originState}|${topLanes[0].destinationCity}|${topLanes[0].destinationState}`.toUpperCase()
      : null;
    return (
      (busiestKey
        ? items.find((l) => this.laneKey(l) === busiestKey && l.loadNumber)
        : undefined) ??
      items.find((l) => this.laneKey(l) && l.loadNumber) ??
      items.find((l) => l.loadNumber) ??
      items[0] ??
      null
    );
  }

  private laneLabel(l: LtlLoadSummary): string | null {
    const o = l.origin, d = l.destination;
    if (!o?.city || !o?.state || !d?.city || !d?.state) return null;
    return `${o.city}, ${o.state} → ${d.city}, ${d.state}`;
  }

  /** Groups the live loads by origin city+state and returns the busiest origins, most freight first. */
  private rankOriginHotspots(items: LtlLoadSummary[]): DemoOriginHotspot[] {
    const groups = new Map<string, { city: string; state: string; count: number }>();
    for (const l of items) {
      const city = l.origin?.city?.trim();
      const state = l.origin?.state?.trim();
      if (!city || !state) continue;
      const key = `${city}|${state}`.toUpperCase();
      const g = groups.get(key);
      if (g) g.count++;
      else groups.set(key, { city, state, count: 1 });
    }
    return [...groups.values()].sort((a, b) => b.count - a.count);
  }

  /**
   * Builds the ordered list of real load numbers the Consolidate seedFind will try, best-first: loads
   * whose origin sits in the pilot corridor's home state (TX / Laredo side) lead — those are the ones
   * that actually return corridor siblings — followed by the rest in revenue order. Distinct, capped,
   * anchor guaranteed first. Never invents a number.
   */
  private pickAnchorCandidates(items: LtlLoadSummary[], anchor: LtlLoadSummary): string[] {
    const withNumber = items.filter((l) => l.loadNumber);
    const rank = (l: LtlLoadSummary): number => {
      if (l.id && anchor.id && l.id === anchor.id) return -1; // anchor always first
      return l.origin?.state?.trim().toUpperCase() === 'TX' ? 0 : 1;
    };
    const ordered = [...withNumber].sort((a, b) => rank(a) - rank(b));
    const seen = new Set<string>();
    const out: string[] = [];
    for (const l of ordered) {
      const n = l.loadNumber!;
      if (seen.has(n)) continue;
      seen.add(n);
      out.push(n);
      if (out.length >= 6) break;
    }
    return out;
  }

  private laneCount(anchor: LtlLoadSummary, topLanes: DemoLane[]): number | null {
    const key = this.laneKey(anchor);
    if (!key) return null;
    const lane = topLanes.find(
      (t) => `${t.originCity}|${t.originState}|${t.destinationCity}|${t.destinationState}`.toUpperCase() === key,
    );
    return lane?.openLoadCount ?? null;
  }

  exit(): void {
    this.clearAdvance();
    this.narrator.cancel();
    this.runToken++;
    this.active.set(false);
    this.playing.set(false);
    this.finished.set(false);
    this.spotlight.set(null);
  }

  private reset(): void {
    this.clearAdvance();
    this.narrator.cancel();
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

    // Let the step re-resolve its caption/target from what actually rendered + the live cast
    // (data-driven acts: inject live figures, or degrade to an honest empty-state caption).
    const ctx = this.demoContext();
    const resolvedCaption = step.resolveCaption?.(ctx) ?? null;
    if (resolvedCaption) this.caption.set(resolvedCaption);

    if (step.action) {
      // A resolved-caption skip (e.g. Auto ejected, empty candidates) means "don't interact".
      if (!resolvedCaption || !this.captionSignalsSkip(resolvedCaption)) {
        await this.performAction(step, token);
        if (this.isStale(token)) return;
      }
    }

    const resolvedTarget = step.resolveTarget?.(ctx) ?? null;
    this.spotlight.set(resolvedTarget ?? step.target ?? null);
    this.status.set('running');

    // Begin narrating the (possibly re-resolved) caption now that it's on screen.
    this.narrate();

    if (this.playing()) this.armAdvance();
  }

  /** True when a data-driven resolved caption is describing an empty/skip state. */
  private captionSignalsSkip(caption: string): boolean {
    return /no |empty|ejected|skipping/i.test(caption);
  }

  private skip(step: DirectorStep, token: number): void {
    if (this.isStale(token)) return;
    const resolved = step.resolveCaption?.(this.demoContext()) ?? null;
    this.caption.set(
      resolved ??
        'Precondition not present on live data right now — skipping this step (nothing was faked).',
    );
    this.spotlight.set(null);
    this.status.set('skipped');
    this.note.set('Skipped — live data for this step is not available at the moment.');
    this.narrate();
    if (this.playing()) this.armAdvance();
  }

  /**
   * Schedules the auto-advance to the next step. The step's (speed-scaled) dwell is treated as a
   * *minimum* on-screen time; when a caption is being narrated we also wait for the utterance to
   * finish so a timed step never advances mid-sentence. A hard {@link DIRECTOR_SPEECH_CAP_MS} cap
   * guarantees a hung speech engine can never stall the run.
   */
  private armAdvance(): void {
    this.clearAdvance();
    const step = this.current();
    const base = step?.dwellMs ?? DIRECTOR_DWELL_MS;
    const dwell = Math.max(600, Math.round(base / this.speed()));
    const token = this.runToken;
    if (typeof setTimeout !== 'function') return;

    const startedAt = Date.now();
    const poll = () => {
      if (this.isStale(token) || !this.playing()) return;
      const elapsed = Date.now() - startedAt;
      const dwellDone = elapsed >= dwell;
      const speechDone = !this.narrator.speaking;
      if ((dwellDone && speechDone) || elapsed >= DIRECTOR_SPEECH_CAP_MS) {
        this.next();
        return;
      }
      // Re-check on the shorter of "remaining dwell" or a light speech-poll cadence.
      const wait = dwellDone ? 200 : Math.min(200, Math.max(50, dwell - elapsed));
      this.advanceTimer = setTimeout(poll, wait);
    };
    this.advanceTimer = setTimeout(poll, Math.min(200, dwell));
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
    const ctx = this.demoContext();
    if (kind === 'fillMany') {
      const fields = (ctx && step.resolveFields?.(ctx)) || step.fields || [];
      for (const f of fields) this.fill(f.selector, f.value);
      return;
    }
    if (kind === 'clickRetry') {
      await this.performClickRetry(step, token);
      return;
    }
    if (kind === 'seedFind') {
      await this.performSeedFind(step, ctx, token);
      return;
    }
    const selector = step.resolveActionSelector?.(ctx) ?? step.actionSelector ?? step.target;
    if (!selector) return;
    if (kind === 'fill') {
      const value = (ctx && step.resolveFillValue?.(ctx)) ?? step.fillValue ?? '';
      this.fill(selector, value);
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

  /**
   * Clicks candidate cards one at a time until a success selector appears — used so the Dock lands a
   * parent load that actually yields combinable siblings rather than the first (possibly dead) card.
   * Resets between tries when a reset selector is given, and bails the moment the run is superseded.
   */
  private async performClickRetry(step: DirectorStep, token: number): Promise<void> {
    const cfg = step.retry;
    if (!cfg) return;
    const initial = this.visibleAll(cfg.candidateSelector).length;
    if (initial === 0) return;
    const attempts = Math.max(1, Math.min(cfg.maxAttempts ?? initial, initial));
    for (let i = 0; i < attempts; i++) {
      if (this.isStale(token)) return;
      const cards = this.visibleAll(cfg.candidateSelector);
      const card = cards[i];
      if (!card) return;
      card.click();
      await this.delay(150);
      const found = await this.waitForAny([cfg.successSelector], 8_000, token);
      if (this.isStale(token)) return;
      if (found) return;
      // No usable result off this candidate — reset and try the next one (unless we're out of tries).
      if (cfg.resetSelector && i < attempts - 1) {
        (document.querySelector(cfg.resetSelector) as HTMLElement | null)?.click();
        await this.delay(150);
        await this.waitForAny([cfg.candidateSelector], 8_000, token);
      }
    }
  }

  /**
   * Types each real anchor load number into the seed input and clicks Find until candidate rows
   * render — used so the Consolidate board drives a live anchor instead of an empty pinned corridor.
   * Tries a second anchor before giving up; honest empty state (no rows) is left for the next step.
   */
  private async performSeedFind(
    step: DirectorStep,
    ctx: DemoContext | null,
    token: number,
  ): Promise<void> {
    const cfg = step.seedFind;
    if (!cfg) return;
    const anchors = (ctx?.anchorCandidates ?? []).filter((a) => a && a.trim().length > 0);
    if (anchors.length === 0) return;
    const attempts = Math.max(1, Math.min(cfg.maxAttempts ?? anchors.length, anchors.length));
    for (let i = 0; i < attempts; i++) {
      if (this.isStale(token)) return;
      this.fill(cfg.seedSelector, anchors[i]);
      await this.delay(80);
      (document.querySelector(cfg.findSelector) as HTMLElement | null)?.click();
      const found = await this.waitForAny([cfg.rowSelector], 8_000, token);
      if (this.isStale(token)) return;
      if (found) return;
    }
  }

  /** All present-and-visible elements matching `selector`, in document order. */
  private visibleAll(selector: string): HTMLElement[] {
    if (typeof document === 'undefined') return [];
    const els = Array.from(document.querySelectorAll<HTMLElement>(selector));
    return els.filter((el) => el.offsetParent !== null || el.getClientRects().length > 0);
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

  /**
   * Speaks the current step's narration when narration is enabled (safe no-op otherwise). Both the
   * caption AND the honest-state posture are spoken, in that order and sequentially, so nothing on
   * screen is left silent — the operator hears the "recorded internally only / writeback gated"
   * disclaimer read aloud, not just shown.
   */
  private narrate(): void {
    if (!this.narrationEnabled()) {
      this.narrator.cancel();
      return;
    }
    const parts = [this.caption(), this.posture()].filter(
      (t): t is string => !!t && t.trim().length > 0,
    );
    this.narrator.speak(parts);
  }

  /** Reads the persisted narration preference; defaults ON (only meaningful when speech exists). */
  private loadNarrationPref(): boolean {
    try {
      if (typeof localStorage === 'undefined') return true;
      const raw = localStorage.getItem(DIRECTOR_NARRATION_KEY);
      return raw === null ? true : raw === 'true';
    } catch {
      return true;
    }
  }

  private saveNarrationPref(value: boolean): void {
    try {
      localStorage?.setItem(DIRECTOR_NARRATION_KEY, String(value));
    } catch {
      // Private-mode / disabled storage — preference just won't persist across reloads.
    }
  }

  /** Reads the persisted voice persona; defaults to the Australian-female house voice. */
  private loadVoicePref(): DirectorVoicePreset {
    try {
      if (typeof localStorage === 'undefined') return DIRECTOR_DEFAULT_VOICE;
      const raw = localStorage.getItem(DIRECTOR_VOICE_KEY);
      return raw === 'narrator' || raw === 'auFemale' || raw === 'system'
        ? raw
        : DIRECTOR_DEFAULT_VOICE;
    } catch {
      return DIRECTOR_DEFAULT_VOICE;
    }
  }

  private saveVoicePref(value: DirectorVoicePreset): void {
    try {
      localStorage?.setItem(DIRECTOR_VOICE_KEY, value);
    } catch {
      // Private-mode / disabled storage — preference just won't persist across reloads.
    }
  }
}
