# LTL Tool — Roadmap

**Repo:** `vc-for-valuetruck/ltl-tool-detection-planner-and-booker`
**Framing:** decision-support workbench on top of read-only Alvys, moving toward controlled sandbox writeback and eventually approved production writeback.
**Workflow spine:** Search → Match → Assign → Bill → Billed.
**Author:** Joshua Davis · Value Truck + Value Logistics
**Last update:** 2026-07-15

> **Field input incorporated (2026-07-15 Phoenix visit, Junior + Holly + Poornima).** The transcript from the yard visit changes the emphasis: consolidation planning is the actual product, not a match factor. Junior already runs LTL by hand (Verdef cross-border → Laredo → Dallas/Phoenix → local delivery) using dummy loads (W1/W2), zeroed-out child miles, and a manually-maintained trip reference. Bre is the political blocker, not tech. Accessorials are where the money lives. Poornima confirmed the intended Alvys path: **trip reference + LTL boolean flag + zero-out miles on child trips + main-load identifier + filterable report**. The roadmap below reflects that.

This roadmap keeps every capability already shipped on `main` (through PR #34) and reorganizes the remaining work into deliverable phases. It is grounded in the repo — filenames, endpoints, services, options, and workflows all reference real code, not generic freight assumptions.

---

## 1. Where the tool is today

### Shipped and stable

- **Backend LTL slice** (`src/LtlTool.Api/Features/Ltl/`):
  - `LtlController.cs` exposes the full `/api/ltl/*` surface (search, load detail, matches, billing-readiness, assign/validate, assign, assignments, billing/worklist, exceptions).
  - `LtlLoadService.cs` orchestrates Alvys sweeps with bounded scan limits (`Ltl:MaxLoadsScanned`), honest truncation, and detail-path enrichment.
  - `LtlNormalizationService.cs` produces the LTL read model; missing fields are surfaced with `MissingDataFlag` and never coerced to `$0` / `false` / "good".
  - `MatchService.cs` + `MatchScoringService.cs` produce explainable, factor-based match recommendations; unavailable factors are excluded from the denominator; hard disqualifiers cap the label at **Not Recommended**.
  - `BillingReadinessService.cs` returns invoice/POD/visibility-aware readiness with the badge set (Ready to Bill / Missing Rate / Missing POD / Missing Accessorial Review / Missing Weight / Customer Review Needed / Exception Blocking Billing / Already Invoiced).
  - `Assignment/AssignmentValidationService.cs` splits validation into typed **blockers** (422) and overridable **warnings** with stated reason persisted on the audit entry.
  - `WorkflowStageService.cs` + `VisibilityAnalyzer.cs` + `EquipmentEventAnalyzer.cs` layer in stage, tracking failures, and truck/trailer OOS/maintenance risk signals.
  - `SavedViews/*` — durable EF Core saved views backed by SQL Server; migration verified in CI.
  - `Integrations/Alvys/` — full **read-only** client (`IAlvysClient`), OAuth2 client-credentials with cached bearer token (`AlvysTokenProvider`), URL-encoded versioned routes, `Fallback` provider for empty-shape boots.
  - `Integrations/Alvys/Writeback/*` — sandbox-gated write boundary with all eight operations, outbox, idempotency, `Environment` + non-production `SandboxBaseUrl` guard, and `/api/alvys/ops` posture endpoints.

- **Angular workbench** (`web/src/app/features/ltl/`):
  - `ltl-search.ts` / `.html` / `.css` — tabbed console: **Search / Billing / Exceptions**, saved-view chips, expanded filter form, removable filter chips, sortable sticky columns (Miles renders `—` when omitted), loading/empty/error states, pagination.
  - `ltl.service.ts` + `ltl.models.ts` — API mapping.
  - `saved-views.ts` (+ `saved-views.spec.ts`) — durable saved views UI.
  - `alvys-ops-panel.*` — writeback-readiness/posture panel consuming `/api/alvys/ops/*`.
  - `alvys-tenders.service.ts` + models — Tenders board scaffold (from PR #29).
  - Load detail drawer with match breakdown, Equipment availability factor (`Unavailable` / `Weak`), billing readiness badges/risks, tracking visibility, exceptions, and the internal assignment panel labelled "Not pushed to Alvys".

- **Platform / CI / hosting**:
  - `.github/workflows/ci.yml` — three jobs: **api** (`dotnet test -c Release`), **migration-sqlserver** (`Category=SqlServerMigration`), **web** (`npm run build` production).
  - `.github/workflows/deploy-ltl-uat.yml`, `provision-ltl-uat-infra.yml`, `verify-ltl-uat-health.yml` — Azure App Service UAT pipeline (rebuilt on freight-dna pattern in PR #31), manual until secrets are configured (PR #34).
  - Bicep infra under `infra/`; Docker Compose local runtime; `.devcontainer/`.

- **Documentation**:
  - `README.md`, `docs/ALVYS_INTEGRATION.md`, `docs/AZURE_HOSTING.md`, `docs/AZURE_UAT_DEPLOY.md`, `docs/LTL_DEMO_RUNBOOK.md`, `docs/TESTING.md`, `docs/JASON_MEETING_STATUS.md`, `docs/ltl-tool.md` (Alvys **production** writeback decision record with empty sign-off log).
  - `CLAUDE.md` — safety principles and product objective.

### Known-good recent fixes (do not regress)

- Billing Worklist badge filter uses signal-safe binding (PR #24).
- Backend LTL sorting keeps missing/null values **last** in both ascending and descending directions (PR #24).
- PR-based CI restored; CI/build-launch and deploy workflows merged (PRs #25, #26, #31–#34).
- Container Apps deploy replaced by an App Service pattern that mirrors freight-dna (PR #31); UAT workflows made manual until Azure/Entra secrets are set (PR #34).
- Stale Claude docs and temporary verification PRs closed (#22, #27).

### Explicit non-goals in current phase

- No live Alvys writeback. Every ops operation is `Unsupported` for live execution by design.
- No fabricated data. Fallback is empty-shape; missing values render as `—` / `missing`.
- No HOS/ELD, live truck GPS, route optimization, or historical-trend narratives without a real data source.
- No outbox/idempotency writeback slice on `main` (that scope was intentionally cancelled and should not be referenced as shipped).

---

## 2. Guardrails that carry into every phase

These are non-negotiable and copied straight from `CLAUDE.md`:

1. Alvys credentials are **server-side only**. Never expose to the SPA, `runtime-config.json`, or any `RUNTIME_*` env.
2. Missing data is surfaced, never invented. Source labels stay honest: **Live Alvys / Derived / Planned / Unavailable**.
3. Writeback is gated and explicit. `Mode` alone can never reach a production tenant — `Environment` must be a recognised non-production label **and** `SandboxBaseUrl` must be a non-production host. Production writeback requires a filled-in row in `docs/ltl-tool.md`.
4. No fake booking path. No fake HOS / GPS / POD / accessorials / revenue.
5. Auditability is preserved on every assignment and billing decision.
6. Deterministic business rules over vague "AI" wording. AI is a signal-extraction layer, not a decision layer.
7. Every PR runs CI before it is treated as stable. UAT workflows stay manual until secrets are confirmed.

---

## 3. Phase roadmap

Phases are ordered by dependency and by dispatcher/billing value, not by calendar. Each phase names the files to inspect/edit, the API/UI surface it touches, UAT-ready scope, what to defer, and risks.

### Phase 0 — Get the runway green (foundation)

**Goal.** Make sure a fresh clone and every PR path lands stable, deployable, and demonstrable before piling on features.

**What repo currently has**
- CI (`.github/workflows/ci.yml`) with api / migration-sqlserver / web jobs.
- UAT workflows (`deploy-ltl-uat.yml`, `provision-ltl-uat-infra.yml`, `verify-ltl-uat-health.yml`) set to manual until secrets exist.
- `docs/LTL_DEMO_RUNBOOK.md` documenting the Fallback vs Live behaviour.

**What needs to change**
- Fill in Azure UAT secrets (subscription, resource group, app service, GHCR, Key Vault) and flip the UAT workflows off manual once they pass on `main` once.
- Add a **web unit-test job** to `ci.yml` (`npm test -- --watch=false`); today CI builds the web app but does not run web tests — this is documented in `CLAUDE.md` as a known gap.
- Add a lightweight **build badge + last-deploy badge** to the README so the state of `main` is visible at a glance.
- Confirm one Entra app registration + redirect URI for the deployed Web URL and record it in `docs/AZURE_UAT_DEPLOY.md`.
- Decide provider posture per environment (`Fallback` for shared demo, `Live` for dispatch UAT) and document it next to the workflow.

**Exact files to inspect/edit**
- `.github/workflows/ci.yml`, `deploy-ltl-uat.yml`, `provision-ltl-uat-infra.yml`, `verify-ltl-uat-health.yml`.
- `docs/AZURE_HOSTING.md`, `docs/AZURE_UAT_DEPLOY.md`, `README.md`, `.env.example`.

**Backend/API impact.** None functional; wiring only.
**Frontend impact.** None; a web test job is a CI addition.

**UAT-ready scope**
- One dispatcher can reach the deployed Web URL, sign in via Entra, land on `/ltl`, and see Search render (empty in Fallback, live in Live).
- CI badge is green on `main`; UAT deploy has run at least once end-to-end.

**Defer**
- Multi-environment matrix (staging + prod) — one working UAT first.
- Automated smoke tests against the deployed URL beyond the existing `verify-ltl-uat-health.yml`.

**Risks / dependencies**
- Requires Azure subscription + GitHub environment secrets + Entra app registration decisions.
- If secrets remain unavailable, keep workflows manual and lean on local `make build` for demos.

---

### Phase 1 — Search hardening (the daily surface)

**Goal.** Make `/ltl` Search the fastest, most trustworthy dispatcher entry point.

**What repo currently has**
- `web/src/app/features/ltl/ltl-search.ts` / `.html` / `.css` — saved-view chips, expanded filter form, applied-filter chips, sortable sticky columns, badges, loading/empty/error, pagination.
- `LtlController.Search`, `LtlLoadService`, `LtlNormalizationService`, `WorkflowStageService`.
- Signal-safe binding on the Billing badge filter (PR #24) and null-last sorting (PR #24).

**What needs to change**
- **Filter parity across tabs.** Ensure Search, Billing Worklist, and Exceptions can share saved views and workflow-stage filters without divergent DTOs.
- **Blocked-only + Ready-to-Bill toggles** made first-class on Search (they exist in `CLAUDE.md` requirements; verify surface + query parameter mapping).
- **Bounded-sweep truncation messaging** — surface the honest "we scanned N, more may exist" banner tied to `Ltl:MaxLoadsScanned`.
- **Column density + keyboard nav** — arrow-key row navigation, `Enter` to open the drawer; small perf pass on the Angular table to keep 200-row pages snappy.
- **Column persistence** — remember sort/column selection per saved view in EF-backed metadata (not browser storage) so it survives across devices.

**Exact files to inspect/edit**
- `web/src/app/features/ltl/ltl-search.ts` / `.html` / `.css`, `ltl.models.ts`, `ltl.service.ts`.
- `src/LtlTool.Api/Features/Ltl/LtlController.cs`, `LtlLoadService.cs`, `LtlReadModels.cs`, `SavedViews/*`.

**Backend/API impact.** Non-breaking additions to `GET /api/ltl/search` query params; saved-view schema gets optional column/sort metadata (EF migration verified in `migration-sqlserver` job).

**Frontend impact.** Table interaction upgrades; new saved-view metadata round-trip.

**UAT-ready scope**
- A dispatcher can pick a saved view, filter by blocker/ready-to-bill/exceptions, sort by any column with missing values last, page through results, and open the drawer via keyboard.
- Truncation banner is visible and correctly wired to Alvys sweep bounds.

**Defer**
- Full-text search across notes/documents (needs `documents`/`notes` indexing — later phase).
- Column virtualization until a real perf issue emerges.

**Risks / dependencies**
- Saved-view schema changes must ship with an EF migration and a `SqlServerMigration` test; otherwise CI fails.

---

### Phase 2.5 — Consolidation Planner (the actual product)

**Goal.** Give Junior, Holly, and Brian a screen that identifies consolidation candidates from live Alvys orders, lets them merge into a parent trip with zeroed-out child miles and an LTL trip reference, and captures the revenue picture across the combined move — the exact workflow they run today by hand.

**Field context (from the yard visit).**
- Junior physically does this today: 2 Verdef loads to Goodyear + a partial to Phoenix on one trailer, delivered by one linehaul driver and two local hourly drivers. One trailer earned $8,000+ instead of $4,000 per load individually.
- The current Alvys workaround is dummy loads (W1/W2, W1 = asset, W2 = flatbed), all-miles-on-parent, zero-miles-on-children, revenue captured on the main trip. Poornima walked Holly through the intended path: an LTL trip reference (boolean + main-load id), assign the same driver/truck/trailer across sibling trips, zero out `loaded miles` on children, and use a report filter to see the combined RPM.
- Pallet count and weight from Verdef are unreliable — often "6,500 lb, FTL" with no piece count. Any suggestion engine must tolerate missing dims, and must show the missing signal honestly (already how `LtlNormalizationService` works).
- Consolidation only works within a bounded window: X hours/miles from the consolidation warehouse, X hours from pickup, along a lane we already run. The rules Junior described: (a) X-miles-from-consolidation-warehouse, (b) X-hours-from-pickup, (c) lane already served.
- Customer visibility is per-customer and political. Kroger/Ring: never. Verdef: currently the friction point. Masonite/Irving: OK with the right people notified. This must be a per-customer flag, not a global switch.

**What repo currently has**
- Full read-only Alvys surface — `SearchLoadsAsync`, `SearchLocationsAsync`, `SearchTripsAsync`, `ListTripStopsAsync`, `SearchCustomersAsync`, `SearchDispatchPreferencesAsync`, `SearchDriversAsync`, `SearchTrucksAsync`, `SearchTrailersAsync`.
- Normalized LTL read model with honest missing-data flags.
- Match engine already scoring geography + fleet alignment.
- No consolidation primitive yet.

**What needs to change**
- **Consolidation candidate service** — new `ConsolidationPlannerService.cs` under `src/LtlTool.Api/Features/Ltl/`. Given a seed load, propose sibling loads that:
  - originate within `Ltl:Consolidation:MaxOriginRadiusMiles` of a consolidation warehouse (config), OR share a consolidation warehouse (Laredo, Dallas, Phoenix, per `Ltl:Consolidation:Warehouses`).
  - deliver along an overlapping lane within `Ltl:Consolidation:LaneOverlapMiles`.
  - fit within trailer capacity when both are provided (honest fallback when either weight/pallets are `Unavailable`).
  - are for customers where `AllowConsolidation` is true (per-customer config), or flagged for confirmation when unknown.
- **Endpoints** (additive):
  - `GET /api/ltl/consolidation/candidates?loadId=…&maxCandidates=…` — ranked candidates with explainable factors (lane overlap, warehouse proximity, timing feasibility, customer allow-flag, capacity fit, unknown-dim caveat).
  - `POST /api/ltl/consolidation/plan` — accepts a parent + children set, returns the intended Alvys write pattern (trip reference value, which trip carries miles, which trips zero-out, projected combined RPM) as a **preview**. This is read-only in Phase 2.5 — no Alvys mutation.
  - `POST /api/ltl/consolidation/plan/{planId}/audit` — records the plan as an internal audit entry (same pattern as assignment audits), so leadership can see "we planned this consolidation, projected revenue = $X, we did / did not execute in Alvys."
- **Frontend** — new **Consolidate** tab in `web/src/app/features/ltl/` (`consolidation-planner.ts` / `.html` / `.css`):
  - Seed picker (or auto-seed from a selected Search row).
  - Candidate list with per-factor rationale (lane, warehouse, timing, customer allow-flag, capacity, missing-dim caveat).
  - Plan builder with visible parent designation, zeroed-child indicators, combined RPM projection, and the exact Alvys trip-reference value the operator should paste.
  - "Not pushed to Alvys" label everywhere, same posture as assignment.
- **Customer allow-flag surface.** Per-customer configuration store (EF-backed) for `AllowConsolidation`, `RequiresNotification`, `NotificationContact` — surfaced anywhere a consolidation is proposed. Default is `Unknown` → "confirm with account owner" chip, not silent-allow.
- **Match engine extension.** Add a **Consolidation Fit** factor to `MatchScoringService.cs` on the sibling trip's match view so the assign flow warns "this driver is already the parent of a consolidation plan" and prevents double-assignment.

**Exact files to inspect/edit**
- New: `src/LtlTool.Api/Features/Ltl/Consolidation/*` (`ConsolidationPlannerService.cs`, `ConsolidationCandidateService.cs`, `ConsolidationModels.cs`, `ConsolidationController.cs` or extend `LtlController.cs`).
- New: `src/LtlTool.Api/Features/Ltl/Consolidation/CustomerConsolidationPolicyStore.cs` + EF migration.
- Update: `LtlOptions.cs` (`Ltl:Consolidation` section — warehouses list, radius, lane overlap, defaults).
- Update: `MatchService.cs` / `MatchScoringService.cs` to add the Consolidation Fit factor.
- New: `web/src/app/features/ltl/consolidation-planner.ts` / `.html` / `.css`, `consolidation.models.ts`, `consolidation.service.ts`, `*.spec.ts`.
- Update: `app.routes.ts` to add `/ltl/consolidate`.
- Update: `docs/LTL_DEMO_RUNBOOK.md` with the consolidation walkthrough.

**Backend/API impact.** New `/api/ltl/consolidation/*` endpoints. Additive; no changes to existing routes. EF migration for `CustomerConsolidationPolicy` — must be covered by a `SqlServerMigration` test.

**Frontend impact.** New Consolidate tab; new per-customer allow-flag chips on Search rows.

**UAT-ready scope**
- Junior picks a Verdef load, sees a ranked list of sibling candidates that share Laredo consolidation and a Goodyear-area delivery.
- The plan preview shows: parent trip carries all miles, children zero out, combined RPM projection is visible, trip-reference value is generated and copyable.
- Customer allow-flag shows honestly: green for Masonite/Irving with `AllowConsolidation=true`, yellow "confirm with account owner" for `Unknown`, red for customers flagged as never-consolidate.
- Missing pallet/weight is surfaced as "unknown — visual verify at warehouse" rather than blocking the plan.
- The plan is auditable as an internal decision (same posture as assignment). No Alvys mutation yet.

**Defer**
- Auto-generating the Alvys trip-reference field via writeback — this rides on Phase 5 sandbox writeback (specifically `load-update` or a `trip-reference` operation once contract is confirmed).
- ML-driven lane inference. Start with a config-driven lane list; add history-driven inference only once Phase 7 snapshots exist.
- Cross-border customs sensitivity beyond a boolean flag — the transcript is explicit that consolidation stays USA-side.
- Automated pallet/weight prediction. Never invent capacity numbers.

**Risks / dependencies**
- The whole feature is politically sensitive. Any UI copy that reads as "we consolidated behind Bre's back" is disqualifying. Copy should stay operational: "combine loads," "share trailer," "one linehaul + local delivery."
- Per-customer allow-flag data has to be sourced from someone (Junior + Jason + account owners), not made up. Ship the store empty and default `Unknown` if there's no signed-off value.
- The Alvys trip-reference approach depends on Poornima's guidance being stable. Verify the field, verify the report filter, and keep the writeback path off until Phase 5.

---

### Phase 2 — Match engine depth (explainability + real signals)

**Goal.** Make match recommendations defensible enough that dispatch trusts them and can articulate *why* to a customer or driver.

**What repo currently has**
- `MatchService.cs` + `MatchScoringService.cs` — weighted factors, per-factor breakdown, Equipment / Weight capacity / Driver readiness / Fleet alignment / Geography, `Unavailable` (excluded) and `Weak` (from equipment events) states.
- `EquipmentEventAnalyzer.cs` for truck/trailer OOS/maintenance overlap.
- Weights/thresholds in `appsettings.json` under `Ltl:Match`.

**What needs to change**
- **Window feasibility factor** — reject or downgrade matches whose driver/truck cannot reach pickup within the pickup window, using Alvys trip/stop data (`ListTripStopsAsync`) and last-known equipment position. Where no signal exists, factor stays `Unavailable`.
- **Dispatch-preferences signal** — plug `SearchDispatchPreferencesAsync` in as a positive factor for known dispatcher/driver/truck/trailer affinity.
- **Multi-driver / co-driver awareness** — flag pairings that violate constraints.
- **Explainability polish** — every factor shows: raw value, weight, contribution, and one-sentence rationale. Add copy-to-clipboard on the breakdown for internal Slack sharing.
- **Cache warm-up** for equipment events per load window so drawer open feels instant.

**Exact files to inspect/edit**
- `src/LtlTool.Api/Features/Ltl/MatchService.cs`, `MatchScoringService.cs`, `EquipmentEventAnalyzer.cs`, `LtlReadModels.cs`, `LtlOptions.cs`.
- `src/LtlTool.Api/Features/Integrations/Alvys/AlvysClient.cs` (only if new read shapes needed).
- `web/src/app/features/ltl/ltl-search.ts` / `.html` / `.css`, `ltl.models.ts`, `ltl.service.ts`.

**Backend/API impact.** `GET /api/ltl/loads/{idOrNumber}/matches` response gains a `windowFeasibility` and `dispatchPreference` factor; both nullable, both explainable.

**Frontend impact.** Drawer breakdown expands; per-factor rationale line; copy button.

**UAT-ready scope**
- A recommended match without valid pickup-window feasibility drops label to at least **Possible** and states why.
- A load whose dispatcher pairing matches a preference shows a positive factor and rationale.
- Missing data still reads `Unavailable` and stays out of the denominator.

**Defer**
- HOS-based feasibility (needs a real ELD provider).
- Live truck GPS ETA calculations (needs a real telemetry source; do not fake).
- Historical performance factor (needs durable history — Phase 6).

**Risks / dependencies**
- Do not turn `Unavailable` into an implicit penalty; that violates the "not scored" contract in `MatchScoringService`.

---

### Phase 3 — Assign hardening (audit-only, safer)

**Goal.** Catch more bad assignments before they happen, keep the internal audit trail defensible, and prepare cleanly for the sandbox writeback plug-in.

**What repo currently has**
- `Assignment/AssignmentValidationService.cs` — typed blockers/warnings, 422 on blocker, override-reason persistence.
- `POST /api/ltl/loads/{idOrNumber}/assign/validate`, `POST /api/ltl/loads/{idOrNumber}/assign`, `GET /api/ltl/loads/{idOrNumber}/assignments`.
- Panel labelled "Not pushed to Alvys"; each audit entry carries `AlvysWriteback = NotPerformed`.

**What needs to change**
- **New blockers**: driver on active PTO, trailer in bay/maintenance overlap, truck without required equipment class for the load's commodity.
- **New warnings**: RPM below configurable threshold; lane where the driver has never run before; back-to-back window compression.
- **Reason taxonomy** — replace free-text override reason with a short reason list + free-text detail (kept), so audit reports can slice by reason type without NLP.
- **Assignment history page** at `/ltl/assignments` (list + filter by user/day/reason type) using the same audit store.
- **Preflight batch validate** — for a saved view (e.g. Unassigned LTL Today), validate top-N in one shot and highlight the ones with zero blockers.

**Exact files to inspect/edit**
- `src/LtlTool.Api/Features/Ltl/Assignment/AssignmentValidationService.cs`, `LtlController.cs`, `LtlReadModels.cs`, `LtlOptions.cs`.
- `web/src/app/features/ltl/ltl-search.ts` / `.html`, `ltl.service.ts`, `ltl.models.ts` (+ new `assignments.*` page if we split the route).
- `web/src/app/app.routes.ts` for the new `/ltl/assignments` route.

**Backend/API impact.** New optional query `POST /api/ltl/loads/{idOrNumber}/assign/validate?batch=…` or a new `POST /api/ltl/assign/validate-batch`; audit records gain `reasonType` enum.

**Frontend impact.** New route, panel copy tweaks, reason picker.

**UAT-ready scope**
- A dispatcher can click "validate all Unassigned LTL Today" and see per-row blocker/warning counts.
- `/ltl/assignments` shows a filterable audit history.
- Every reason is typed; free-text detail is preserved.

**Defer**
- Any Alvys mutation — this endpoint stays audit-only until Phase 5.
- Assignment "recommend + auto-assign" flows.

**Risks / dependencies**
- Reason taxonomy schema change requires EF migration + `SqlServerMigration` test.

---

### Phase 3.5 — Accessorial review (where LTL revenue actually lives)

**Goal.** Surface accessorial-review candidates on every LTL load. Direct quote from the yard: "LTL makes money with accessorials. If you're not baking those in, you're not making money."

**Field context.**
- Accessorials in scope for LTL: detention, layover, reconsignment, handling fees, DSV, cross-dock, lumper, driver assist, inside delivery, appointment fee, weekend/after-hours delivery. The tool does not need to *price* these — it needs to flag when the underlying evidence is present so the accessorial team can bill them.
- Signal sources already available: `ListTripStopsAsync` (stop dwell times → detention/layover), `ListLoadNotesAsync` (free-text mentions of "held at gate," "reconsigned to," "had to hand-unload"), `ListLoadDocumentsAsync` (extra BOLs → reconsignment, POD notes), visibility history (arrival vs departure gap).
- Bre's commission concern is real: when the accessorial team catches revenue that would otherwise be lost, the audit trail has to make it obvious who caught it.

**What needs to change**
- **`AccessorialReviewAnalyzer.cs`** (new, sibling to `EquipmentEventAnalyzer.cs`) — deterministic rules first:
  - Detention: `dwell > customer_free_time_minutes` at any stop → detention candidate with hours over.
  - Layover: dwell > 24h → layover candidate.
  - Reconsignment: any stop added after tender or `stop.appointmentChanged=true` → reconsignment candidate.
  - Handling / lumper / inside / weekend: note-text keyword match (deterministic dictionary, not LLM) with the matched quote surfaced as evidence.
- **Badges on Billing tab.** `Accessorial Review` badge (already in the badge set from `CLAUDE.md`) becomes populated. Multiple sub-reasons per load allowed.
- **Endpoint.** `GET /api/ltl/loads/{idOrNumber}/accessorial-review` — returns typed candidates with evidence (stop id + dwell, note id + quote, document id + type). Every candidate cites its Alvys source.
- **Signal handoff.** When Phase 6 (signal-extraction AI layer) ships, LLM-derived accessorial signals feed into the same table with `confidence` and `evidenceQuote` — but deterministic rules always run first.

**Exact files to inspect/edit**
- New: `src/LtlTool.Api/Features/Ltl/AccessorialReviewAnalyzer.cs`.
- Update: `BillingReadinessService.cs` to consume the analyzer output for the `Accessorial Review` badge.
- Update: `LtlController.cs` for the new endpoint.
- Update: `web/src/app/features/ltl/ltl-search.ts` / `.html` for badge + drawer section; `ltl.models.ts`, `ltl.service.ts`.

**UAT-ready scope**
- A delivered Verdef load with 6h dwell at the consignee shows "Detention — 3h over free time (evidence: stop 2, arrived 10:00, departed 16:00, customer free time = 3h)."
- A load with a note containing "had to unload with pallet jack, no dock" shows "Handling / lumper review — evidence: load note NC0034."
- Nothing invented. Every candidate cites a real Alvys record id.

**Defer**
- Dollar-value calculation of the accessorial (business owns tariff data).
- Automated invoice line-item creation in Alvys (Phase 5 sandbox writeback, needs `invoice-update` contract).

**Risks / dependencies**
- Customer free-time thresholds must live in per-customer config, not be invented per load. Default `Unknown` when unset, and flag "can't evaluate detention — customer free time not configured" instead of assuming a number.

---

### Phase 4 — Bill readiness that catches leakage

**Goal.** Move billing readiness from "flag missing" to "prevent revenue leakage and cash delay".

**What repo currently has**
- `BillingReadinessService.cs` and `/api/ltl/loads/{idOrNumber}/billing-readiness`.
- Billing Worklist tab (`/api/ltl/billing/worklist?badge=`), badge set complete.
- PR #30 shipped invoice aging + carrier-payable gross margin signals.
- Detail drawer shows already-invoiced + unpaid-balance risks, plus tracking visibility as blocking risks.
- Note: `/api/ltl/exceptions` enriches only the first `Ltl:MaxVisibilityEnriched` (default **25**) loads with visibility history; visibility-only failures beyond that appear on the load detail path only.

**What needs to change**
- **POD-aware readiness** — cross-reference `ListLoadDocumentsAsync` for POD document types; when absent, promote the Missing POD badge and block Ready to Bill.
- **Accessorial review** — pull load notes and stop timing (`ListTripStopsAsync`) to flag detention, layover, and reconsignment candidates; do not compute dollars, just flag reviews needed.
- **Cash-delay risk** — combine invoice aging (PR #30) with unpaid-balance sign to compute a **days-past-terms** risk badge; keep customer terms configurable in `Ltl:Billing`.
- **Combined-RPM billing view.** For consolidation-audited loads (Phase 2.5), the billing detail shows the combined revenue and combined miles across the plan, not just the parent trip's inflated RPM. This is the counter-signal Junior needs when Bre pushes back — "one truck earned $12,000 across three customers" is the story, not "the parent trip's RPM is $8/mile."
- **Payroll double-pay guard.** Read the Alvys trip-reference field and flag when a driver appears on multiple LTL sibling trips with non-zero mileage on more than one — this is the exact bug the yard walkthrough described ("we paid a driver triple") and Poornima's triple-fail-safe recommendation.
- **Batched worklist actions** — mark N loads as "Customer review requested" or "Accessorial reviewed" in one action, all persisted in the audit store, none pushed to Alvys.
- **Bounded-enrichment banner** — surface the `MaxVisibilityEnriched` cap in the Exceptions tab so users know why some loads have `NotEvaluated` visibility.

**Exact files to inspect/edit**
- `src/LtlTool.Api/Features/Ltl/BillingReadinessService.cs`, `VisibilityAnalyzer.cs`, `LtlController.cs`, `LtlReadModels.cs`, `LtlOptions.cs`.
- `src/LtlTool.Api/Features/Integrations/Alvys/AlvysClient.cs` if any new read call is required (documents/notes/stops already available).
- `web/src/app/features/ltl/ltl-search.ts` / `.html`, `ltl.models.ts`, `ltl.service.ts`.

**Backend/API impact.** `/api/ltl/billing/worklist` and `/api/ltl/loads/{idOrNumber}/billing-readiness` gain new badges (`POD Missing`, `Accessorial Review`, `Days Past Terms`). Additive, non-breaking.

**Frontend impact.** New badge chips + Worklist bulk-action row; Exceptions banner for enrichment cap.

**UAT-ready scope**
- Billing worklist lists loads sorted readiness-first with the new badges, filterable by any badge.
- POD-missing on a delivered load blocks Ready to Bill and shows the exact missing document type.
- Days-past-terms lights up when appropriate, using customer terms from config.

**Defer**
- Auto-invoice creation in Alvys (writeback slice).
- Accessorial dollar calculation (business rule ownership isn't ours to define alone).

**Risks / dependencies**
- Customer terms need to live in config (`Ltl:Billing:CustomerTerms`) rather than being invented per load.
- Document type mapping must be defensive — if the Alvys type name is missing, flag "unknown document type" instead of assuming POD.

---

### Phase 5 — Sandbox writeback, then approved production writeback

**Goal.** Move the Alvys write boundary from **Disabled** in every real environment to **Sandbox** in a recognised non-production tenant, then gate production per-operation via the sign-off log in `docs/ltl-tool.md`.

**What repo currently has**
- `src/LtlTool.Api/Features/Integrations/Alvys/Writeback/*` — all eight operations, `AlvysHttpWriteClient` with real HTTP + bearer + `If-Match` + status-only logging.
- `AlvysWriteOptions` — mode requires recognised `Environment` and non-production `SandboxBaseUrl`; the production host is architecturally rejected.
- Outbox + idempotency keys + `AlvysOperationRecorder`.
- `/api/alvys/ops` posture endpoints + Angular writeback-readiness panel.
- `docs/ltl-tool.md` — production sign-off table (empty).

**What needs to change (sandbox first)**
- Provision a recognised sandbox tenant and set `ALVYS_WRITEBACK_MODE=Sandbox`, `ALVYS_WRITEBACK_ENVIRONMENT=sandbox|uat|staging|test`, `ALVYS_WRITEBACK_SANDBOX_BASE_URL=<non-prod host>` in the UAT environment only.
- Implement **post-write reconciliation** (already scoped in `docs/ltl-tool.md`): after every write, re-fetch the resource and compare against the outbox expected state; surface mismatches as reconciliation exceptions, never silent passes.
- Track reconciliation state on `AlvysOperationOutbox` (pushed / pushed-but-unconfirmed / confirmed) and expose it in the ops panel.
- Wire the internal assignment path to optionally emit a sandbox `trip-assign` / `trip-dispatch` when the sandbox is armed; the audit entry then shows `AlvysWriteback = SandboxPushed` (or `SandboxFailed → 502`).

**What needs to change (production, gated)**
- Add an independent `AllowProduction` flag whose truthiness requires `SandboxBaseUrl` to equal the real production host (inverting today's rejection). Never a side-effect of relaxing sandbox checks.
- For each production-approved operation, fill in the sign-off row in `docs/ltl-tool.md` (contract link, approver, date, gate implemented). No production execution for any operation without a filled row.
- Add rollback playbooks per operation to `docs/ALVYS_INTEGRATION.md`.

**Exact files to inspect/edit**
- `src/LtlTool.Api/Features/Integrations/Alvys/Writeback/AlvysWriteOptions.cs`, `AlvysWriteClient.cs`, `AlvysWriteOperations.cs`, `AlvysOperationOutbox.cs`, `AlvysOperationRecorder.cs`, `AlvysOperationsController.cs`.
- `src/LtlTool.Api/Features/Ltl/Assignment/*` — the internal assignment path is where sandbox writeback plugs in.
- `docs/ltl-tool.md`, `docs/ALVYS_INTEGRATION.md`, `docs/LTL_DEMO_RUNBOOK.md`.

**Backend/API impact.** `POST /api/ltl/loads/{idOrNumber}/assign` may now return `AlvysWriteback = SandboxPushed | SandboxFailed | NotPerformed`. `POST /api/alvys/ops/execute` becomes reachable under sandbox config. Every response continues to state posture honestly.

**Frontend impact.** Ops panel shows reconciliation state; assignment history shows sandbox status per row.

**UAT-ready scope**
- With sandbox armed on the UAT environment only, a dispatcher can push `trip-assign` and see it land in the sandbox tenant, reconciled, and reflected on the audit entry.
- Production remains architecturally unreachable until a specific operation is signed off.

**Defer**
- Any production writeback until a signed-off row exists per operation.
- Auto-tender-accept until `tender-accept` has its own signed-off row and rollback plan.

**Risks / dependencies**
- Do not merge a change that lets a write reach `integrations.alvys.com` without both `AllowProduction` and a filled sign-off row. This is the single most sensitive area of the codebase — treat every PR touching Writeback as a two-reviewer PR at minimum.
- Reconciliation exception handling must not silently retry — a mismatch is a human review, not a loop.

---

### Phase 6 — Signal-extraction AI layer (workflow intelligence)

**Goal.** Let notes, emails, call summaries, and transcripts feed structured LTL actions instead of staying buried in free text. AI is a signal extractor; deterministic rules still drive dispatch.

**What repo currently has**
- Load notes (`ListLoadNotesAsync`) and documents (`ListLoadDocumentsAsync`) available server-side.
- No note ingestion layer, no transcript ingestion.

**What needs to change**
- Add an ingestion endpoint (`POST /api/ltl/signals/ingest`) that accepts note/email/transcript text plus source metadata and routes it through an LLM extractor to produce **typed** signals: suggested contacts, role changes, new sites, new lanes, equipment needs, contract signals, competitor weaknesses, project freight, billing risk, service issues, delayed loads, missing docs, regression signals, **accessorial evidence**, **consolidation opportunity mentions**, and **customer visibility posture** ("customer said don't split," "account owner OK'd cross-dock").
- **Field context.** The Phoenix transcript alone contains: a Verdef consolidation workflow, a Bre commission conflict, a payroll triple-pay bug that already fired once, a Sage/Pecan integration owner (Jason), a McLeod → Alvys migration story, a customer-allow signal for Masonite/Irving, and an unknown East-Coast/NC intermediary competitor that Junior wants researched. The ingestion layer is how conversations like that become structured LTL actions instead of dying in a transcript.
- Every signal is written to a signal table with `sourceType`, `sourceId`, `signalType`, `confidence`, `evidenceQuote`, and — importantly — a **suggested LTL surface**: Search filter, Billing worklist badge, Exception, Match warning, Saved view, Audit note, Next-best-action prompt.
- Wire the signals table to the SPA: Signals panel with accept/reject/reroute; accepted signals mutate the relevant LTL surface (e.g. accepting a "new lane" signal creates or updates a saved view; accepting a "billing risk" signal adds a badge to the load).
- Never let the LLM assert numbers or availability — text-only signals; anything numeric must come from Alvys or config.

**Exact files to inspect/edit**
- New: `src/LtlTool.Api/Features/Ltl/Signals/*` (controller, service, extractor abstraction, models).
- Update: `LtlController.cs`, `LtlReadModels.cs`, `web/src/app/features/ltl/*`, `app.routes.ts` (Signals panel).

**Backend/API impact.** New `/api/ltl/signals/*` endpoints; no changes to existing routes.

**Frontend impact.** New Signals panel; opt-in surfacing on Search/Billing/Exceptions.

**UAT-ready scope**
- A dispatcher can paste a customer call transcript, get typed signals with evidence quotes, and accept the "new lane" signal into a saved view.
- No fabricated dollar/weight/mileage values from AI; all such values still come from Alvys.

**Defer**
- Automated ingestion from Outlook/Teams until connectors are approved.
- AI-drafted assignment notes until the reason taxonomy from Phase 3 is stable.

**Risks / dependencies**
- Evidence quote is mandatory — no signal without a source snippet, otherwise audit traceability is broken.
- LLM prompt lives in code + config; no drift into vague wording.

---

### Phase 7 — Durable history + trends (only after the source is real)

**Goal.** Give dispatch a defensible historical view without inventing history.

**What repo currently has**
- No durable history store for Alvys-derived signals.
- Real-time Alvys reads only; no lane/driver/customer trend data.

**What needs to change**
- Add a durable **snapshot store** (EF Core) that captures selected read-model fields per day/load: assignment decisions, billing readiness at delivery, match label chosen, override reasons, exceptions. Never a full Alvys mirror — only what the LTL tool decided or observed.
- Expose trends via `GET /api/ltl/trends/*`: assignment overrides by reason type, billing readiness aging, exception recurrence per customer/lane.
- Add a Trends tab to `/ltl` that reads from snapshots, clearly labelled "Value Truck history — not an Alvys mirror".

**Exact files to inspect/edit**
- New: `src/LtlTool.Api/Features/Ltl/History/*`.
- Update: `AppDbContext.cs`, `LtlController.cs`, `web/src/app/features/ltl/*`, `app.routes.ts`.

**Backend/API impact.** New endpoints; new EF migration; new `SqlServerMigration` test coverage.

**Frontend impact.** New Trends tab.

**UAT-ready scope**
- After a week of use, Trends shows a truthful, sourced view of Value Truck's own LTL activity.

**Defer**
- Any "AI-coached" narrative on top of trends until the underlying signal is structured.
- Cross-tenant benchmarking.

**Risks / dependencies**
- Snapshot cadence and retention must be config-driven; storage cost matters at scale.
- Never join snapshots with fabricated Alvys history.

---

## 4. Cross-phase workstreams

These run in parallel, not on their own timeline.

### Testing

- Every phase adds targeted tests in `src/LtlTool.Api.Tests/Ltl/*` and Angular specs alongside the feature. See `docs/TESTING.md`.
- CI must stay green — three-job matrix today, plus the web unit-test job added in Phase 0.
- API tests must run offline; Alvys is faked via `AlvysTestDoubles.cs`.

### Docs

- Update `README.md` §11 whenever the LTL surface changes.
- Update `docs/LTL_DEMO_RUNBOOK.md` whenever demo behaviour changes.
- `docs/ALVYS_INTEGRATION.md` is the read/write contract source of truth; update it before or with the code.
- `docs/ltl-tool.md` — the ONLY place production writeback approvals live.

### Observability

- Status-only logging in writeback (never bodies, never secrets) is already correct; carry that pattern to new integrations.
- Add structured logs and light metrics on Alvys sweep counts, match durations, billing readiness evaluations, and assignment blockers hit — enough to answer "is the tool actually helping".

### Security

- `AllowedEmailDomain` policy on every LTL endpoint.
- Entra app registrations reviewed each time a new environment appears.
- Key Vault for every secret in Azure; no secrets in repo, tests, screenshots, or committed env files.

---

## 5. Suggested near-term sequence

Re-ordered after the Phoenix yard visit — Junior/Holly/Brian are the actual users, and their workflow is consolidation, not just search. Order matters more than timing.

1. **Phase 0** — CI web test job, Azure UAT secrets, one clean deploy on `main`. Nothing else ships smoothly until this is done.
2. **Phase 1** — Search hardening (workflow-stage filter parity, truncation banner, keyboard nav, column persistence). Highest daily-use return per hour of dev.
3. **Phase 2.5** — **Consolidation Planner.** This is now the top product priority. It is the workflow Junior actually runs; the tool becomes real to him the day this ships. Uses only read-only Alvys signals + internal audit — no writeback risk.
4. **Phase 2** — Match engine depth (window feasibility + dispatch preferences). Feeds Phase 2.5 and sharpens the "explainable" claim.
5. **Phase 3.5** — Accessorial review analyzer. Where the LTL revenue actually lives; hits the "you can't leak this" story that lands with leadership.
6. **Phase 3** — Assign hardening (new blockers/warnings including consolidation-parent double-assignment guard, reason taxonomy, batch validate).
7. **Phase 4** — Billing leakage catches (POD-aware, days-past-terms, combined-RPM view for consolidations, payroll double-pay guard).
8. **Phase 5 (sandbox only)** — Arm writeback in UAT sandbox, add reconciliation. First real writeback candidate is likely `create-load-note` (lowest blast radius); consolidation trip-reference writeback comes after Alvys contract is confirmed. Do NOT touch production writeback yet.
9. **Phase 6** — Signal-extraction AI layer, once the surfaces it needs to feed are stable. Prioritize the accessorial and consolidation-opportunity signals first — they map cleanly to Phase 2.5 and 3.5 surfaces.
10. **Phase 5 (production, per-operation)** — Only after each operation has a signed-off row in `docs/ltl-tool.md`, an `AllowProduction` gate in code, and a rollback plan.
11. **Phase 7** — Durable history + trends, once real Value Truck usage has produced real snapshots. Junior's "how many of this lane did we run last year" question lives here.

---

## 5a. What the Phoenix visit changed vs. the previous roadmap

A short changelog so future readers know why the sequence looks the way it does:

- Added **Phase 2.5 (Consolidation Planner)** as top product priority. Was implicit before; is now explicit and central.
- Added **Phase 3.5 (Accessorial review)** with deterministic-first rules. Was buried inside Phase 4; promoted because "accessorials are where LTL revenue lives" — Junior's words.
- Added **combined-RPM billing view** and **payroll double-pay guard** to Phase 4. Both are direct responses to the "we paid a driver triple" incident and the McLeod mileage-inflation problem.
- Added **per-customer `AllowConsolidation` policy store**. Not a global flag; not invented. Ships empty and defaults to `Unknown → confirm with account owner`.
- Added **consolidation opportunity, accessorial evidence, and customer-visibility posture** as first-class Phase 6 signal types.
- Reframed the demo audience: it is Junior + Holly + Brian + Jason, not just Jason. The `docs/LTL_DEMO_RUNBOOK.md` should get a Consolidate walkthrough before the next demo.

Anything that would violate the guardrails in section 2 is not on the roadmap regardless of value.

---

## 6. What is intentionally NOT on the roadmap

Direct restatement of the deferred items from `docs/LTL_DEMO_RUNBOOK.md` and `CLAUDE.md`, so no one adds them by mistake:

- Live Alvys mutation / writeback outside the gated Phase 5 path.
- Fake HOS / ELD / GPS / POD / accessorials / revenue values.
- A raw Alvys grid replacing the workbench.
- AI narratives layered on top of missing or unstructured data.
- Historical trends without a durable, honest snapshot source.
- Route optimization or telemetry until a real provider is wired.
- Cross-tenant benchmarking or comparative dashboards.

---

## 7. Definition of "UAT-ready" (unchanged from `README.md` §10)

A slice is UAT-ready when a dispatcher can:

- Search real or honestly-labeled fallback loads.
- Identify missing operational/billing data.
- View billing readiness and exceptions.
- See explainable match recommendations.
- Validate and stage an assignment internally.
- View audit/history.
- Understand whether data is **Live Alvys / Derived / Planned / Unavailable**.

Every phase in this roadmap ships when it hits that bar — not before.
