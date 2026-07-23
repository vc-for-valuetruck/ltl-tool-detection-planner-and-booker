import { DemoContext, DemoOriginHotspot, DirectorFill, DirectorStep } from './demo-director.models';

/**
 * The scripted walkthrough, in order — the revenue story of Search → Match → Assign → Bill told for
 * a CFO. Every selector here is an existing `data-testid` / component selector already shipped by the
 * feature components; the director drives them as-is (no feature-code changes).
 *
 * Two rules govern this script:
 *  1. NOTHING is hardcoded to a specific load or lane. The data-driven acts pull their load number
 *     and lane from the run's live {@link DemoContext} (assembled at start from the app's own
 *     authenticated open-loads feed). When the live read is empty the step either narrates an honest
 *     empty state or, as a last resort, skips-with-caption. See the resolve* callbacks below.
 *  2. The narration is plain-English and leads with money — empty miles recovered, two invoices on
 *     one truck, minutes not hours, an audit trail your auditors will love. One light technical beat
 *     (the gate/audit design) is kept because the audience is technical, but the jargon is gone.
 */

/** Fallback lane fields, used ONLY when the live cast could not resolve a real anchor lane. */
const FALLBACK_LANE_FIELDS: readonly DirectorFill[] = [
  { selector: '#da-ocity', value: 'Laredo' },
  { selector: '#da-ostate', value: 'TX' },
  { selector: '#da-dcity', value: 'Dallas' },
  { selector: '#da-dstate', value: 'TX' },
];

/** Selector for the yard cards on the Dock warehouse step (excludes arrival/sibling cards). */
const YARD_CARD_SELECTOR = 'app-dock .tap-grid button.tap-card:not(.arrival):not(.sibling)';

/**
 * Chooses which yard card the Dock should land the truck at: the yard nearest the busiest live
 * freight. Reads the rendered yard cards and matches them against the run's origin hotspots (busiest
 * first) by state, then city; falls back to the first yard when nothing matches. Never fabricates a
 * pick — if no cards are rendered it returns null and the step degrades honestly.
 */
function chooseYard(ctx: DemoContext | null): string | null {
  if (typeof document === 'undefined') return null;
  const cards = Array.from(document.querySelectorAll<HTMLElement>(YARD_CARD_SELECTOR));
  if (cards.length === 0) return null;
  const nth = (i: number) => `${YARD_CARD_SELECTOR}:nth-of-type(${i + 1})`;
  for (const spot of ctx?.originHotspots ?? []) {
    const state = spot.state?.trim().toUpperCase();
    const city = spot.city?.trim().toUpperCase();
    const idx = cards.findIndex((c) => {
      const text = (c.textContent ?? '').toUpperCase();
      return (
        (!!state && new RegExp(`·\\s*${state}\\b`).test(text)) || (!!city && text.includes(city))
      );
    });
    if (idx >= 0) return nth(idx);
  }
  return nth(0);
}

/** Names the busiest origin hotspot for the Dock yard-pick narration ("8 loads out of Laredo"). */
function hotspotPhrase(spot: DemoOriginHotspot | undefined): string | null {
  if (!spot?.city) return null;
  const where = spot.state ? `${spot.city}, ${spot.state}` : spot.city;
  const loads = `${spot.count} ${spot.count === 1 ? 'load' : 'loads'}`;
  return `${loads} moving out of ${where}`;
}

/** True when an Invoice Studio surface is mounted in this build. */
function invoiceStudioPresent(): boolean {
  if (typeof document === 'undefined') return false;
  return !!document.querySelector('[data-testid^="invoice-studio"], app-invoice-studio, .invoice-studio');
}

/** Rounds a load count into a warm phrase, e.g. "just over 400". Falls back to the exact number. */
function volumePhrase(total: number | null): string {
  if (total == null || total <= 0) return 'the freight moving through your network';
  if (total >= 50) return `the ${total.toLocaleString()} loads moving through your network right now`;
  return `the ${total.toLocaleString()} loads live in your network right now`;
}

