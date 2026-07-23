import { DirectorFill, DirectorStep } from './demo-director.models';

/**
 * The scripted walkthrough, in order. Acts A–E of the Search → Match → Assign → Bill story.
 *
 * Every selector here is an existing `data-testid` / component selector already shipped by the
 * feature components — the director drives them as-is (no feature-code changes). Steps that touch
 * a gated write carry an honest {@link DirectorStep.posture}. Steps whose live data may be empty
 * right now (no eligible siblings, no candidates) are {@link DirectorStep.optional} so the tour
 * skips-with-caption instead of stalling.
 */

/** Ad-hoc lane used to pull Dispatch Assist recommendations without needing a specific live load id. */
const DEMO_LANE = {
  originCity: 'Laredo',
  originState: 'TX',
  destinationCity: 'Dallas',
  destinationState: 'TX',
} as const;

/** Static fallback lane fields, used only when no live load is resolved at runtime. */
const DEMO_LANE_FIELDS: readonly DirectorFill[] = [
  { selector: '#da-ocity', value: DEMO_LANE.originCity },
  { selector: '#da-ostate', value: DEMO_LANE.originState },
  { selector: '#da-dcity', value: DEMO_LANE.destinationCity },
  { selector: '#da-dstate', value: DEMO_LANE.destinationState },
];

/** Example parent seed for the Consolidate corridor (mirrors the existing demo-tour seed). */
const DEMO_SEED = 'L-100234';

/** True when an Invoice Studio surface (sibling agent's work) is mounted in this build. */
function invoiceStudioPresent(): boolean {
  if (typeof document === 'undefined') return false;
  return !!document.querySelector('[data-testid^="invoice-studio"], app-invoice-studio, .invoice-studio');
}

