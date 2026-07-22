import { CommonModule } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { catchError, of } from 'rxjs';
import { ConsolidationService } from './consolidation.service';
import { LtlParentChildBadge } from './ltl-parent-child-badge';
import { LtlEdiEnrichment, LtlLoadSummary } from './ltl.models';
import {
  ConsolidationAuditRecord,
  ConsolidationCandidate,
  ConsolidationCandidateResponse,
  ConsolidationFactor,
  ConsolidationFit,
  ConsolidationOpportunitiesResponse,
  ConsolidationOpportunity,
  ConsolidationOpportunityLoad,
  ConsolidationPlanResponse,
  CorridorHealth,
  CorridorHealthSnapshot,
  CorridorSummary,
} from './consolidation.models';

/** Merged view of a corridor + its live open-load count, ready to render in the picker. */
export interface CorridorPickerRow {
  code: string;
  originName: string;
  destinationName: string;
  /**
   * `null` when the corridors-health call degraded or hasn't completed yet. Distinct from
   * `0` (which means "we asked Alvys and there are no open loads right now").
   */
  openLoadCount: number | null;
  loadedCleanly: boolean;
  /**
   * First open load on the corridor's canonical lane. Used to auto-seed the candidate queue by
   * DEFAULT so the pilot corridor is populated on tab-load without a manual seed / app-settings.
   * `null` when the lane is empty or the Alvys read degraded — never fabricated.
   */
  seedLoadId: string | null;
  seedLoadNumber: string | null;
  /**
   * True for lanes discovered from the live "Today's consolidations" sweep rather than from the
   * configured pilot corridors. Live lanes let the demo walk the full workflow against real data
   * even when the pilot corridor has no open loads today.
   */
  isLiveLane: boolean;
  /** True when a live lane is NOT a configured pilot corridor — surfaced honestly in the UI. */
  outsidePilot: boolean;
  /**
   * The representative live opportunity (parent + siblings) that drives the walkthrough when a
   * live lane is selected. `null` for pilot-corridor rows (which use the seed/candidate path).
   */
  opportunity: ConsolidationOpportunity | null;
  /**
   * Projected consolidation uplift in dollars for the representative live opportunity on this
   * lane. `null` for pilot-corridor rows (no representative opportunity yet) and for live lanes
   * whose backing opportunity had no uplift computed. Rendered as a `+$X` chip on the corridor
   * picker so the tab leads with money, not just counts. Never fabricated — the value comes
   * straight from `ConsolidationOpportunity.projectedUplift`, which is computed server-side from
   * real Alvys revenue + mileage in `ConsolidationOpportunityService`.
   */
  projectedUplift: number | null;
}

/** Stable lane key from an opportunity load pair — normalises case/whitespace. */
function laneKeyOf(o: ConsolidationOpportunity): string {
  return [o.originCity, o.originState, o.destinationCity, o.destinationState]
    .map((s) => (s ?? '').trim().toUpperCase())
    .join('|');
}

/**
 * Builds live-lane picker rows from the opportunity sweep. Opportunities are grouped by lane;
 * each lane's representative opportunity is the one with the most loads (tie-broken by projected
 * uplift). `openLoadCount` is the count of distinct loads on the lane. Lanes whose origin→dest
 * state pair matches a configured pilot corridor are folded into the pilot row (skipped here) so
 * the picker never shows a duplicate chip for the pilot lane.
 */
export function buildLiveLaneRows(
  response: ConsolidationOpportunitiesResponse | null,
  pilotStatePairs: Set<string>,
): CorridorPickerRow[] {
  const byLane = new Map<string, ConsolidationOpportunity[]>();
  for (const opp of response?.opportunities ?? []) {
    const statePair = `${(opp.originState ?? '').toUpperCase()}->${(opp.destinationState ?? '').toUpperCase()}`;
    if (pilotStatePairs.has(statePair)) continue; // folded into the pilot row
    const key = laneKeyOf(opp);
    (byLane.get(key) ?? byLane.set(key, []).get(key)!).push(opp);
  }

  const rows: CorridorPickerRow[] = [];
  for (const [, opps] of byLane) {
    const loadCount = opps.reduce((n, o) => n + 1 + o.siblings.length, 0);
    const rep = [...opps].sort(
      (a, b) => b.siblings.length - a.siblings.length || b.projectedUplift - a.projectedUplift,
    )[0];
    rows.push({
      code: `LIVE::${laneKeyOf(rep)}`,
      originName: `${rep.originCity}, ${rep.originState}`,
      destinationName: `${rep.destinationCity}, ${rep.destinationState}`,
      openLoadCount: loadCount,
      loadedCleanly: true,
      seedLoadId: null,
      seedLoadNumber: null,
      isLiveLane: true,
      outsidePilot: true,
      opportunity: rep,
      // Representative opportunity's uplift — the same figure the plan-detail Economics panel
      // shows if the dispatcher picks this lane and opens the plan. Never fabricated.
      projectedUplift:
        typeof rep.projectedUplift === 'number' && Number.isFinite(rep.projectedUplift)
          ? rep.projectedUplift
          : null,
    });
  }
  // Busiest lane first so the picker reads high-value → low-value.
  return rows.sort((a, b) => (b.openLoadCount ?? 0) - (a.openLoadCount ?? 0));
}