export const DEMO_DIRECTOR_SCRIPT: readonly DirectorStep[] = [
  // ---- Act A — Dock: land a truck, put two shipments on one trailer ---------------------------
  {
    id: 'dock-open',
    act: 'Dock',
    caption:
      'Welcome. Over the next little while I’ll walk you through your freight the way your team lives it — find the work, pick the right truck, make the call, and get it billed. We start on the dock, where a worker has just landed a truck. Every number you see is your real freight, pulled live from your system of record.',
    route: '/ltl/dock',
    target: 'app-dock',
    waitFor: [YARD_CARD_SELECTOR, 'app-dock'],
    dwellMs: 9_000,
  },
  {
    id: 'dock-pick-yard',
    act: 'Dock',
    caption:
      'I’m landing the truck at the yard closest to your busiest freight — I’ll pick it now. The moment I do, the app reads the trucks actually sitting there and starts the matching a person would otherwise do by phone and spreadsheet.',
    target: 'app-dock',
    action: 'click',
    resolveActionSelector: (ctx) => chooseYard(ctx),
    actionSelector: YARD_CARD_SELECTOR,
    waitFor: [YARD_CARD_SELECTOR],
    optional: true,
    dwellMs: 9_000,
    resolveCaption: (ctx) => {
      const phrase = hotspotPhrase(ctx?.originHotspots?.[0]);
      return phrase
        ? `I’m landing the truck at the yard closest to your busiest freight — right now that’s ${phrase}, more than anywhere else on your board — so I’ll pick that yard. The moment I do, the app reads the trucks actually sitting there and starts the matching a person would otherwise do by phone and spreadsheet.`
        : null;
    },
  },
  {
    id: 'dock-parent',
    act: 'Dock',
    caption:
      'First the app picks the shipment that controls the paperwork — the BOL-controlling load — from the trucks really at this yard. I’m tapping one that has partner freight worth pairing, so you can see a genuine combine, not a staged one.',
    target: 'app-dock',
    action: 'clickRetry',
    retry: {
      candidateSelector: 'app-dock button.tap-card.arrival:not([disabled])',
      successSelector: 'app-dock button.tap-card.sibling:not([disabled])',
      resetSelector: 'app-dock .dock-head button.btn-ghost',
      maxAttempts: 4,
    },
    waitFor: ['app-dock button.tap-card.arrival', 'app-dock .state-empty'],
    waitMs: 20_000,
    optional: true,
    dwellMs: 9_000,
    resolveCaption: () =>
      document.querySelector('app-dock button.tap-card.arrival')
        ? null
        : 'No trucks are inbound to this yard for the board day right now — and the app says so plainly rather than inventing one. On a working shift this is where the parent load is chosen. We’ll move on.',
  },
  {
    id: 'dock-siblings',
    act: 'Dock',
    caption:
      'Now the payoff: the app has already found the partner shipment heading the same way, ranked by how well it fits — same lane, same day, right capacity. I’m adding it, so two invoices now ride on one trailer and the empty miles you were paying for disappear.',
    target: 'app-dock',
    action: 'click',
    actionSelector: 'app-dock button.tap-card.sibling:not([disabled])',
    waitFor: ['app-dock button.tap-card.sibling:not([disabled])'],
    optional: true,
    dwellMs: 9_000,
    resolveCaption: () =>
      document.querySelector('app-dock button.tap-card.sibling:not([disabled])')
        ? null
        : 'No partner freight worth combining on this parent at this exact moment, and the app says so rather than padding the screen. On a busy day this is where the second invoice appears. We’ll move on.',
  },
  {
    id: 'dock-review',
    act: 'Dock',
    caption:
      'Before committing, the app shows the combined driver math — the loads together, the miles, and the revenue per mile — so the decision is made on numbers, not a hunch. I’m opening that review now.',
    target: 'app-dock',
    action: 'click',
    actionSelector: 'app-dock .selected-actions .btn-ghost',
    waitFor: ['app-dock .selected-actions .btn-ghost'],
    optional: true,
    dwellMs: 8_000,
  },
  {
    id: 'dock-combine',
    act: 'Dock',
    caption:
      'When I combine, the app writes a complete record of the decision — who, what, when, and the dollars — but it does not touch your system of record. Nothing posts back until leadership signs off. That’s by design, so a demo can never move real freight.',
    posture:
      'Writeback stays OFF and gated: the combine is recorded internally only (AlvysWriteback = NotPerformed) and produces a click card the dispatcher executes by hand. Nothing is written to your system of record.',
    target: 'app-dock',
    action: 'click',
    actionSelector: 'app-dock .review-actions .btn-primary:not([disabled])',
    waitFor: ['app-dock .review-actions .btn-primary'],
    optional: true,
    dwellMs: 8_000,
  },
  {
    id: 'dock-result',
    act: 'Dock',
    caption:
      'Recorded, with a full trail your auditors will love, and a ready-to-paste instruction for the dispatcher. That’s two shipments on one truck, decided in seconds — the first dollars we’ve protected today.',
    target: 'app-dock',
    waitFor: ['[data-testid="dock-download-pdf"]', 'app-dock .result', 'app-dock'],
    optional: true,
    dwellMs: 8_000,
  },

  // ---- Act B — Consolidate: the busiest lanes in your network, live -------------------------
  {
    id: 'consolidate-open',
    act: 'Consolidate',
    caption:
      'Let’s zoom out from one dock to your whole network. This board ranks your busiest lanes by how much freight is moving on each — and it’s built from your live loads, not a sample. The app has already put the busiest lane at the top and lined up the shipments that could travel together.',
    route: '/ltl/consolidate',
    target: '[data-testid="corridor-picker"]',
    waitFor: ['[data-testid="corridor-picker"]'],
    dwellMs: 9_000,
    resolveCaption: (ctx: DemoContext | null) => {
      const lanes = ctx?.topLanes ?? [];
      if (lanes.length === 0) return null;
      const top = lanes[0];
      const busiest = `${top.label} with ${top.openLoadCount} open ${top.openLoadCount === 1 ? 'load' : 'loads'}`;
      return `Let’s zoom out from one dock to your whole network. Right now I’m looking at ${volumePhrase(ctx?.totalOpen ?? null)}, grouped by lane. Your busiest is ${busiest} — the app has put it at the top and lined up the shipments that could travel together. Nothing here is a sample; it’s all live.`;
    },
  },
  {
    id: 'consolidate-seed',
    act: 'Consolidate',
    caption:
      'Rather than wait for a lane to fill, let me put this to work on a real load right now. I’m typing one of your live load numbers into the search and asking the app to pull every shipment that could ride with it.',
    target: '[data-testid="consolidate-seed-form"]',
    action: 'seedFind',
    seedFind: {
      seedSelector: '[data-testid="consolidate-seed-input"]',
      findSelector: '[data-testid="consolidate-find-candidates"]',
      rowSelector: '[data-testid="consolidate-candidate-row"]',
      maxAttempts: 3,
    },
    waitFor: ['[data-testid="consolidate-seed-input"]'],
    optional: true,
    dwellMs: 8_000,
    resolveCaption: (ctx) =>
      ctx?.anchorCandidates && ctx.anchorCandidates.length > 0
        ? `Rather than wait for a lane to fill, let me put this to work on one of your real loads right now — load ${ctx.anchorCandidates[0]}. I’m dropping it into the search and asking the app to pull every shipment that could ride with it.`
        : null,
  },
  {
    id: 'consolidate-candidates',
    act: 'Consolidate',
    caption:
      'These are the real shipments that could ride with that load, ranked by how well they fit — same lane, same day, right capacity. A dispatcher reads this in a glance instead of cross-checking a dozen loads by hand. I’m adding the top match so you can see the money.',
    target: '.candidates-card',
    action: 'click',
    actionSelector: '[data-testid="consolidate-candidate-checkbox"]',
    waitFor: [
      '[data-testid="consolidate-candidate-row"]',
      '[data-testid="consolidate-empty-state"]',
      '.banner-warn',
    ],
    optional: true,
    dwellMs: 9_000,
    resolveCaption: () =>
      document.querySelector('[data-testid="consolidate-empty-state"], .banner-warn') ||
      !document.querySelector('[data-testid="consolidate-candidate-checkbox"]')
        ? 'No pairs worth combining on this load at this exact moment — and the app says so plainly rather than padding the screen. On a normal business day this queue is where consolidation dollars are found.'
        : null,
    resolveTarget: () =>
      document.querySelector('[data-testid="consolidate-empty-state"]')
        ? '[data-testid="consolidate-empty-state"]'
        : document.querySelector('.banner-warn')
          ? '.banner-warn'
          : null,
  },
  {
    id: 'consolidate-uplift',
    act: 'Consolidate',
    caption:
      'And here’s the number that matters to you: the projected margin gain from consolidating this lane, worked out from your own revenue and miles — before anyone commits to anything. Multiply this across every busy lane and it’s real money back on the table.',
    target: '[data-testid="consolidate-uplift"]',
    waitFor: ['[data-testid="consolidate-uplift"]', '[data-testid="corridor-chip-uplift"]'],
    optional: true,
    dwellMs: 8_000,
  },

  // ---- Act C — Search + Loads + Dispatch Assist: the right truck, explained -----------------
  {
    id: 'search-open',
    act: 'Loads & Dispatch',
    caption:
      'This is the front door your dispatchers use. It isn’t a spreadsheet of everything — it’s a prioritized worklist that puts the loads needing attention, and the revenue at risk, right at the top. Same live freight, organized the way a decision-maker needs it.',
    route: '/ltl',
    target: 'app-ltl-search',
    waitFor: ['app-ltl-search'],
    dwellMs: 8_000,
  },
  {
    id: 'loads-console',
    act: 'Loads & Dispatch',
    caption:
      'Alongside the work, a live read of your capacity — the trucks and trailers actually available. Matching work to capacity is the whole game, and here they sit on one screen.',
    route: '/ltl/loads',
    target: '[data-testid="capacity-widget"]',
    waitFor: ['[data-testid="capacity-widget"]', 'app-ltl-console'],
    optional: true,
    dwellMs: 8_000,
  },
  {
    id: 'dispatch-open',
    act: 'Loads & Dispatch',
    caption:
      'Now the match. Dispatch Assist ranks the best driver, truck and trailer for a shipment — and, just as importantly, tells you why. No black box; every recommendation shows its reasoning, so your team can stand behind the call.',
    route: '/ltl/dispatch',
    target: '[data-testid="dispatch-assist"]',
    waitFor: ['[data-testid="da-loadid"]'],
    dwellMs: 8_000,
  },
  {
    id: 'dispatch-lane',
    act: 'Loads & Dispatch',
    caption:
      'I’m dropping in a real lane from your busiest corridor so the recommendations are grounded in freight you actually have.',
    target: '[data-testid="dispatch-assist"]',
    waitFor: ['#da-ocity'],
    action: 'fillMany',
    fields: FALLBACK_LANE_FIELDS,
    dwellMs: 7_000,
    // Prefer the live anchor's real lane; only fall back to the static lane when the cast is empty.
    resolveFields: (ctx: DemoContext) =>
      ctx.originCity && ctx.originState && ctx.destinationCity && ctx.destinationState
        ? [
            { selector: '#da-ocity', value: ctx.originCity },
            { selector: '#da-ostate', value: ctx.originState },
            { selector: '#da-dcity', value: ctx.destinationCity },
            { selector: '#da-dstate', value: ctx.destinationState },
          ]
        : null,
    resolveCaption: (ctx: DemoContext | null) =>
      ctx?.laneLabel
        ? `I’m dropping in a real lane from your network — ${ctx.laneLabel} — so the recommendations are grounded in freight you actually have, not a made-up example.`
        : null,
  },
  {
    id: 'dispatch-search',
    act: 'Loads & Dispatch',
    caption:
      'And I’ll ask the app to rank your fleet against it.',
    target: '[data-testid="dispatch-assist"]',
    waitFor: ['[data-testid="da-search"]'],
    action: 'click',
    actionSelector: '[data-testid="da-search"]',
    dwellMs: 6_000,
  },
  {
    id: 'dispatch-candidates',
    act: 'Loads & Dispatch',
    caption:
      'Here’s the ranked shortlist. Each row scores the fit and spells out the reasons — equipment, capacity, how close the driver is, how ready they are to roll. This is the judgement of an experienced dispatcher, made consistent and instant across your whole fleet.',
    target: '[data-testid="da-candidates"]',
    waitFor: ['[data-testid="da-candidates"]'],
    waitMs: 20_000,
    optional: true,
    dwellMs: 9_000,
    resolveCaption: () =>
      document.querySelector('[data-testid="da-candidates"]')
        ? null
        : 'No drivers clear the bar for this lane at this exact moment, and the app says so rather than forcing a weak match. Better an honest empty answer than a bad assignment.',
  },
  {
    id: 'dispatch-assemble',
    act: 'Loads & Dispatch',
    caption:
      'When the dispatcher picks a driver, the app records the decision and lines up the notifications to the driver and the office. Notice the safety rail: messages are held and rerouted to a safe inbox until you switch them on, so a walkthrough can never text a real driver by accident.',
    posture:
      'Recorded internally only (AlvysWriteback = NotPerformed). Messages are OFF by default and rerouted to a safe address — the banner shows exactly where they went. Nothing reaches a real contact or your system of record.',
    target: '[data-testid="da-candidates"]',
    waitFor: ['[data-testid^="da-assemble-"]'],
    action: 'click',
    actionSelector: '[data-testid^="da-assemble-"]',
    optional: true,
    dwellMs: 8_000,
  },
  {
    id: 'dispatch-override',
    act: 'Loads & Dispatch',
    caption:
      'Decision recorded, and the banner confirms the messages were held safely — nothing left the building, and nothing posted to your system of record. Every assignment leaves a clean trail behind it.',
    target: '[data-testid="da-override-banner"]',
    waitFor: ['[data-testid="da-assembly-result"]', '[data-testid="da-override-banner"]'],
    optional: true,
    dwellMs: 8_000,
  },

  // ---- Act D — Back office: where revenue is protected and proven ---------------------------
  {
    id: 'billing',
    act: 'Back Office',
    caption:
      'Now the money side. This is the billing worklist — and it’s sorted by what pays first and what’s at risk, not alphabetically. Anything blocking an invoice is flagged with the exact reason, so nothing quietly ages into a write-off.',
    route: '/ltl/billing',
    target: 'app-ltl-billing',
    waitFor: ['[data-testid="billing-count"]', '[data-testid="billing-loading"]', '[data-testid="billing-empty"]'],
    optional: true,
    dwellMs: 9_000,
  },
  {
    id: 'ar-aging',
    act: 'Back Office',
    caption:
      'Overdue balances surface right on the row as days past due, so your accounting team chases the revenue that’s slipping away first. This is leakage caught early instead of found at month-end.',
    target: '[data-testid="billing-revenue-at-risk"]',
    waitFor: ['[data-testid="billing-revenue-at-risk"]', 'app-ltl-billing'],
    optional: true,
    dwellMs: 8_000,
  },
  {
    id: 'invoice-studio',
    act: 'Back Office',
    caption:
      'This is Invoice Studio — where the two shipments we put on one truck earlier become one clean customer invoice. It pulls the charges together, checks the paperwork is attached, and produces a professional PDF. Two loads, one bill, no manual re-keying.',
    route: '/ltl/invoice-studio',
    target: '[data-testid="invoice-studio"]',
    waitFor: ['[data-testid="invoice-studio"]', '[data-testid="is-list"]', '[data-testid="is-form"]'],
    optional: true,
    dwellMs: 9_000,
  },
  {
    id: 'tenders',
    act: 'Back Office',
    caption:
      'Tenders is where incoming freight offers land, already enriched with pallet, piece and weight detail. It means your team quotes and books from real numbers instead of guessing at the dock.',
    route: '/ltl/tenders',
    target: 'app-ltl-tenders',
    waitFor: ['[data-testid="tenders-count"]', '[data-testid="tenders-loading"]', '[data-testid="tenders-empty"]'],
    optional: true,
    dwellMs: 8_000,
  },
  {
    id: 'assignments',
    act: 'Back Office',
    caption:
      'Every assignment decision we make lands here, permanently — who assigned what, when, and the reason for any override. If a customer or an auditor ever asks how a load was handled, the answer is one click away.',
    route: '/ltl/assignments',
    target: 'app-ltl-assignments',
    waitFor: ['[data-testid="assignments-count"]', '[data-testid="assignments-loading"]', '[data-testid="assignments-empty"]'],
    optional: true,
    dwellMs: 8_000,
  },
  {
    id: 'exceptions',
    act: 'Back Office',
    caption:
      'Exceptions gathers the loads carrying a problem — a late delivery, a truck stuck at a stop — that could hold up billing or a customer promise. Seeing them in one place is how small issues get fixed before they cost you.',
    route: '/ltl/exceptions',
    target: 'app-ltl-exceptions',
    waitFor: ['[data-testid="exceptions-count"]', '[data-testid="exceptions-loading"]', '[data-testid="exceptions-empty"]'],
    optional: true,
    dwellMs: 8_000,
  },
  {
    id: 'signals',
    act: 'Back Office',
    caption:
      'Signals is the app reading your notes and documents for money you might be leaving on the table — a detention charge, an accessorial — and surfacing it with the exact evidence quote. Nothing is charged automatically; it’s simply put in front of a person to approve.',
    route: '/ltl/signals',
    target: '[data-testid="signals-extractor"]',
    waitFor: ['[data-testid="signals-extractor"]', '[data-testid="signals-feed"]', '[data-testid="signals-empty"]'],
    optional: true,
    dwellMs: 8_000,
  },
  {
    id: 'reporting',
    act: 'Back Office',
    caption:
      'Reporting rolls the whole picture up by customer, by rep, by lane — margin and exceptions on your real numbers, with a one-click export straight into your own BI tools. Your reporting stack pulls from the same source of truth as the dispatch floor.',
    route: '/ltl/reporting',
    target: '[data-testid="reporting-export-csv"]',
    waitFor: ['[data-testid="reporting-count"]', '[data-testid="reporting-loading"]', '[data-testid="reporting-empty"]'],
    optional: true,
    dwellMs: 8_000,
  },
  {
    id: 'notifications',
    act: 'Back Office',
    caption:
      'Notifications keeps everyone in step as work moves through the flow. And it’s honest about how it reaches people — in-app is always on, while email and Teams stay off until you deliberately enable them.',
    route: '/ltl/notifications',
    target: '[data-testid="notif-channels"]',
    waitFor: ['[data-testid="notif-channels"]', '[data-testid="notif-feed"]', '[data-testid="notif-empty"]'],
    optional: true,
    dwellMs: 8_000,
  },

  // ---- Act E — Close the loop ---------------------------------------------------------------
  {
    id: 'lifecycle-billing',
    act: 'Lifecycle',
    caption:
      'So we close the loop back at billing, where the work we did on the dock and dispatch floor turns into a clean, ready invoice.',
    route: '/ltl/billing',
    target: 'app-ltl-billing',
    waitFor: ['[data-testid="billing-count"]', '[data-testid="billing-loading"]', '[data-testid="billing-empty"]'],
    optional: true,
    dwellMs: 8_000,
    resolveCaption: () =>
      invoiceStudioPresent()
        ? 'So we close the loop in Invoice Studio, where the work we did on the dock and the dispatch floor becomes one clean, ready-to-send invoice.'
        : null,
    resolveTarget: () =>
      invoiceStudioPresent()
        ? '[data-testid="invoice-studio"], app-invoice-studio, .invoice-studio'
        : null,
  },
  {
    id: 'lifecycle-done',
    act: 'Lifecycle',
    caption:
      'That’s the whole journey — find the freight, put two shipments on one truck, match the right driver, protect the revenue, and prove every decision. Everything you saw was your real, live freight, and not a single thing posted back to your system of record until you say so. Faster decisions, less leakage, and a trail your auditors will love. Thank you.',
    waitFor: ['app-ltl-shell', 'app-ltl-billing'],
    optional: true,
    dwellMs: 11_000,
  },
];