export const DEMO_DIRECTOR_SCRIPT: readonly DirectorStep[] = [
  // ---- Act A — Dock: land a truck, auto-combine LTL freight in a few taps -------------------
  {
    id: 'dock-open',
    act: 'Dock',
    caption:
      'Dock mode. A dock worker lands a truck at the yard and combines LTL freight in a few taps — all from live Alvys reads.',
    route: '/ltl/dock',
    target: 'app-dock',
    waitFor: ['[data-testid="dock-auto-toggle"]', 'app-dock'],
  },
  {
    id: 'dock-auto-on',
    act: 'Dock',
    caption:
      'Turning on Auto. The app auto-picks the BOL-controlling parent load and the best-fit siblings — never silently, never fabricated.',
    target: '[data-testid="dock-auto-toggle"]',
    waitFor: ['[data-testid="dock-auto-toggle"]'],
    action: 'check',
    actionSelector: '[data-testid="dock-auto-toggle"]',
  },
  {
    id: 'dock-pick-yard',
    act: 'Dock',
    caption:
      'Selecting a yard so live arrivals load from Alvys, then Auto walks the parent → siblings → review cascade.',
    target: 'app-dock',
    action: 'click',
    actionSelector: 'app-dock button.tap-card',
    // Wait for the cascade to land on one of its honest terminal states.
    waitFor: [
      '[data-testid="dock-onetap"]',
      '[data-testid="dock-auto-eject"]',
      '[data-testid="dock-security-hold"]',
    ],
    waitMs: 20_000,
    optional: true,
  },
  {
    id: 'dock-plan-preview',
    act: 'Dock',
    caption:
      'Plan preview assembled — parent plus siblings with combined economics. One tap to combine.',
    target: '[data-testid="dock-onetap"]',
    waitFor: ['[data-testid="dock-onetap"]'],
    optional: true,
    resolveCaption: () =>
      document.querySelector('[data-testid="dock-auto-eject"]')
        ? 'No eligible dock siblings on the yard right now — Auto ejected to the manual flow (honest empty state). Skipping the one-tap combine.'
        : null,
  },
  {
    id: 'dock-combine',
    act: 'Dock',
    caption: 'Combining. This records an internal audit only — nothing is pushed to Alvys.',
    posture:
      'Alvys writeback is OFF (gated). The combine records an internal audit (AlvysWriteback = NotPerformed) and produces the click card the dispatcher executes manually.',
    target: '[data-testid="dock-onetap"]',
    waitFor: ['[data-testid="dock-onetap-btn"]'],
    action: 'click',
    actionSelector: '[data-testid="dock-onetap-btn"]',
    optional: true,
  },
  {
    id: 'dock-result',
    act: 'Dock',
    caption:
      'Combine recorded with a full audit trail; the Alvys click card is ready for the dispatcher to execute manually.',
    target: 'app-dock',
    waitFor: ['[data-testid="dock-auto-chip"]', '[data-testid="dock-download-pdf"]', 'app-dock'],
    optional: true,
  },

  // ---- Act B — Consolidate: corridor board, candidates, plan economics ----------------------
  {
    id: 'consolidate-open',
    act: 'Consolidate',
    caption:
      'Consolidate. The corridor board for the Laredo → Dallas pilot — corridor health and uplift computed from live Alvys loads.',
    route: '/ltl/consolidate',
    target: '[data-testid="corridor-picker"]',
    waitFor: ['[data-testid="corridor-picker"]'],
  },
  {
    id: 'consolidate-seed',
    act: 'Consolidate',
    caption: 'Seeding a real parent load from live Alvys to find consolidation candidates on this corridor.',
    target: '[data-testid="consolidate-seed-form"]',
    waitFor: ['[data-testid="consolidate-seed-input"]'],
    action: 'fill',
    actionSelector: '[data-testid="consolidate-seed-input"]',
    fillValue: DEMO_SEED,
    // Prefer a live load number so the seed resolves against this tenant; fall back to the static seed.
    resolveFillValue: (ctx) => ctx.loadNumber,
  },
  {
    id: 'consolidate-find',
    act: 'Consolidate',
    caption: 'Finding candidates from Alvys…',
    target: '[data-testid="consolidate-seed-form"]',
    waitFor: ['[data-testid="consolidate-find-candidates"]'],
    action: 'click',
    actionSelector: '[data-testid="consolidate-find-candidates"]',
  },
  {
    id: 'consolidate-candidates',
    act: 'Consolidate',
    caption:
      'Candidate siblings ranked by fit; the projected plan economics and margin uplift show on the right.',
    target: '.candidates-card',
    waitFor: ['[data-testid="consolidate-candidate-row"]', '[data-testid="consolidate-empty-state"]'],
    optional: true,
    resolveCaption: () =>
      document.querySelector('[data-testid="consolidate-empty-state"]')
        ? 'No open candidates on the corridor right now — the queue shows its honest empty state (live Alvys data drives this).'
        : null,
    resolveTarget: () =>
      document.querySelector('[data-testid="consolidate-empty-state"]')
        ? '[data-testid="consolidate-empty-state"]'
        : null,
  },
  {
    id: 'consolidate-uplift',
    act: 'Consolidate',
    caption:
      'Plan uplift — the projected margin gain from consolidating this corridor, before anything is committed.',
    target: '[data-testid="consolidate-uplift"]',
    waitFor: ['[data-testid="consolidate-uplift"]'],
    optional: true,
  },

  // ---- Act C — Loads render + Dispatch Assist ------------------------------------------------
  {
    id: 'search-open',
    act: 'Loads & Dispatch',
    caption: 'Search. Live Alvys freight rendered as a prioritized dispatch worklist — not a raw grid.',
    route: '/ltl',
    target: 'app-ltl-search',
    waitFor: ['app-ltl-search'],
  },
  {
    id: 'loads-console',
    act: 'Loads & Dispatch',
    caption: 'Loads console. Fleet capacity and live loads read straight from Alvys.',
    route: '/ltl/loads',
    target: '[data-testid="capacity-widget"]',
    waitFor: ['[data-testid="capacity-widget"]', 'app-ltl-console'],
    optional: true,
  },
  {
    id: 'dispatch-open',
    act: 'Loads & Dispatch',
    caption:
      'Dispatch Assist. Ranked driver + truck + trailer recommendations for a load or lane, each row explaining its score.',
    route: '/ltl/dispatch',
    target: '[data-testid="dispatch-assist"]',
    waitFor: ['[data-testid="da-loadid"]'],
  },
  {
    id: 'dispatch-lane',
    act: 'Loads & Dispatch',
    caption: 'Entering a live lane pulled from Alvys freight to pull recommendations from the fleet.',
    target: '[data-testid="dispatch-assist"]',
    waitFor: ['#da-ocity'],
    action: 'fillMany',
    fields: DEMO_LANE_FIELDS,
    // Prefer the lane of a real live load (guaranteed to exist on this tenant); fall back to the
    // static demo lane. Only override when the live load carries a complete origin+destination.
    resolveFields: (ctx) =>
      ctx.originCity && ctx.originState && ctx.destinationCity && ctx.destinationState
        ? [
            { selector: '#da-ocity', value: ctx.originCity },
            { selector: '#da-ostate', value: ctx.originState },
            { selector: '#da-dcity', value: ctx.destinationCity },
            { selector: '#da-dstate', value: ctx.destinationState },
          ]
        : null,
  },
  {
    id: 'dispatch-search',
    act: 'Loads & Dispatch',
    caption: 'Requesting recommendations from live Alvys drivers, trucks and trailers…',
    target: '[data-testid="dispatch-assist"]',
    waitFor: ['[data-testid="da-search"]'],
    action: 'click',
    actionSelector: '[data-testid="da-search"]',
  },
  {
    id: 'dispatch-candidates',
    act: 'Loads & Dispatch',
    caption:
      'Ranked candidates. Every row explains its score — equipment compatibility, capacity, geography, readiness.',
    target: '[data-testid="da-candidates"]',
    waitFor: ['[data-testid="da-candidates"]'],
    waitMs: 20_000,
    optional: true,
    resolveCaption: () =>
      document.querySelector('[data-testid="da-candidates"]')
        ? null
        : 'No candidates returned for this lane right now — the panel shows its honest empty state (live Alvys reads).',
  },
  {
    id: 'dispatch-assemble',
    act: 'Loads & Dispatch',
    caption:
      'Assembling the chosen driver / truck / trailer, then firing the notify step to driver + dispatcher.',
    posture:
      'Recorded app-side only (AlvysWriteback = NotPerformed). Comms are flag-gated (default OFF) and rerouted to the override recipient — the banner shows exactly where they went.',
    target: '[data-testid="da-candidates"]',
    waitFor: ['[data-testid^="da-assemble-"]'],
    action: 'click',
    actionSelector: '[data-testid^="da-assemble-"]',
    optional: true,
  },
  {
    id: 'dispatch-override',
    act: 'Loads & Dispatch',
    caption:
      'Assembly recorded with the recipient-override banner visible — no write reached Alvys, and comms were rerouted honestly.',
    target: '[data-testid="da-override-banner"]',
    waitFor: ['[data-testid="da-assembly-result"]', '[data-testid="da-override-banner"]'],
    optional: true,
  },

  // ---- Act D — Back-office tabs --------------------------------------------------------------
  {
    id: 'billing',
    act: 'Back Office',
    caption:
      'Billing worklist. Readiness-first, revenue-at-risk visible, filterable by the blocker badge that is holding each invoice.',
    route: '/ltl/billing',
    target: 'app-ltl-billing',
    waitFor: ['[data-testid="billing-count"]', '[data-testid="billing-loading"]', '[data-testid="billing-empty"]'],
    optional: true,
  },
  {
    id: 'ar-aging',
    act: 'Back Office',
    caption:
      'AR / aging. Overdue balances surface as "days past due" on each billing row so accounting sees revenue-at-risk first.',
    target: '[data-testid="billing-revenue-at-risk"]',
    waitFor: ['[data-testid="billing-revenue-at-risk"]', 'app-ltl-billing'],
    optional: true,
  },
  {
    id: 'exceptions',
    act: 'Back Office',
    caption:
      'Exceptions. Loads carrying an operational or visibility exception that is blocking billing or dispatch.',
    route: '/ltl/exceptions',
    target: 'app-ltl-exceptions',
    waitFor: ['[data-testid="exceptions-count"]', '[data-testid="exceptions-loading"]'],
    optional: true,
  },
  {
    id: 'reporting',
    act: 'Back Office',
    caption:
      'Reporting. Margin and exception rollups over the same Alvys-derived load set, with CSV export for external BI.',
    route: '/ltl/reporting',
    target: '[data-testid="reporting-export-csv"]',
    waitFor: ['[data-testid="reporting-count"]', '[data-testid="reporting-loading"]', '[data-testid="reporting-empty"]'],
    optional: true,
  },
  {
    id: 'notifications',
    act: 'Back Office',
    caption:
      'Notifications. Workflow events with honest per-channel config — in-app is always on; Teams / email are config-gated.',
    route: '/ltl/notifications',
    target: '[data-testid="notif-channels"]',
    waitFor: ['[data-testid="notif-channels"]'],
    optional: true,
  },
  {
    id: 'agents-heartbeat',
    act: 'Back Office',
    caption:
      'Agents heartbeat. Signals shows the extractor and ingest agents’ live status — the automation pulse behind the worklists.',
    route: '/ltl/signals',
    target: '[data-testid="signals-extractor"]',
    waitFor: ['[data-testid="signals-extractor"]', '[data-testid="signals-ingest"]'],
    optional: true,
  },

  // ---- Act E — LTL lifecycle: create → comms → bill (data-driven, degrades gracefully) -------
  {
    id: 'lifecycle-comms',
    act: 'Lifecycle',
    caption:
      'LTL lifecycle. The service is created and dispatched through the flow just shown; the comms step notifies driver + dispatcher here.',
    posture:
      'Comms are flag-gated (default OFF). While gated, every message reroutes to the override recipient — shown on the feed, never silently sent to a live contact.',
    route: '/ltl/notifications',
    target: '[data-testid="notif-feed"]',
    waitFor: ['[data-testid="notif-feed"]', '[data-testid="notif-empty"]', '[data-testid="notif-channels"]'],
    optional: true,
  },
  {
    id: 'lifecycle-billing',
    act: 'Lifecycle',
    caption:
      'Billing is updated to close the loop. Using the billing worklist to show the invoice-readiness change.',
    route: '/ltl/billing',
    target: 'app-ltl-billing',
    waitFor: ['[data-testid="billing-count"]', '[data-testid="billing-loading"]', '[data-testid="billing-empty"]'],
    optional: true,
    resolveCaption: () =>
      invoiceStudioPresent()
        ? 'Billing is updated to close the loop. Invoice Studio is deployed in this build — the invoice is drafted and reconciled here.'
        : null,
    resolveTarget: () =>
      invoiceStudioPresent()
        ? '[data-testid^="invoice-studio"], app-invoice-studio, .invoice-studio'
        : null,
  },
  {
    id: 'lifecycle-done',
    act: 'Lifecycle',
    caption:
      'That completes Search → Match → Assign → Bill. Every step ran on live Alvys reads; every write stayed gated and auditable.',
    waitFor: ['app-ltl-shell', 'app-ltl-billing'],
    optional: true,
  },
];