/**
 * Chooses which picker row to select by default:
 *   1. the pilot corridor if it has a plannable pair — a seed hint AND at least two open loads
 *      on the lane, so the auto-seeded queue can actually form a consolidation (findings #5);
 *   2. otherwise the busiest live lane (most open loads), clearly labelled outside-pilot;
 *   3. otherwise the first pilot corridor (an honest empty state — nothing to plan today).
 * Pure and side-effect free so it can be unit-tested directly.
 */
export function chooseDefaultSelection(
  pilotRows: CorridorPickerRow[],
  liveLaneRows: CorridorPickerRow[],
): string | null {
  // A lone open load on the pilot lane can be seeded but never consolidated, so it must not beat
  // a live lane that actually yields a workable pair. Require >= 2 open loads alongside the seed.
  const pilotWithPair = pilotRows.find(
    (r) => (r.seedLoadId || r.seedLoadNumber) && (r.openLoadCount ?? 0) >= 2,
  );
  if (pilotWithPair) return pilotWithPair.code;
  if (liveLaneRows.length > 0) {
    return [...liveLaneRows].sort(
      (a, b) => (b.openLoadCount ?? 0) - (a.openLoadCount ?? 0),
    )[0].code;
  }
  return pilotRows[0]?.code ?? null;
}

/**
 * Phase 1 pilot: Laredo → Dallas consolidation planner. Three screens on one route:
 *   1. Enter a seed load id → see corridor-matching sibling candidates with per-factor chips
 *   2. Select siblings → build a plan preview with projected combined RPM
 *   3. Copy the sanctioned Alvys click card + record the plan as an audit entry
 *
 * Read-only end-to-end. Nothing on this screen writes to Alvys — the click card is text the
 * dispatcher pastes into Alvys manually, exactly following Poornima's yard walkthrough with
 * Holly. Every value derives from live Alvys reads or from static config.
 */
@Component({
  selector: 'app-consolidate',
  standalone: true,
  imports: [CommonModule, FormsModule, LtlParentChildBadge],
  templateUrl: './consolidate.html',
  styleUrls: ['./consolidate.css'],
})
export class Consolidate implements OnInit {
  private readonly consolidation = inject(ConsolidationService);
  private readonly router = inject(Router);

  // Corridor picker (loaded once on init).
  readonly loadingCorridors = signal(true);
  readonly corridors = signal<CorridorPickerRow[]>([]);
  readonly selectedCorridor = signal<string>('LAREDO_TO_DALLAS');

  /**
   * True when the selected picker row is a live lane discovered from the opportunity sweep
   * (outside the configured pilot corridor). Live lanes drive the walkthrough from a synthesized
   * candidate/plan built entirely from the opportunity's real Alvys fields — never fabricated —
   * and route the "Generate click card" / "Save as audit" actions through the corridor-agnostic
   * live-plan + ungated-audit endpoints (the corridor-gated ones reject non-pilot lanes by design).
   */
  readonly liveLaneMode = signal(false);
  /** The live opportunity backing the current live-lane walkthrough. `null` in pilot mode. */
  readonly selectedOpportunity = signal<ConsolidationOpportunity | null>(null);

  // Live footer facts from the opportunity sweep (non-empty state only).
  readonly totalScanned = signal<number | null>(null);
  readonly generatedAt = signal<string | null>(null);
  readonly dataSource = signal<string | null>(null);

  // Instant the served corridor-health snapshot was computed; null on a cold cache. Rendered
  // as an honest "as of" stamp beside the chips so counts are never implied to be live-now.
  readonly healthAsOf = signal<string | null>(null);

  // Screen 1: candidate search
  readonly seedInput = signal('');
  readonly loadingCandidates = signal(false);
  readonly candidateError = signal<string | null>(null);
  readonly candidateResponse = signal<ConsolidationCandidateResponse | null>(null);
  readonly selectedSiblingIds = signal<Set<string>>(new Set<string>());

  // Screen 2: plan preview
  readonly loadingPlan = signal(false);
  readonly planError = signal<string | null>(null);
  readonly plan = signal<ConsolidationPlanResponse | null>(null);

  // Screen 3: audit
  readonly recordingAudit = signal(false);
  readonly auditError = signal<string | null>(null);
  readonly auditRecord = signal<ConsolidationAuditRecord | null>(null);
  readonly copyMessage = signal<string | null>(null);

