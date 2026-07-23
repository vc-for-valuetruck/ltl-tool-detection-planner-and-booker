/**
 * Declarative script model for the in-app Demo Director (issue: feat/demo-director).
 *
 * The director is a *driver*, not a feature: it navigates the real LTL workspace and
 * interacts with the real, already-shipped components (Dock, Consolidate, Dispatch Assist,
 * back-office tabs) against LIVE Alvys reads. It never mocks an API, never fabricates data,
 * and never fakes a write. A step that depends on a gated write says so in its {@link posture}
 * caption while still showing the real payload/flow.
 *
 * Each step is pure data + optional DOM-inspecting callbacks. All selectors are the stable
 * `data-testid` / component-selector hooks the feature components already expose — the
 * director does NOT modify feature code to drive it.
 */

/** A single UI action the director performs on the live page. */
export type DirectorActionKind = 'click' | 'fill' | 'fillMany' | 'check';

export interface DirectorFill {
  readonly selector: string;
  readonly value: string;
}

/**
 * A real record pulled from LIVE Alvys reads at the start of a run, used so acts drive live data
 * instead of hardcoded demo ids/lanes (a fixed load number or lane can return an empty/404 against
 * a live tenant). Any field may be null when the live read is unavailable, in which case a step
 * falls back to its static demo value. This is a read of what the app already fetches — no new data
 * source, no fabrication.
 */
export interface DemoContext {
  readonly loadNumber: string | null;
  readonly loadId: string | null;
  readonly originCity: string | null;
  readonly originState: string | null;
  readonly destinationCity: string | null;
  readonly destinationState: string | null;
}

export interface DirectorStep {
  /** Stable id (used in tests + as the overlay step key). */
  readonly id: string;
  /** Act this step belongs to (grouping label shown in the caption bar). */
  readonly act: string;
  /** Plain-business-language narration shown in the caption bar. */
  readonly caption: string;
  /**
   * Optional DOM-inspecting override run after {@link waitFor} settles. Lets an act degrade
   * gracefully / stay data-driven (e.g. "Invoice Studio present" vs "using billing worklist").
   */
  readonly resolveCaption?: () => string | null;
  /** Honest-state note for gated/no-op writes ("payload prepared, execution gated pending sign-off"). */
  readonly posture?: string;
  /** Route to navigate to before the step runs (skipped when already there). */
  readonly route?: string;
  /** CSS selector to spotlight. When absent the whole workspace is highlighted. */
  readonly target?: string;
  /** Optional DOM-inspecting override for the spotlight target. */
  readonly resolveTarget?: () => string | null;
  /**
   * Selectors to wait for (ANY match) before the step is considered ready. Covers live-data
   * latency; on timeout an {@link optional} step is skipped-with-caption, a required one proceeds
   * with an honest "still waiting" note.
   */
  readonly waitFor?: readonly string[];
  /** Per-step wait budget override (ms). Defaults to {@link DIRECTOR_WAIT_MS}. */
  readonly waitMs?: number;
  /** Interaction to perform once ready. */
  readonly action?: DirectorActionKind;
  /** Target of the action; defaults to {@link target} when omitted. */
  readonly actionSelector?: string;
  /** Value for a `fill` action. */
  readonly fillValue?: string;
  /**
   * Live-data override for a `fill` action's value. Given the run's {@link DemoContext}, returns the
   * value to type (e.g. a real load number), or null to fall back to {@link fillValue}.
   */
  readonly resolveFillValue?: (ctx: DemoContext) => string | null;
  /** Fields for a `fillMany` action. */
  readonly fields?: readonly DirectorFill[];
  /**
   * Live-data override for a `fillMany` action's fields. Given the run's {@link DemoContext}, returns
   * the fields to type (e.g. a real origin/destination lane), or null to fall back to {@link fields}.
   */
  readonly resolveFields?: (ctx: DemoContext) => readonly DirectorFill[] | null;
  /** Base dwell before auto-advance (ms), scaled by 1/speed. Defaults to {@link DIRECTOR_DWELL_MS}. */
  readonly dwellMs?: number;
  /** When true, a missing precondition skips this step (with an honest caption) instead of stalling. */
  readonly optional?: boolean;
}

/** Lifecycle status of the step currently being presented. */
export type DirectorStepStatus = 'idle' | 'navigating' | 'waiting' | 'running' | 'skipped' | 'done';

/** Default wait budget for a step's readiness selectors — generous for live Alvys latency. */
export const DIRECTOR_WAIT_MS = 12_000;
/** Default dwell on a step before auto-advancing (at 1x speed). */
export const DIRECTOR_DWELL_MS = 4_000;
/** Selectable playback speeds. */
export const DIRECTOR_SPEEDS = [0.5, 1, 1.5, 2, 3] as const;