  /** Guards the one-time default auto-seed so a manual search is never re-seeded out from under the user. */
  private autoSeeded = false;

  /** Origin→destination STATE pairs of the configured pilot corridors, used to fold matching live lanes. */
  private pilotStatePairs = new Set<string>();

  /** Guards the one-time default corridor selection across the two progressive reads (health, opportunities). */
  private defaultApplied = false;

  readonly hasSelection = computed(() => this.selectedSiblingIds().size > 0);

  /**
   * True once corridors have loaded but the queue is still empty — i.e. the default auto-seed
   * could not fire because no configured corridor lane has a live open parent right now. Drives
   * an honest in-corridor empty state (banner + picker + footer stay visible) instead of a blank
   * screen. Distinct from an error and from the still-loading state so we never imply a failure
   * where Alvys simply has nothing to plan today.
   */
  readonly corridorReadyNoQueue = computed(
    () =>
      !this.loadingCorridors() &&
      !this.loadingCandidates() &&
      !this.candidateError() &&
      !this.candidateResponse(),
  );

  /** Display label for the selected corridor, e.g. "Laredo → Dallas". Falls back to the pilot lane. */
  readonly selectedCorridorLabel = computed(() => {
    const row = this.corridors().find((r) => r.code === this.selectedCorridor());
    return row ? `${row.originName} → ${row.destinationName}` : 'Laredo → Dallas';
  });

  /**
   * True when the currently-selected picker row is a live lane outside the configured pilot
   * corridor. Drives an honest "Live lane — outside pilot corridor" badge so the dispatcher is
   * never misled into thinking a non-pilot lane is the sanctioned Laredo→Dallas pilot.
   */
  readonly selectedIsOutsidePilot = computed(
    () => this.corridors().find((r) => r.code === this.selectedCorridor())?.outsidePilot ?? false,
  );

  readonly canBuildPlan = computed(
    () => !!this.candidateResponse()?.seed && this.hasSelection() && !this.loadingPlan(),
  );

  /**
   * True when the current walkthrough is driven by an opportunity-sweep live lane. The sweep
   * sources loads with status <c>Delivered</c> from Alvys (see ConsolidationOpportunityService),
   * so these lanes are recent COMPLETED freight replayed as a realistic example — never open,
   * plannable freight. We surface that honestly (badge) and disable the execute-style actions
   * (Generate click card / Save as audit) so a dispatcher can never action a delivered load as
   * though it were live. The pilot-corridor path (seeded from open loads) stays fully actionable.
   */
  readonly isDeliveredExample = computed(() => this.liveLaneMode());

  readonly canRecordAudit = computed(
    () =>
      !!this.plan() &&
      (this.plan()?.blockers.length ?? 0) === 0 &&
      !this.recordingAudit() &&
      !this.isDeliveredExample(),
  );

  /** Generating the sanctioned Alvys click card is disabled on delivered examples (see above). */
  readonly canGenerateClickCard = computed(() => !!this.plan() && !this.isDeliveredExample());

  /**
   * Combined RPM for the plan = combined customer revenue ÷ parent linehaul miles. Null (renders
   * "—") when either input is missing — never guessed. This is the same billing-side ratio the
   * plan-detail economics panel shows.
   */
  readonly combinedRpm = computed<number | null>(() => {
    const p = this.plan();
    if (!p || p.combinedRevenue == null || p.linehaulMiles == null || p.linehaulMiles <= 0) {
      return null;
    }
    return p.combinedRevenue / p.linehaulMiles;
  });

  /**
   * "If sold individually" = the average of each load's own RPM (its own revenue ÷ its own miles).
   * Every input is a real per-load Alvys value; loads missing revenue or miles are skipped rather
   * than zeroed, and the average is null when nothing is computable.
   */
  readonly individualRpm = computed<number | null>(() => {
    const p = this.plan();
    const seed = this.candidateResponse()?.seed;
    if (!p || !seed) return null;
    const rpms: number[] = [];
    if (seed.revenue != null && p.linehaulMiles != null && p.linehaulMiles > 0) {
      rpms.push(seed.revenue / p.linehaulMiles);
    }
    for (const s of p.siblings) {
      if (s.revenue != null && s.loadedMiles != null && s.loadedMiles > 0) {
        rpms.push(s.revenue / s.loadedMiles);
      }
    }
    if (rpms.length === 0) return null;
    return rpms.reduce((a, b) => a + b, 0) / rpms.length;
  });

  /** Incremental revenue the siblings add on the parent's linehaul = combined − parent revenue. */
  readonly projectedUpliftDollars = computed<number | null>(() => {
    const p = this.plan();
    const seed = this.candidateResponse()?.seed;
    if (!p || !seed || p.combinedRevenue == null || seed.revenue == null) return null;
    return p.combinedRevenue - seed.revenue;
  });

  /** RPM uplift vs selling each load on its own, as a whole-percent delta. Null when uncomputable. */
  readonly projectedUpliftRpmPct = computed<number | null>(() => {
    const combined = this.combinedRpm();
    const individual = this.individualRpm();
    if (combined == null || individual == null || individual <= 0) return null;
    return (combined / individual - 1) * 100;
  });

  /** One-line uplift summary for the Current plan panel. Renders only the parts we can compute. */
  readonly upliftText = computed<string>(() => {
    const dollars = this.projectedUpliftDollars();
    const pct = this.projectedUpliftRpmPct();
    const parts: string[] = [];
    if (dollars != null) parts.push(`+${this.formatMoney0(dollars)}`);
    if (pct != null) parts.push(`+${Math.round(pct)}% RPM`);
    if (parts.length === 0) return 'Projected uplift: — (vs individual dispatch)';
    return `Projected uplift: ${parts.join(' · ')} (vs individual dispatch)`;
  });

  ngOnInit(): void {
    // /corridors is static config and returns instantly. Render the picker chips from it ALONE so
    // the tab is usable the moment it opens — the two live Alvys reads (/corridors/health, now
    // server-cached; /opportunities, the "Today's consolidations" sweep) are progressive
    // enhancements layered on afterwards and never block the chips from appearing. Each read is
    // wrapped so one degrading never blanks the picker.
    this.consolidation
      .getCorridors()
      .pipe(catchError(() => of([] as CorridorSummary[])))
      .subscribe((corridors) => {
        const pilotRows: CorridorPickerRow[] = corridors.map((c) => ({
          code: c.code,
          originName: c.origin.name,
          destinationName: c.destination.name,
          // Counts + seed hints arrive with the health snapshot (applyHealth); null = "not yet".
          openLoadCount: null,
          loadedCleanly: false,
          seedLoadId: null,
          seedLoadNumber: null,
          isLiveLane: false,
          outsidePilot: false,
          opportunity: null,
          // Pilot rows have no representative opportunity yet; the live-lane sweep is where
          // per-opportunity uplift comes from. Stays null so the picker renders no chip until
          // there is a real number to show.
          projectedUplift: null,
        }));
        // A live lane whose origin→dest state pair matches a pilot corridor is folded into that
        // pilot row (not shown twice), so remember the pilot corridors' state pairs.
        this.pilotStatePairs = new Set<string>(
          corridors.map(
            (c) => `${(c.origin.state ?? '').toUpperCase()}->${(c.destination.state ?? '').toUpperCase()}`,
          ),
        );
        this.corridors.set(pilotRows);
        this.loadingCorridors.set(false);

        // Progressive enhancement 1: fold live open-load counts + seed hints into the pilot rows
        // as soon as the (server-cached) health snapshot arrives. When a pilot lane already has a
        // workable pair we default-select and auto-seed it here without waiting on the slower
        // opportunity sweep.
        this.consolidation
          .getCorridorHealth()
          .pipe(catchError(() => of({ asOf: null, corridors: [] } as CorridorHealthSnapshot)))
          .subscribe((snapshot) => this.applyHealth(snapshot));

        // Progressive enhancement 2: append live lanes discovered by the opportunity sweep. This
        // is the fallback default-selection path when no pilot lane has a workable pair.
        this.consolidation
          .getOpportunities()
          .pipe(catchError(() => of(null as ConsolidationOpportunitiesResponse | null)))
          .subscribe((opportunities) => this.applyOpportunities(opportunities));
      });
  }

  /**
   * Folds the corridor-health snapshot into the already-rendered pilot rows (open-load counts +
   * seed hints). If a pilot lane has a seed AND at least two open loads it can actually form a
   * consolidation, so we default-select and auto-seed it immediately — no need to wait for the
   * slower opportunity sweep. This mirrors {@link chooseDefaultSelection}'s pilotWithPair rule (a
   * lone open load defers to a live pair, so it is NOT auto-selected here).
   */
  private applyHealth(snapshot: CorridorHealthSnapshot): void {
    this.healthAsOf.set(snapshot.asOf);
    const health = snapshot.corridors;
    const healthByCode = new Map<string, CorridorHealth>(health.map((h) => [h.code, h]));
    const merged = this.corridors().map((r) => {
      if (r.isLiveLane) return r;
      const h = healthByCode.get(r.code);
      if (!h) return r;
      return {
        ...r,
        openLoadCount: h.openLoadCount ?? null,
        loadedCleanly: h.openLoadCount !== null,
        seedLoadId: h.seedLoadId ?? null,
        seedLoadNumber: h.seedLoadNumber ?? null,
      };
    });
    this.corridors.set(merged);

    if (this.defaultApplied) return;
    const pilotWithPair = merged.find(
      (r) => !r.isLiveLane && (r.seedLoadId || r.seedLoadNumber) && (r.openLoadCount ?? 0) >= 2,
    );
    if (pilotWithPair) {
      this.defaultApplied = true;
      this.selectedCorridor.set(pilotWithPair.code);
      this.maybeAutoSeedQueue();
    }
  }

  /**
   * Appends live lanes from the opportunity sweep and, if no default corridor was already applied
   * from the health snapshot, runs the full default-selection ladder (pilot-with-pair → busiest
   * live lane → first pilot honest-empty). Also fills the live footer facts.
   */
  private applyOpportunities(opportunities: ConsolidationOpportunitiesResponse | null): void {
    const liveLaneRows = buildLiveLaneRows(opportunities, this.pilotStatePairs);
    const pilotRows = this.corridors().filter((r) => !r.isLiveLane);
    this.corridors.set([...pilotRows, ...liveLaneRows]);
    this.totalScanned.set(opportunities?.totalScanned ?? null);
    this.generatedAt.set(opportunities?.generatedAt ?? null);
    this.dataSource.set(opportunities?.dataSource ?? null);

    if (this.defaultApplied) return;
    const defaultCode = chooseDefaultSelection(pilotRows, liveLaneRows);
    if (defaultCode) {
      this.defaultApplied = true;
      this.selectedCorridor.set(defaultCode);
      const row = this.corridors().find((r) => r.code === defaultCode);
      if (row?.isLiveLane) this.selectLiveLane(row);
      else this.maybeAutoSeedQueue();
    }
  }

  selectCorridor(code: string): void {
    this.selectedCorridor.set(code);
    // Clear any stale candidates/plan/errors when the corridor changes.
    this.candidateResponse.set(null);
    this.selectedSiblingIds.set(new Set<string>());
    this.plan.set(null);
    this.candidateError.set(null);
    this.planError.set(null);
    this.auditError.set(null);
    this.auditRecord.set(null);
    this.seedInput.set('');

    const row = this.corridors().find((r) => r.code === code);
    if (row?.isLiveLane) {
      // Live lanes drive the walkthrough from a synthesized candidate/plan (no seed search).
      this.selectLiveLane(row);
      return;
    }

    // Pilot corridor: leave live-lane mode and re-seed the newly-selected corridor's queue.
    this.liveLaneMode.set(false);
    this.selectedOpportunity.set(null);
    this.autoSeeded = false;
    this.maybeAutoSeedQueue();
  }

  /**
   * Populates the full walkthrough for a live lane discovered by the opportunity sweep. Every
   * value on-screen comes from the opportunity's real Alvys fields — parent + siblings, revenue,
   * miles, weight — so the plan panel, click card, and audit all reflect live data. The candidate
   * rows carry Lane fit = Good / Timing fit = Good (the sweep groups strictly by same-lane +
   * same-day, so both are true by construction) and Customer = Unknown (consolidation policy tier
   * can't be read client-side — surfaced honestly, never assumed Allowed). Read-only end to end.
   */
  selectLiveLane(row: CorridorPickerRow): void {
    const opp = row.opportunity;
    if (!opp) return;
    this.liveLaneMode.set(true);
    this.selectedOpportunity.set(opp);
    this.candidateError.set(null);
    this.planError.set(null);
    this.auditError.set(null);
    this.auditRecord.set(null);
    this.loadingCandidates.set(false);
    this.loadingPlan.set(false);

    const seed = this.synthesizeSeed(opp.parent, opp.pickupDate, opp.customerName);
    const candidates: ConsolidationCandidate[] = opp.siblings.map((s) =>
      this.synthesizeCandidate(s, opp.parent, row.code),
    );
    this.candidateResponse.set({
      corridorCode: row.code,
      seed,
      candidates,
      scanTruncated: false,
    });

    // All siblings are in-plan by default so the Current plan panel fills in immediately.
    this.selectedSiblingIds.set(new Set(opp.siblings.map((s) => s.loadId)));
    this.plan.set(this.synthesizeLivePlan(opp, seed, row.code));
  }

  /** Builds a minimal LtlLoadSummary from an opportunity load — only the fields the UI reads. */
  private synthesizeSeed(
    load: ConsolidationOpportunityLoad,
    pickupDate: string,
    fallbackCustomer: string,
  ): LtlLoadSummary {
    return {
      id: load.loadId,
      loadNumber: load.loadNumber,
      customerName: load.customerName || fallbackCustomer || null,
      origin: { label: `${load.originCity}, ${load.originState}` },
      destination: { label: `${load.destinationCity}, ${load.destinationState}` },
      scheduledPickupAt: pickupDate,
      weightLbs: load.weightPounds,
      revenue: load.linehaulAmount,
    } as unknown as LtlLoadSummary;
  }

  private synthesizeCandidate(
    load: ConsolidationOpportunityLoad,
    parent: ConsolidationOpportunityLoad,
    corridorCode: string,
  ): ConsolidationCandidate {
    return {
      loadId: load.loadId,
      loadNumber: load.loadNumber,
      customerName: load.customerName,
      originLabel: `${load.originCity}, ${load.originState}`,
      destinationLabel: `${load.destinationCity}, ${load.destinationState}`,
      scheduledPickupAt: undefined,
      revenue: load.linehaulAmount,
      weightLbs: load.weightPounds ?? undefined,
      corridorCode,
      factors: [
        this.laneFit(load, parent),
        {
          name: 'Timing fit',
          fit: 'Good',
          rationale: 'Same pickup day as the parent.',
        },
        {
          name: 'Customer',
          fit: 'Unknown',
          rationale: 'Consolidation policy tier not read here — confirm with the account owner.',
        },
      ],
      isBlocked: false,
      customerTier: 'Unknown',
    };
  }

  /**
   * Honest Lane fit for a live-lane sibling. The opportunity sweep groups strictly by origin
   * STATE + destination STATE (+ pickup day + customer), not by city — so a TX→TX group can mix
   * Laredo→Dallas with Laredo→Houston. We compare the sibling's actual origin/destination cities
   * against the parent's: only a full city+state match is a true same-lane "Good"; a state-only
   * match is surfaced as "Same state — verify lane" (Tight), never silently claimed as Good.
   */
  private laneFit(
    load: ConsolidationOpportunityLoad,
    parent: ConsolidationOpportunityLoad,
  ): ConsolidationFactor {
    const norm = (s: string | null | undefined) => (s ?? '').trim().toUpperCase();
    const sameCity =
      norm(load.originCity) === norm(parent.originCity) &&
      norm(load.destinationCity) === norm(parent.destinationCity);
    const sameState =
      norm(load.originState) === norm(parent.originState) &&
      norm(load.destinationState) === norm(parent.destinationState);
    if (sameCity && sameState) {
      return {
        name: 'Lane fit',
        fit: 'Good',
        rationale: 'Same origin→destination city as the parent (verified from live Alvys loads).',
      };
    }
    if (sameState) {
      return {
        name: 'Lane fit',
        fit: 'Tight',
        rationale:
          'Same origin/destination state as the parent but a different city — verify the lane before consolidating.',
      };
    }
    return {
      name: 'Lane fit',
      fit: 'Unknown',
      rationale: 'Origin/destination differ from the parent — verify the lane before consolidating.',
    };
  }

  /**
   * Synthesizes a plan preview from the opportunity's own server-computed economics. combinedRpm
   * (client computed) = combinedRevenue ÷ linehaulMiles, which equals the opportunity's own
   * combinedRpm; per-load RPMs derive from each sibling's real revenue ÷ miles. clickCard is left
   * blank here — the routed plan-detail screen re-fetches the real click card from live Alvys.
   */
  private synthesizeLivePlan(
    opp: ConsolidationOpportunity,
    seed: LtlLoadSummary,
    corridorCode: string,
  ): ConsolidationPlanResponse {
    return {
      previewId: 'live',
      corridorCode,
      parent: seed,
      siblings: opp.siblings.map((s) => ({
        loadId: s.loadId,
        loadNumber: s.loadNumber,
        customerName: s.customerName,
        originLabel: `${s.originCity}, ${s.originState}`,
        destinationLabel: `${s.destinationCity}, ${s.destinationState}`,
        revenue: s.linehaulAmount,
        loadedMiles: s.miles,
        customerTier: 'Unknown',
        customerPolicySource: 'None',
        cautions: [],
      })),
      combinedRevenue: opp.combinedRevenue,
      linehaulMiles: opp.parentLinehaulMiles,
      clickCard: { plainText: '', tripReferenceValue: '', mainLoadIdReferenceValue: '' },
      blockers: [],
    };
  }

  /**
   * Populate the candidate queue by DEFAULT — no manual seed or app-settings required — so the
   * pilot corridor is live the moment the Consolidate tab opens (matches the approved mockup).
   * Uses the first open load on the corridor's canonical lane (from /corridors/health) as the
   * seed, then auto-selects the first eligible sibling so the Current plan panel fills in too.
   * Runs at most once per corridor selection and never overrides a seed the dispatcher typed.
   */
  private maybeAutoSeedQueue(): void {
    if (this.autoSeeded) return;
    if (this.seedInput().trim() || this.candidateResponse()) return;
    const row = this.corridors().find((r) => r.code === this.selectedCorridor());
    const seed = row?.seedLoadNumber ?? row?.seedLoadId ?? null;
    if (!seed) return;
    this.autoSeeded = true;
    this.seedInput.set(seed);
    this.loadCandidates({ autoSelectFirstSibling: true });
  }

  loadCandidates(opts?: { autoSelectFirstSibling?: boolean }): void {
    const seed = this.seedInput().trim();
    if (!seed) return;
    // A manual seed search is a pilot-corridor query. If a live lane is currently selected, drop
    // out of live-lane mode and target a real pilot corridor code (the live `LIVE::…` pseudo-code
    // is not a valid backend corridor).
    if (this.liveLaneMode() || this.selectedCorridor().startsWith('LIVE::')) {
      this.liveLaneMode.set(false);
      this.selectedOpportunity.set(null);
      const pilot = this.corridors().find((r) => !r.isLiveLane);
      this.selectedCorridor.set(pilot?.code ?? 'LAREDO_TO_DALLAS');
    }
    this.loadingCandidates.set(true);
    this.candidateError.set(null);
    this.candidateResponse.set(null);
    this.selectedSiblingIds.set(new Set<string>());
    this.plan.set(null);
    this.planError.set(null);
    this.auditRecord.set(null);
    this.copyMessage.set(null);

    this.consolidation.getCandidates(seed, this.selectedCorridor()).subscribe({
      next: (response) => {
        this.candidateResponse.set(response);
        this.loadingCandidates.set(false);
        // On the default auto-seed path, pre-select the first eligible sibling so the Current
        // plan economics render immediately — matching the mockup's populated panel. A manual
        // search leaves selection to the dispatcher.
        if (opts?.autoSelectFirstSibling) {
          const first = response.candidates.find((c) => !c.isBlocked);
          if (first) this.toggleSibling(first);
        }
      },
      error: (err) => {
        this.candidateError.set(err?.error?.error ?? err?.message ?? 'Failed to load candidates.');
        this.loadingCandidates.set(false);
      },
    });
  }

  toggleSibling(candidate: ConsolidationCandidate): void {
    if (candidate.isBlocked) return;
    const next = new Set(this.selectedSiblingIds());
    if (next.has(candidate.loadId)) next.delete(candidate.loadId);
    else next.add(candidate.loadId);
    this.selectedSiblingIds.set(next);
    // Clear a stale plan when the selection changes.
    this.plan.set(null);
    this.planError.set(null);
    this.auditRecord.set(null);
    // Auto-build the plan preview so the Current plan economics fill in immediately on
    // selection — the mockup has no separate "Build plan" button. buildPlan() is still a
    // public method (canBuildPlan gates it) for the routed detail flow and specs.
    if (next.size > 0) this.buildPlan();
  }

  isSelected(loadId: string): boolean {
    return this.selectedSiblingIds().has(loadId);
  }

  buildPlan(): void {
    const seed = this.candidateResponse()?.seed;
    if (!seed) return;
    if (this.selectedSiblingIds().size === 0) return;

    // Live lanes are corridor-agnostic; the backend /plan endpoint is region+corridor gated and
    // would return blockers for a non-pilot lane. Re-synthesize the plan client-side from the
    // currently-selected siblings' real Alvys revenue/miles instead — never fabricated.
    if (this.liveLaneMode()) {
      const opp = this.selectedOpportunity();
      if (!opp) return;
      const selected = new Set(this.selectedSiblingIds());
      const siblings = opp.siblings.filter((s) => selected.has(s.loadId));
      const combinedRevenue =
        (opp.parent.linehaulAmount ?? 0) + siblings.reduce((sum, s) => sum + (s.linehaulAmount ?? 0), 0);
      this.plan.set(
        this.synthesizeLivePlan(
          { ...opp, siblings, combinedRevenue },
          seed,
          this.selectedCorridor(),
        ),
      );
      return;
    }

    this.loadingPlan.set(true);
    this.planError.set(null);
    this.plan.set(null);
    this.auditRecord.set(null);

    this.consolidation
      .buildPlan({
        parentLoadId: seed.id,
        siblingLoadIds: Array.from(this.selectedSiblingIds()),
      })
      .subscribe({
        next: (response) => {
          this.plan.set(response);
          this.loadingPlan.set(false);
        },
        error: (err) => {
          this.planError.set(err?.error?.error ?? err?.message ?? 'Failed to build plan preview.');
          this.loadingPlan.set(false);
        },
      });
  }

  recordAudit(): void {
    const seed = this.candidateResponse()?.seed;
    const plan = this.plan();
    if (!seed || !plan) return;
    // Delivered examples are view-only: the opportunity sweep sources already-DELIVERED loads, so
    // there is nothing plannable to record (findings #1/#4). The button is disabled too; this is
    // the defence-in-depth guard so a programmatic call can never persist a delivered "plan".
    if (this.isDeliveredExample()) return;

    this.recordingAudit.set(true);
    this.auditError.set(null);
    this.auditRecord.set(null);

    this.consolidation
      .recordPlanAudit({
        parentLoadId: seed.id,
        siblingLoadIds: plan.siblings.map((s) => s.loadId),
      })
      .subscribe({
        next: (record) => {
          this.auditRecord.set(record);
          this.recordingAudit.set(false);
        },
        error: (err) => {
          this.auditError.set(err?.error?.error ?? err?.message ?? 'Failed to record audit.');
          this.recordingAudit.set(false);
        },
      });
  }

  async copyClickCard(): Promise<void> {
    const text = this.plan()?.clickCard.plainText;
    if (!text) return;
    try {
      if (navigator?.clipboard?.writeText) {
        await navigator.clipboard.writeText(text);
        this.copyMessage.set('Copied. Paste into a note or into Alvys directly.');
      } else {
        this.copyMessage.set('Clipboard not available — select the card and copy manually.');
      }
    } catch {
      this.copyMessage.set('Copy failed — select the card and copy manually.');
    }
  }

  /**
   * Navigates to the routed Plan Detail screen for the current preview. The plan is re-fetched
   * there from live Alvys data via parent/sibling ids carried as query params — nothing is
   * cached across the route boundary, so a direct link (or refresh) still shows live data.
   */
  openPlanDetail(): void {
    const seed = this.candidateResponse()?.seed;
    const p = this.plan();
    if (!seed || !p) return;
    // Delivered examples cannot generate a sanctioned click card (findings #1/#4) — the freight is
    // already delivered, so there is nothing to dispatch. Guard mirrors the disabled button.
    if (this.isDeliveredExample()) return;

    this.router.navigate(['/ltl/consolidate/plan', p.previewId], {
      queryParams: {
        parent: seed.id,
        siblings: p.siblings.map((s) => s.loadId).join(','),
        corridor: this.selectedCorridor(),
      },
    });
  }

  chipClass(fit: ConsolidationFit): string {
    switch (fit) {
      case 'Good':
        return 'chip chip-good';
      case 'Tight':
        return 'chip chip-tight';
      case 'Blocked':
        return 'chip chip-blocked';
      default:
        return 'chip chip-unknown';
    }
  }

  factorFit(candidate: ConsolidationCandidate, name: string): ConsolidationFactor | undefined {
    return candidate.factors.find((f) => f.name === name);
  }

  formatCurrency(value: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    return `$${value.toLocaleString(undefined, {
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    })}`;
  }

  formatNumber(value: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    return value.toLocaleString();
  }

  formatDate(value: string | undefined | null): string {
    if (!value) return '—';
    try {
      const d = new Date(value);
      return d.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
    } catch {
      return value;
    }
  }

  /** Date + time for the corridor-health "as of" stamp (snapshot refreshes every ~2 min). */
  formatDateTime(value: string | undefined | null): string {
    if (!value) return '—';
    try {
      const d = new Date(value);
      return d.toLocaleString(undefined, {
        month: 'short',
        day: 'numeric',
        hour: 'numeric',
        minute: '2-digit',
      });
    } catch {
      return value;
    }
  }

  /** Whole-dollar money for the uplift summary (no cents), e.g. "$1,235". "—" when null. */
  formatMoney0(value: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    return `$${value.toLocaleString(undefined, { maximumFractionDigits: 0 })}`;
  }

  /** RPM rendered as "$1.85 / mi". "—" when null — never guessed. */
  formatRpm(value: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    return `$${value.toFixed(2)} / mi`;
  }

  /**
   * Pallet cell for a load/candidate row (Phase 7.2). Shows the EDI-tender volume-derived estimate
   * badged "(est.)" when a matched tender supplied one; otherwise the honest "— pallets · visual
   * verify". Accepts the minimal shape shared by the seed (LtlLoadSummary) and candidate rows.
   */
  palletCellLabel(load: { ediEnrichment?: LtlEdiEnrichment | null }): string {
    const est = load.ediEnrichment?.palletEstimate;
    return est != null ? `~${this.formatNumber(est)} pallets (est.)` : '— pallets · visual verify';
  }

  /** Tooltip behind {@link palletCellLabel}: the tender-derived math, or the dock-verify caveat. */
  palletCellTitle(load: { ediEnrichment?: LtlEdiEnrichment | null }): string {
    return (
      load.ediEnrichment?.palletBasis ??
      'Pallet count not on the load or any matched tender — visual verify at dock.'
    );
  }

  /** Weight cell preferring the load's own weight, falling back to a matched tender's weight. */
  weightCellLabel(load: { weightLbs?: number | null; ediEnrichment?: LtlEdiEnrichment | null }): string {
    const weight = load.weightLbs ?? load.ediEnrichment?.weightLbs ?? null;
    return weight != null ? `${this.formatNumber(weight)} lb` : '— lb';
  }

  /** True when any dimension on this row came from a matched EDI tender (drives the source badge). */
  ediSourced(load: { ediEnrichment?: LtlEdiEnrichment | null }): boolean {
    return !!load.ediEnrichment;
  }
}
