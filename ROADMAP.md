# LTL Tool — Roadmap

**Repo:** `vc-for-valuetruck/ltl-tool-detection-planner-and-booker`
**Framing:** decision-support workbench on top of read-only Alvys, moving toward controlled writeback and eventually approved production execution.

> **2026-07-17 architectural update.** The Alvys **Public API** (and the MCP server that sits
> in front of it) is read-only for the LTL tool's use cases — confirmed by Alvys lead engineer
> Reuben Sheyko in the [2026-07-17 sync](docs/transcripts/2026-07-17-reuben-sync.md). All
> Phase 2–5 writes (Waypoint creation, `dispatch_miles` zeroing, references, `trip-assign`)
> must go through the **Alvys internal API** — the endpoints the Alvys web UI itself calls,
> authenticated with an active user's Auth0 session token rather than a client-credentials
> token. Read [`docs/ALVYS_API_DECISIONS.md`](docs/ALVYS_API_DECISIONS.md) before touching
> any writeback code. The existing `AlvysWriteGateway` slice is aimed at the Public API and
> cannot fulfill Phase 2 writes; Phase 5 now covers its replacement.

**Workflow spine:** Search → Match → Assign → Bill → Billed.
**Author:** Joshua Davis · Value Truck + Value Logistics
**Last update:** 2026-07-17 (Reuben sync — Public API is read-only; writes pivot to internal API; anti-failure map row 3p added)

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

1. **Alvys is the ONLY source of truth for operational data.** The tool must never ingest load, driver, truck, trailer, customer, invoice, tender, dispatch, visibility, or accessorial context from any other system. The only permitted additional inputs are **DOT-tier public regulatory APIs** (FMCSA, SAFER, DOT registry, ELD provider APIs when a real provider is wired) — and only when absolutely necessary for a specific compliance signal. No open-web scraping. No partner-TMS reads. No fabricated demo data.
2. Alvys credentials are **server-side only**. Never expose to the SPA, `runtime-config.json`, or any `RUNTIME_*` env.
3. Missing data is surfaced, never invented. Source labels stay honest: **Live Alvys / Derived / Planned / Unavailable**.
4. Writeback is gated and explicit. `Mode` alone can never reach a production tenant — `Environment` must be a recognised non-production label **and** `SandboxBaseUrl` must be a non-production host. Production writeback requires a filled-in row in `docs/ltl-tool.md`.
5. No fake booking path. No fake HOS / GPS / POD / accessorials / revenue.
6. Auditability is preserved on every assignment and billing decision.
7. Deterministic business rules over vague "AI" wording. AI is a signal-extraction layer, not a decision layer.
8. Every PR runs CI before it is treated as stable. UAT workflows stay manual until secrets are confirmed.

---

## 2a. Anti-failure map — the 15 industry-documented ways FTL carriers fail at LTL, and the specific phase that defeats each

Based on the analyst-grade research compiled 2026-07-16 (`ltl-expansion-failure-analysis.md`), every LTL failure mode the industry has documented in the last decade is mapped below to the exact phase(s) of this roadmap that neutralize it. Every future PR must state which failure mode it defeats. If a proposed feature does not defeat a listed failure mode, it does not ship in Phase 0–7 — it goes in the Phase 8+ backlog.

**How to read the table.** Each row lists a failure mode with the industry evidence, the mitigation this roadmap ships, and the phase where it lands. **Speculation is called out.** Failure modes that are cultural / political rather than technical are still on the list because they kill more implementations than tech does.

| # | Industry failure mode | Evidence (from research report) | This roadmap's mitigation | Phase |
|---|---|---|---|---|
| 3a | **TMS mismatch — LTL primitives missing.** Alvys/McLeod/Turvo/Mercury Gate ship no first-class LTL primitives; carriers end up in dummy-load workarounds. | Every documented FTL-to-LTL attempt on non-LTL-native TMSes hits this wall. Our own W1/W2 dummy-load pattern is proof. | Consolidation Planner uses Poornima's sanctioned Alvys pattern (waypoints on parent, zero loaded miles on children, boolean LTL trip reference, main-load id, report AND/OR filter). No dummy loads. Preserves Alvys as sole source of truth. | **Phase 2.5** |
| 3b | **Freight classification and NMFC pricing disputes.** Density/class 50–500, HazMat, stackability, freight-class reclass audits at billing. | NMFC is a paid licensed data layer (NMFTA → SMC3 RateWare). FTL crews have no muscle memory for reclass disputes. | **Deferred:** we do not price LTL from tariffs; Alvys carries the rate on the load. We do surface **piece-count and weight discrepancies** in Phase 4 billing readiness as `Missing Weight` and `Missing Accessorial Review` badges so reclass risk is visible before billing closes. | **Phase 4** (visibility only). Pricing sophistication is out of scope. |
| 3c | **Dimensional data / pallet counts unreliable at intake.** Customers send "6,500 lb, FTL" with no dims 50% of the time. | Verified in the yard visit — Verdef sends weight-only ~50% of the time. Journal of Commerce OS&D data confirms this is industry-wide. | Consolidation Planner degrades gracefully to `Unknown → visual verify at warehouse`. Capacity fit is `Unavailable` when either weight or pallets are missing. Never fabricates dims. | **Phase 2.5** |
| 3d | **Accessorial billing complexity.** Detention, layover, reconsignment, lumper, inside delivery, DSV, hazmat, appointment fees, sort/segregate. Industry estimate: 15–25% of pure-LTL revenue. | Direct quote from Josh at the yard: "LTL makes money with accessorials. If you're not baking those in, you're not making money." Cited industry benchmarks in the research report. | Accessorial Review analyzer runs deterministic rules against `ListTripStopsAsync`, `ListLoadNotesAsync`, `ListLoadDocumentsAsync`. Every candidate cites its Alvys source. Ships a `Accessorial Review` badge on the Billing worklist. | **Phase 3.5** |
| 3e | **Payroll and mileage accounting — double/triple-pay bug.** Multi-stop LTL breaks driver-pay math; McLeod inflates mileage across combined trips. | **This bug has already fired once at Value Truck** ("we paid a driver triple") — caught before payout. Not hypothetical. | (i) Consolidation Planner zeroes loaded miles on children per Poornima's guidance. (ii) Payroll double-pay guard in Phase 4 reads the Alvys trip reference and flags drivers with non-zero mileage on multiple LTL siblings. (iii) Combined-RPM billing view shows real economics across the sibling set. | **Phase 2.5 + Phase 4** |
| 3f | **Customer visibility and consent — seal integrity is a contractual tripwire.** Kroger and Ring fire carriers for unauthorized consolidation. "Once you break a seal, you will not money." | Junior's exact words. Documented industry practice; not paranoia. | (i) Per-customer `AllowConsolidation` policy store (EF-backed), ships empty, defaults to `Unknown → confirm with account owner`. (ii) `SealBreakRequired` red flag blocks any candidate that would need mid-route seal break; consolidation happens only at yard hand-off points (Laredo, Dallas, Phoenix). | **Phase 2.5** |
| 3g | **Rate confirmation vs. actual delivery — multi-BOL/POD reality.** LTL is inherently multi-BOL, multi-POD; FTL crews think one BOL per load. OCR/AP mismatch drives revenue leakage. | Industry benchmarks in the research report on billing leakage from BOL/POD mismatches. | (i) POD-aware billing readiness cross-references `ListLoadDocumentsAsync` for POD types; absent POD blocks Ready-to-Bill. (ii) Phase 6 deterministic OCR extractor on BOL/POD emits discrepancy signals into Phase 3.5 (Accessorial Review), Phase 4 (Billing), and Phase 3 (Assignment audit). Never overwrites Alvys values silently. | **Phase 4 + Phase 6** |
| 3h | **Internal politics — commissions punish consolidation.** Every documented FTL-to-LTL attempt has been killed by commission structure before ops. | Bre at Value Truck (loses commission credit when three brokered loads collapse into one shipment) is the industry-norm blocker, not an anomaly. | The tool cannot fix commission structures. It *can* make the audited value of consolidation legible to leadership: `POST /api/ltl/consolidation/plan/{planId}/audit` records the plan as an internal decision with projected combined revenue. Combined-RPM view (Phase 4) is the counter-signal to "the parent trip's RPM is inflated." Everything ships with a full audit trail so leadership can see who caught the revenue that would otherwise leak. | **Phase 2.5 + Phase 4 (visibility layer only; policy sits outside the tool)** |
| 3i | **Terminal/cross-dock infrastructure gap.** Most FTL carriers lack consolidation warehouses, dock doors, forklift crews. Consolidating "in the wild" is where compliance/insurance/theft risk spikes. | Yellow's $1.88B terminal auction went to LTL incumbents only; no truckload carrier bought in. | Consolidation Planner requires a configured `Ltl:Consolidation:Warehouses` list (Laredo, Dallas, Phoenix). Candidates outside the radius of any configured warehouse are disqualified. `SealBreakRequired` (see 3f) further blocks mid-route consolidation. Value Truck's Dallas 20+ acre / 154-door yard is real terminal capacity; the tool ensures we route through it, not around it. | **Phase 2.5** |
| 3j | **Insurance and cargo liability confusion.** LTL cargo liability priced differently. Broker-vs-carrier liability confusion. Comingling risk. | FMCSA / BMC-32 vs BMC-91 filings; research report cites the confusion pattern. | (i) US-side-only region gate: `Ltl:Consolidation:AllowedRegions = [US]` (default). South-of-border consolidation points disqualified with explicit reason. (ii) Per-customer policy store (see 3f) is also the place a future field for `LiabilityPosture` (broker vs asset) lives. **Tool does not compute liability — it surfaces which regime applies per load, sourced from Alvys.** | **Phase 2.5** |
| 3k | **EDI/integration burden.** LTL customers demand 210/214/990/997 EDI at scale. Yellow's tech debt was famously part of its collapse. | Yellow's 2018–2023 Moody's warnings on tech debt; research report evidence. | **Do not build EDI in the LTL Tool.** Alvys already has EDI auto-accept (see Alvys beta features in Phase 2). The tool calls Alvys for tender/EDI state, does not reimplement. | **Phase 2 (dependency, not build)** |
| 3l | **Freight bill audit and pay (FAB) muscle missing.** FTL shops lack this discipline. AFS Logistics and U.S. Bank Freight Payment Index show billing leakage patterns. | Research report cites AFS Logistics and U.S. Bank data on FTL-to-LTL revenue leakage at billing. | Continuous audit posture: Phase 0 stability tests, Phase 4 combined-RPM view, Phase 4 payroll double-pay guard, Phase 4 days-past-terms, Phase 7 durable history for lane/customer trend audits. Per Poornima's guidance from the yard: **weekly cadence, not annual.** `PayableTimingRisk` signal surfaces before payroll closes. | **Phase 0 + Phase 4 + Phase 7** |
| 3m | **Culture and process — dispatchers think in atoms, LTL requires shipment-planning atoms.** Single-load vs multi-order mental model. | Research report interview data + Reddit r/logistics practitioner threads. | The Consolidate tab **is** the mental-model shift. It exists explicitly as a shipment-planning surface (multiple orders → one movement → multiple deliveries), not a load list. Copy and UI reinforce this: "share trailer," "one linehaul + local delivery," not "combined load." | **Phase 2.5** |
| 3n | **Pricing sophistication gap.** Tariffs, FAK, discount agreements, minimums, dimensional divisors, base rates by lane. FTL is spot/contract-lane; LTL is priced. | SMC3 RateWare / CarrierConnect is paid, proprietary data. Standing up an internal LTL tariff engine is a multi-year build. | **Explicitly out of scope for Phases 0–7.** Value Truck relies on Alvys-carried rate per load. The tool never priced-quotes LTL; it plans consolidations of loads that already have rates. If pricing becomes a business requirement, that is a separate initiative on its own timeline. | **Not planned. Explicit non-goal.** |
| 3o | **Regulatory exposure — reweigh/reclass audits, freight fraud (fictitious pickup), NMFTA membership.** | Research report on fictitious pickup and freight fraud patterns. | (i) Assignment validation (Phase 3) blocks assignments to drivers with expired credentials, terminated status, or missing equipment class — the same guardrails that reduce fictitious-pickup exposure. (ii) Audit trail on every assignment/consolidation decision (Phase 0 exit criterion) provides the paper trail for a real audit. (iii) Post-tender vs pre-tender automation boundary (Phase 6) keeps humans on the fraud-critical relationship interfaces. | **Phase 3 + Phase 6** |
| 3p | **Half-executed writeback plan.** Undocumented internal APIs authenticated with a short-lived user session token can expire mid-plan; a consolidation execution that lands the Waypoint + zero-miles but 401s on `trip-assign` leaves the load in an inconsistent state neither Alvys nor the tool can auto-recover. | 2026-07-17 Reuben sync: session tokens expire, no documented refresh path yet, and the internal API is not contracted. Every internal endpoint is observed behavior that can shift on Alvys' side. | (i) Every Phase 5 plan-execution PR requires a **token-expired failure test** that verifies the tool halts the plan and surfaces a legible error rather than retrying blind. (ii) Post-write reconciliation via Public-API read-back after every internal-API write; mismatch = human review, not retry loop. (iii) Reuben-sanctioned + observed-endpoint discipline logged in `docs/ALVYS_API_DECISIONS.md`. (iv) Consolidation plan-execute is transactional at the plan level: partial success is treated as failure and the audit entry shows exactly which of the 4 writes landed. | **Phase 5** |

### Two failure modes the tool cannot defeat alone

1. **Commission structure (3h).** The tool makes the value of consolidation legible; leadership has to change the incentive structure. Neither engineering nor UX will fix a comp plan.
2. **Pricing sophistication (3n).** Explicit non-goal. Value Truck does not become an LTL carrier via this tool. We optimize consolidation of already-priced loads.

Any future PR that would ship a feature outside this map either fills a new failure-mode row (with sourced evidence) or lands in the Phase 8+ backlog until it does.

---

## 3. Phase roadmap

Phases are ordered by dependency and by dispatcher/billing value, not by calendar. Each phase names the files to inspect/edit, the API/UI surface it touches, UAT-ready scope, what to defer, and risks.

### Phase 0 — Stability (Search → Match → Assign → Bill locked down)

**Goal.** Freeze net-new LTL feature work until **Search → Match → Assign → Bill** is stable end-to-end: green CI on every PR, a clean UAT deploy, no known regressions on the four workflow stages, and guardrail tests around the areas most likely to silently break. Nothing in Phase 1+ starts until this phase lands.

**Why this is now Phase 0.** Direction from Josh (2026-07-15): stability over scope. The tool is only useful when the four-stage workflow runs cleanly on the current merged surface — adding a Consolidation Planner or a Match factor on top of a shaky base makes the tool worse, not better.

**Success criteria (all four stages must clear)**

1. **Search stable.**
   - `/ltl` Search renders in both `Fallback` and `Live` provider modes without console errors.
   - Saved views: create / rename / delete / reload survives; the migration is verified in the `migration-sqlserver` CI job.
   - Sort keeps missing/null values last in both directions (PR #24 behavior — add a regression test in `src/LtlTool.Api.Tests/Ltl/` if one does not exist).
   - Billing badge filter uses signal-safe binding (PR #24 behavior — covered by an Angular spec, not just observed manually).
   - Bounded-sweep truncation message reflects `Ltl:MaxLoadsScanned` honestly.
   - Loading / empty / error / paginated states all render cleanly, including with real Alvys 401 / 429 / 500 responses (already handled server-side; verify the SPA does not crash on the shape).

2. **Match stable.**
   - `GET /api/ltl/loads/{idOrNumber}/matches` returns a deterministic ranking for a fixed input; snapshot-style test locks the ordering and per-factor breakdown.
   - Score formula (`earned / availableMax × 100`) is unit-tested for the exclude-when-Unavailable contract — factors with no data must not appear in the denominator.
   - Hard-disqualifier caps (expired license/medical, terminated driver, over capacity) always produce **Not Recommended** with a stated reason.
   - Equipment availability factor reads `Unavailable` (excluded) when equipment events were not fetched and `Weak` when a maintenance/OOS event overlaps the load window — both paths covered by tests.

3. **Assign stable.**
   - `POST /api/ltl/loads/{idOrNumber}/assign/validate` and `POST /api/ltl/loads/{idOrNumber}/assign` behave identically for the same input; blockers return 422 and never record; warnings record and persist `overrideReason`.
   - Every assignment audit entry carries `AlvysWriteback = NotPerformed` in this phase (nothing else is allowed until Phase 5 sandbox arms).
   - `GET /api/ltl/loads/{idOrNumber}/assignments` returns full history including override reasons.
   - SPA panel label reads **"Not pushed to Alvys"** on every path (assign, validate, drawer, history) — covered by an Angular assertion.

4. **Bill stable.**
   - `GET /api/ltl/billing/worklist?badge=` returns readiness-first ordering; every badge from the current set (Ready to Bill / Missing Rate / Missing POD / Missing Accessorial Review / Missing Weight / Customer Review Needed / Exception Blocking Billing / Already Invoiced) renders and filters correctly.
   - Invoice aging + carrier-payable margin (PR #30) render on load detail and are covered by a test.
   - `Ltl:MaxVisibilityEnriched` bounded-enrichment banner shows on the Exceptions tab so `NotEvaluated` visibility is explained, not silent.

**What repo currently has**
- CI (`.github/workflows/ci.yml`) with **api / migration-sqlserver / web** jobs. Web builds but does not test — documented gap in `CLAUDE.md`.
- UAT workflows (`deploy-ltl-uat.yml`, `provision-ltl-uat-infra.yml`, `verify-ltl-uat-health.yml`) set to **manual** until secrets exist (PR #34).
- App Service UAT pattern rebuilt from freight-dna (PR #31 — replaced the earlier Container Apps path).
- Alvys write boundary architecturally unreachable for production (`AlvysWriteOptions.HasSandboxBaseUrl` rejects the production host).
- Full offline Alvys test doubles (`AlvysTestDoubles.cs`); Alvys tests run with no network.

**What needs to change**

*CI & deploy*
- Add a **web unit-test job** to `ci.yml`: `npm ci && npm test -- --watch=false --browsers=ChromeHeadless`. This closes the known `CLAUDE.md` gap and prevents Angular regressions on `ltl-search.ts` / `saved-views.ts` from slipping in on green PRs.
- Add a **contract-preserving-tests** step that asserts the current LTL API surface still exists exactly as documented in `CLAUDE.md` § "Current API surface (preserve)". Any accidental route/verb rename fails CI.
- Fill in Azure UAT secrets (subscription, resource group, App Service, GHCR, Key Vault, Entra client ids / secret / redirect URI). Flip UAT workflows off manual **only after** one clean end-to-end deploy on `main`.
- Extend `verify-ltl-uat-health.yml` to hit `GET /api/health`, `GET /api/ltl/search` (expect 401 without auth — proves route is mapped + protected), and `GET /api/alvys/ops/status` (same 401 assertion).
- Add a build badge + last-deploy badge to `README.md`.

*Regression guard tests (targeted at silent-failure surfaces)*
- `LtlLoadServiceTests` — lock the null-last sort in both directions.
- `LtlSearchAngularTests` (component spec) — lock the signal-safe billing-badge binding, saved-view chip round-trip, and applied-filter chip removal.
- `MatchScoringServiceTests` — lock the exclude-when-Unavailable denominator contract, hard-disqualifier cap, and label boundaries.
- `AssignmentValidationServiceTests` — every blocker returns 422 and does not record; every warning records and persists override reason.
- `BillingReadinessServiceTests` — every badge path has a test; POD absence blocks Ready-to-Bill.
- `AlvysWriteOptionsTests` — assert that the production host is rejected by `HasSandboxBaseUrl`. This is the writeback safety guarantee and should never regress silently.
- `SavedViewsMigrationTests` — already runs under `Category=SqlServerMigration`; add a round-trip persistence test that survives an app restart.

*Docs*
- Update `docs/LTL_DEMO_RUNBOOK.md` — add a §12 "Stability checklist" section mirroring the four-stage success criteria above.
- Update `README.md` §10 "UAT guidance" to reference the stability checklist.
- Update `CLAUDE.md` — remove the note that CI does not run web tests once the web-test job lands.

*Housekeeping*
- Sweep for any TODO/FIXME/HACK comments in `src/LtlTool.Api/Features/Ltl/*` and `web/src/app/features/ltl/*`; each one is either closed with a linked issue or resolved before Phase 0 exits.
- Confirm no dead code paths remain from the cancelled outbox/idempotency slice (per `docs/LTL_DEMO_RUNBOOK.md` §8).
- Bump dependencies once, run full CI, freeze. Do not bump again mid-phase.

**Exact files to inspect/edit**
- `.github/workflows/ci.yml`, `deploy-ltl-uat.yml`, `provision-ltl-uat-infra.yml`, `verify-ltl-uat-health.yml`.
- `docs/AZURE_HOSTING.md`, `docs/AZURE_UAT_DEPLOY.md`, `docs/LTL_DEMO_RUNBOOK.md`, `README.md`, `CLAUDE.md`, `.env.example`.
- `src/LtlTool.Api/Features/Ltl/*` (targeted test additions only; no behavior changes).
- `src/LtlTool.Api.Tests/Ltl/*` and `src/LtlTool.Api.Tests/Alvys/*` (new tests).
- `web/src/app/features/ltl/*.spec.ts` (new + expanded specs).

**Backend/API impact.** None functional. No new endpoints, no behavior changes. Tests + one new CI job only.
**Frontend impact.** None functional. Specs added; UI untouched.

**UAT-ready scope (definition of "stable")**
- `main` is green on every PR through the full four-job CI matrix (api / migration-sqlserver / web-build / web-test).
- One clean end-to-end deploy has completed to Azure UAT App Service.
- A dispatcher can execute **Search → Match → Assign → Bill** on the deployed URL without hitting a known bug.
- Every guardrail test above is committed and passing.
- No open TODO/FIXME/HACK in the LTL feature paths.
- Alvys posture is provably read-only in every environment (writeback tests pass; production host still architecturally rejected).

**Defer (explicitly)**
- Phase 1 Search hardening.
- Phase 2 Match depth (window feasibility, dispatch preferences).
- Phase 2.5 Consolidation Planner.
- Phase 3 Assign hardening, Phase 3.5 Accessorial Review, Phase 4 Bill leakage.
- Phase 5 sandbox writeback and everything downstream.
- Any dependency bumps beyond the single sweep noted above.

**Risks / dependencies**
- Requires Azure UAT secrets and Entra app registration values. If those are blocked, freeze the deploy portion of this phase and land only the CI + tests + docs portion — do not let "we can't deploy yet" delay the stability tests.
- Adding a web-test job may reveal existing failures. That is the point — they get fixed here, not deferred.
- Do not use this phase as cover for silent refactors. Behavior stays identical; only tests, CI, and docs move.

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

**Phase 1 pilot status (Laredo → Dallas) — delivered + post-Reuben corrections shipped.** The pilot slice landed across PRs #46 (spec + mockups), #47 (candidate service + endpoint), #48 (plan service + endpoint), #49 (audit store + endpoints), #50 (Angular Consolidate tab), and the runbook (§13). Read-only end-to-end; the click card is text the dispatcher pastes into Alvys manually. Corridor is scoped to `LAREDO_TO_DALLAS`, warehouses are Laredo + Dallas 154-door, per-customer policy defaults to `Unknown → confirm with account owner`. See `docs/PILOT_LAREDO_DALLAS.md` and `docs/LTL_DEMO_RUNBOOK.md` §13.

Empirical corrections after the 2026-07-17 Reuben sync + live MCP verification (2026-07-18):

- [#58](https://github.com/vc-for-valuetruck/ltl-tool-detection-planner-and-booker/pull/58) — `AlvysTrip` DTO shape aligned to the wire (`LoadedMileage.Distance.Value`, `TripValue.Amount`, `References[]`).
- [#59](https://github.com/vc-for-valuetruck/ltl-tool-detection-planner-and-booker/pull/59) — **Latent bug fix.** Combined RPM was computed as `Load.CustomerRate / Load.CustomerMileage` (billing math). Now `Trip.TripValue.Amount / Trip.LoadedMileage.Distance.Value` (driver math), per Reuben 33:06 + 15:55. Click card prints both blocks so leadership sees the operator's number and billing keeps its own.
- [#60](https://github.com/vc-for-valuetruck/ltl-tool-detection-planner-and-booker/pull/60) — `CustomerNotesLtlPolicyReader` replaces the hardcoded `CustomerPolicies` list. Reads Alvys customer notes for `LTL_TIER=…` / `LTL_ALLOW=…` markers per decision #10 (SHIPPED), with static-config fallback. Account owners now edit consolidation tiers in Alvys directly instead of via appsettings redeploy.
- [#61](https://github.com/vc-for-valuetruck/ltl-tool-detection-planner-and-booker/pull/61) — **Local demo mode.** `AccessPolicy:Mode=Demo` + `DemoAuthenticationHandler` let the full stack run on a laptop against live va336 reads without Azure or Entra. Unblocks demoing the pilot to leadership in parallel with the pending Azure UAT Contributor role assignment. Runbook: `docs/LOCAL_DEMO.md`.

**Field context (from the yard visit).**
- Junior physically does this today: 2 Verdef loads to Goodyear + a partial to Phoenix on one trailer, delivered by one linehaul driver and two local hourly drivers. One trailer earned $8,000+ instead of $4,000 per load individually.
- The current Alvys workaround is dummy loads (W1/W2, W1 = asset, W2 = flatbed), all-miles-on-parent, zero-miles-on-children, revenue captured on the main trip. Poornima walked Holly through the intended path: an LTL trip reference (boolean + main-load id), assign the same driver/truck/trailer across sibling trips, zero out `loaded miles` on children, and use a report filter to see the combined RPM.
- **Poornima's exact click path in Alvys (canonical instructions from the yard).** (1) Build both loads separately — each gets its own load number. (2) On both orders, assign the **same driver, same truck, same trailer** — do **not** use dummy trucks. (3) On the parent order, use **Add stop → Waypoint** (not Stop) for every sibling delivery, because waypoints do not render as terminal deliveries. (4) On each child order, scroll down to the **dispatch language panel on the left**, find **loaded miles**, and **zero them out** — this prevents the payroll double-pay. (5) Create a **boolean true/false LTL trip reference** on the parent plus a **main-load id** reference on each child pointing at the parent. (6) Use the **report AND/OR filter** on the reference to view combined RPM — individually a child reads `$0/mi` and a parent inflated; combined it reads truthfully (e.g. children $1.90/mi and $3.10/mi collapse to ~$5/mi combined). The tool should generate these exact values and copy them so an operator pastes rather than retypes.
- **Alvys beta feature to reuse, not reinvent.** Alvys already has an EDI auto-accept and a **beta best-driver prediction** ("we do have a thing in beta also where we have predictions for the best driver for a specific trip"). The LTL tool should call this beta prediction where available and layer LTL-specific context (window feasibility, dispatch preferences, consolidation fit) on top — rather than duplicate the ranking logic.
- Pallet count and weight from Verdef are unreliable — often "6,500 lb, FTL" with no piece count. Any suggestion engine must tolerate missing dims, and must show the missing signal honestly (already how `LtlNormalizationService` works).
- Consolidation only works within a bounded window: X hours/miles from the consolidation warehouse, X hours from pickup, along a lane we already run. The rules Junior described: (a) X-miles-from-consolidation-warehouse, (b) X-hours-from-pickup, (c) lane already served.
- **Concrete lane-already-run example (Junior's words).** "We got a customer who wants like 2 pallets a week move from Nashville to Dallas, right? And we have a consolidation warehouse [near Dallas], and we're already running that lane between Nashville and Dallas. Now maybe we grab this few pallets, right, and complete that thing we're already doing." That is exactly the shape the Consolidation Planner must surface — a small partial that fits an existing lane the fleet is already running, ranked above a partial that would open a new lane.
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
- [x] Junior picks a load, sees a ranked list of sibling candidates along the pilot corridor with per-factor chips (Lane / Timing / Customer).
- [x] The plan preview shows: parent trip carries all miles, children zero out (dispatcher does this manually in Alvys per the click card), combined RPM projection is visible, trip-reference and Main-Load-Id values are generated and copyable.
- [x] Customer allow-flag shows honestly: `Allowed` (green), `NotifyRequired` (amber "confirm with account owner"), `Never` (red, blocks selection), `Unknown` (grey — defaults to confirm, never silent-allow).
- [x] Missing pallet/weight is surfaced as "visual verify" rather than blocking the plan.
- [x] The plan is auditable as an internal decision (in-memory audit store, `POST /api/ltl/consolidation/plan/audit`, `GET /api/ltl/consolidation/plan/audits`). No Alvys mutation.
- [ ] Extend beyond the pilot corridor once Phoenix/LA warehouses are configured (Phase 3 corridor expansion).
- [ ] EF-backed audit persistence (swap `InMemoryConsolidationAuditStore` → `EfConsolidationAuditStore` alongside Phase 2 writeback).

**Defer**
- Auto-generating the Alvys trip-reference field via writeback — this rides on Phase 5
  internal-API writeback (specifically the Reuben-sanctioned `add-extended-stop` endpoint
  for Waypoints and the still-to-discover reference-write endpoints; see
  [`docs/ALVYS_API_DECISIONS.md`](docs/ALVYS_API_DECISIONS.md)).
- The current customer-tier check reads a static config file. Replacing it with a
  `CustomerNotesLtlPolicyReader` that parses the Alvys customer's `notes` field (per Reuben
  2026-07-17 sync) is a Phase 3 follow-up, not Phase 1 pilot scope.
- ML-driven lane inference. Start with a config-driven lane list; add history-driven inference only once Phase 7 snapshots exist.
- Cross-border customs sensitivity beyond a boolean flag — the transcript is explicit that consolidation stays USA-side.
- Automated pallet/weight prediction. Never invent capacity numbers.

**Risks / dependencies**
- The whole feature is politically sensitive. Any UI copy that reads as "we consolidated behind Bre's back" is disqualifying. Copy should stay operational: "combine loads," "share trailer," "one linehaul + local delivery."
- **Seal integrity is the real physical risk.** Junior: "once you break a seal, you will not money." DOT can legitimately break a seal at inspection (and the driver carries a replacement); a *carrier*-broken seal kills customer trust. The Consolidation Planner must never propose a plan that would require breaking a customer's seal mid-route — consolidation happens at yard hand-off points (Laredo, Dallas, Phoenix), not in transit. Add a `SealBreakRequired` red flag to any candidate whose route would need it, and block the plan.
- **US-side only for cross-border consolidation.** The customs body / bond / union side is off-limits. Any candidate whose consolidation point sits south of the border is disqualified. Config: `Ltl:Consolidation:AllowedRegions = [US]` (default) with an explicit disqualifier reason on candidates that fail this check.
- Per-customer allow-flag data has to be sourced from someone (Junior + Jason + account owners), not made up. Ship the store empty and default `Unknown` if there's no signed-off value.
- The Alvys trip-reference approach depends on Poornima's guidance being stable. Verify the
  field, verify the report filter, and keep the writeback path off until Phase 5.
- Reuben confirmed 2026-07-17 that references are stringly-typed (`LTL="true"`, not native
  bool). The click-card generator currently formats them as text; keep that shape when the
  writeback lands.
- Combined-RPM computation must use **driver rate ÷ dispatch_miles**, not customer rate. If
  the current `ConsolidationPlanService` uses customer rate, that's a follow-up fix.

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
- **Alvys beta best-driver prediction — call it, do not reinvent it.** Alvys already runs a beta prediction ("the system looks at open orders and drivers and tries to find the best driver for the best trip, right? Cut down deadhead miles and everything like that"). When the LTL tool needs a driver ranking, call the beta prediction first and add LTL-specific factors on top (window feasibility, consolidation fit, hard disqualifiers). If the beta prediction is unavailable, fall back to our factor-based ranking and label the result `AlvysPredictionUnavailable` — do not silently substitute one for the other.
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

### Phase 5 — Alvys writeback via the internal API (Reuben-sanctioned pattern)

**Goal.** Execute the four writes Phase 2 consolidation needs — `add-extended-stop` (Waypoint
creation), `dispatch_miles = 0` on child loads, `LTL` + `main_load_id` trip references,
`trip-assign` — through the Alvys internal API rather than the Public API, following the
discovery + auth pattern Reuben walked through in the [2026-07-17 sync](docs/transcripts/2026-07-17-reuben-sync.md).

> **Pivot from prior plan.** Earlier drafts of this phase framed the work as "turn on
> sandbox writeback via Public API operations." The Public API does not expose any of these
> writes and never will (Reuben, 2026-07-17). The plan below replaces that framing. The
> existing `AlvysWriteGateway` slice under `src/LtlTool.Api/Features/Integrations/Alvys/Writeback/`
> stays on `main` because its safety scaffolding (outbox, idempotency, gateway validation)
> is reusable — only the transport underneath (`AlvysHttpWriteClient`) is replaced.

**Prerequisites (2026-07-17 findings from the Reuben sync).**
- All decisions in this phase must cite [`docs/ALVYS_API_DECISIONS.md`](docs/ALVYS_API_DECISIONS.md). Do not invent behavior; if it isn't in the decisions log, discover it via the Network-tab pattern and add it.
- Value Truck tenant must have the "one driver per trip" setting **OFF**. Confirm before the first `trip-assign` PR.
- A dedicated Alvys user account ("valuetruck-ltl-tool") should back the tool's session token so writes don't attribute to a real dispatcher. Requires Alvys account rep to provision.

**What repo currently has**
- `src/LtlTool.Api/Features/Integrations/Alvys/Writeback/*` — outbox, idempotency, gateway
  validation, and `AlvysHttpWriteClient` (targets the Public API — the transport this phase
  replaces, though the surrounding scaffolding is reused).
- `AlvysWriteOptions` — mode requires recognised `Environment` and non-production `SandboxBaseUrl`; the production host is architecturally rejected.
- Outbox + idempotency keys + `AlvysOperationRecorder`.
- `/api/alvys/ops` posture endpoints + Angular writeback-readiness panel.
- `docs/ltl-tool.md` — production sign-off table (empty).
- `docs/ALVYS_API_DECISIONS.md` — Reuben-sanctioned internal-API pattern, discovered
  endpoint table (starts with `add-extended-stop`), auth model, guardrails.

**What needs to change (internal-API transport, first).**
- Add a new `AlvysInternalTokenProvider` that acquires and refreshes an Alvys user session
  token. Not the same Auth0 client-credentials flow the Public API uses — requires an
  Alvys user login. First-pass implementation may be a manual token-paste for pilot; the
  headless-login helper is a follow-up.
- Add a new `AlvysInternalWriteClient` behind the existing `IAlvysWriteClient` interface,
  targeting endpoints discovered via the Network-tab pattern (starting with
  `add-extended-stop`; add rows to the discovered-endpoints table in
  `docs/ALVYS_API_DECISIONS.md` for each new one).
- Keep the existing `AlvysHttpWriteClient` (Public API) as `AlvysHttpWriteClient_Legacy`
  — do not delete; the safety scaffolding around it is still valid, and it may be useful
  for any future Public-API write operations Alvys releases.
- Implement **post-write reconciliation**: after every internal-API write, re-fetch the
  affected load/trip via the Public API (which is authoritative for reads) and compare
  against the outbox expected state; surface mismatches as reconciliation exceptions,
  never silent passes.
- Track reconciliation state on `AlvysOperationOutbox` (pushed / pushed-but-unconfirmed /
  confirmed) and expose it in the ops panel.
- Wire the consolidation plan-execution path to optionally emit internal-API writes when
  writeback is armed; the audit entry then shows `AlvysWriteback = InternalPushed` (or
  `InternalFailed → <status>`).

**What needs to change (production, gated).**
- Add an independent `AllowInternalApiProduction` flag. Default off. Enable per-operation
  via signed-off rows in `docs/ltl-tool.md`.
- For each production-approved operation, fill in the sign-off row in `docs/ltl-tool.md`
  (Alvys engineer contact = Reuben, contract note = "Reuben-sanctioned internal endpoint,
  captured 2026-07-17", approver, date). Approach cannot be silently promoted; every row
  is an explicit act.
- Add rollback playbooks per operation to `docs/ALVYS_INTEGRATION.md`.
- Add a **token-expired failure test** that verifies the tool surfaces a clean legible
  error to the operator rather than silently retrying or half-executing a plan (this is
  the internal-API's biggest failure surface — anti-failure map **3p**).

**Exact files to inspect/edit**
- `src/LtlTool.Api/Features/Integrations/Alvys/Writeback/AlvysWriteOptions.cs`, `AlvysWriteClient.cs`, `AlvysWriteOperations.cs`, `AlvysOperationOutbox.cs`, `AlvysOperationRecorder.cs`, `AlvysOperationsController.cs`.
- `src/LtlTool.Api/Features/Ltl/Assignment/*` — the internal assignment path is where sandbox writeback plugs in.
- `docs/ltl-tool.md`, `docs/ALVYS_INTEGRATION.md`, `docs/LTL_DEMO_RUNBOOK.md`.

**Backend/API impact.** `POST /api/ltl/consolidation/plan/execute` (new) and
`POST /api/ltl/loads/{idOrNumber}/assign` may now return
`AlvysWriteback = InternalPushed | InternalFailed | InternalReconciliationPending | NotPerformed`.
`POST /api/alvys/ops/execute` becomes reachable under the internal-API config. Every
response continues to state posture honestly.

**Frontend impact.** Ops panel shows reconciliation state; assignment history shows sandbox status per row.

**UAT-ready scope**
- With the internal-API transport armed on the UAT environment only, a dispatcher can
  execute a Laredo → Dallas consolidation plan (Waypoint creation on parent, zero
  `dispatch_miles` on each child, `LTL` + `main_load_id` references, same-driver trip
  assign) and see it land in Alvys production, reconciled via a Public-API read-back, and
  reflected on the audit entry.
- Production remains architecturally unreachable until each operation is signed off in
  `docs/ltl-tool.md`. Reuben-sanctioned or not, the sign-off is still required — it's
  what tells us we've done the discovery, reconciliation test, and rollback playbook.

**Defer**
- Any production writeback until a signed-off row exists per operation.
- Auto-tender-accept until `tender-accept` has its own signed-off row and rollback plan.
- Alvys MCP writes. MCP inherits the Public API's read-only ceiling; when Alvys adds write
  tools that expose the same internal-endpoint capabilities, revisit whether the LTL tool
  should call them instead of hand-rolled internal-API calls.

**Risks / dependencies**
- Do not merge a change that lets a write reach the Alvys internal API without both
  `AllowInternalApiProduction` and a filled sign-off row. This is the single most sensitive
  area of the codebase — treat every PR touching Writeback as a two-reviewer PR at minimum.
- Reconciliation exception handling must not silently retry — a mismatch is a human
  review, not a loop.
- **Internal-API endpoints are observed, not contracted.** They can change on Alvys' side
  without notice. Every internal-endpoint call site needs a regression test that fails
  loudly (not silently) when the endpoint returns a differently-shaped response than the
  recorded snapshot.
- Session-token expiry is the internal API's biggest operational risk. Every plan
  execution must check token validity before firing the first call, and abort cleanly if
  the token is stale rather than half-executing.

---

### Phase 6 — Signal-extraction AI layer (workflow intelligence)

**Goal.** Let notes, emails, call summaries, and transcripts feed structured LTL actions instead of staying buried in free text. AI is a signal extractor; deterministic rules still drive dispatch.

**What repo currently has**
- Load notes (`ListLoadNotesAsync`) and documents (`ListLoadDocumentsAsync`) available server-side.
- No note ingestion layer, no transcript ingestion.

**What needs to change**
- Add an ingestion endpoint (`POST /api/ltl/signals/ingest`) that accepts note/email/transcript text plus source metadata and routes it through an LLM extractor to produce **typed** signals: suggested contacts, role changes, new sites, new lanes, equipment needs, contract signals, competitor weaknesses, project freight, billing risk, service issues, delayed loads, missing docs, regression signals, **accessorial evidence**, **consolidation opportunity mentions**, and **customer visibility posture** ("customer said don't split," "account owner OK'd cross-dock").
- **Field context.** The Phoenix transcript alone contains: a Verdef consolidation workflow, a Bre commission conflict, a payroll triple-pay bug that already fired once, a Sage/Pecan integration owner (Dustin), a McLeod → Alvys migration story, a customer-allow signal for Masonite/Irving, and an unknown East-Coast/NC intermediary competitor that Junior wants researched. The ingestion layer is how conversations like that become structured LTL actions instead of dying in a transcript.
- **Post-tender vs pre-tender automation boundary (from the yard).** Junior asked "what would it take to hire me — the whole process? no people, no discrepancies, no human errors," and the room landed on a clean split. **Pre-tender stays human**: customer relationships and negotiation drive the ~$3.00–$3.10/mi rates the asset fleet earns; automation cannot replicate a dispatcher who calls a broker and gets asked-for money 99% of the time. **Post-tender is automatable end-to-end**: EDI or manual tender → best-driver assignment (Alvys beta + LTL factors) → dispatch → mobile check-in/check-out → paperwork upload → **OCR on BOL/POD** to verify values against the rate confirmation → billing readiness → exception routing back to a human. Human-behavior monitoring (driver overslept, phone dead, broke down) stays human because "we can't predict human behavior." The signal-extraction layer is what turns customer/broker conversations into the pre-tender inputs the post-tender pipeline consumes.
- **East Coast / NC consolidation intermediary competitor.** Poornima referenced a customer she works with that runs an intermediary LTL consolidation business, based on the East Coast (Greensboro / Wilmington area, North Carolina). Josh committed on the call to research it. It should feed into the signals table as a competitive-intel signal type with source metadata, so future account owners and Junior can see how a working reference model operates.
- Every signal is written to a signal table with `sourceType`, `sourceId`, `signalType`, `confidence`, `evidenceQuote`, and — importantly — a **suggested LTL surface**: Search filter, Billing worklist badge, Exception, Match warning, Saved view, Audit note, Next-best-action prompt.
- Wire the signals table to the SPA: Signals panel with accept/reject/reroute; accepted signals mutate the relevant LTL surface (e.g. accepting a "new lane" signal creates or updates a saved view; accepting a "billing risk" signal adds a badge to the load).
- **OCR is the paperwork → billing plumbing piece.** BOL and POD scans are the source of truth for pallet count, case count, and signatures. The signal layer should include a **deterministic OCR extractor** (not an LLM narrative) that reads BOL/POD fields, compares against the rate confirmation and the Alvys load, and emits a **discrepancy signal** with the extracted vs expected values and a confidence score. Discrepancies feed the Accessorial Review analyzer (Phase 3.5), the Billing worklist (Phase 4), and the assignment audit (Phase 3). Never let OCR values overwrite Alvys values silently — always surface as a proposed correction the operator confirms.
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

**Audit cadence guidance (from Poornima at the yard, applies to Phase 0 and Phase 7).** Annual audits are too late: "why wait a full year for the audit?" Prefer continuous or weekly audit surfaces. Two concrete examples she cited that the LTL tool should be able to catch:

- **Toll deduction lag.** A company's toll-deduction workflow ran on the week's ground report, but tolls posted after the report closed — thousands of dollars in owner-operator recoveries were eaten by the company. The equivalent LTL surface: any payable that lags the payout window should surface as a `PayableTimingRisk` signal before payroll closes, not after.
- **IFTA / fuel-tax against wrong truck.** Fuel reported against the wrong truck cannot be un-reported on the field report — it has to be eaten. The equivalent LTL surface: a weekly audit that reconciles fuel records against truck/trailer assignments and flags mismatches early (before IFTA filing), instead of surfacing them at year-end. This is a Phase 7 snapshot-driven surface, not a Phase 4 billing badge.

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

### Adjacent systems (data-source awareness)

The LTL tool does not integrate with these directly today, but decisions in Phase 4 (billing), Phase 5 (writeback), and Phase 6 (signals) touch them. Track ownership and integration posture so we don't reinvent or step on them:

- **McLeod (LoadMaster)** — the predecessor TMS Value Truck is migrating off. It is the source of the mileage-inflation-on-multi-stops behavior that our combined-RPM view (Phase 4) and payroll double-pay guard (Phase 4) are specifically designed to prevent recurring in Alvys. When someone references a McLeod workflow, verify whether the Alvys equivalent exists before replicating.
- **Sage / Pecan (Dustin's domain)** — the payment/AP side. "They push payment straight [to] horses." Owned by Dustin; treat as an adjacent system, not a target. If Phase 4 billing readiness ever needs paid/unpaid confirmation from Sage, coordinate with Dustin before integrating — do not read Sage from the LTL tool directly.
- **Justin's maintenance app** — will eventually touch Alvys per Justin's description. Truck/trailer maintenance events already flow into the LTL tool via `SearchTruckEventsAsync` / `SearchTrailerEventsAsync`, so the LTL surface should not need a direct dependency; note it so the two apps do not race for the same Alvys endpoints.
- **Alvys itself** — already has EDI auto-accept and a **beta best-driver prediction**. Prefer calling Alvys features over rebuilding them (see Phase 2).

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

1. **Phase 0 — Stability.** Search → Match → Assign → Bill locked down: green CI (with the new web-test job), one clean UAT deploy, guardrail tests around every silent-failure surface, no open TODO/FIXME in the LTL feature paths. **Nothing else starts until this phase lands.**
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
- **Phase 0 rewritten as Stability** per Josh's direction (2026-07-15, "stable Search → Match → Assign → Bill"). Freezes net-new work until the current four-stage workflow is provably stable on `main` and deployed UAT.
- **Anti-failure map** added as §2a (2026-07-16) after compiling an analyst-grade research report on why FTL carriers historically fail at LTL expansion. All 15 industry-documented failure modes (3a–3o) are mapped to specific phases of this roadmap that neutralize them, with evidence citations from Journal of Commerce/SJ Consulting, NMFTA, Reuters, ATRI, U.S. Bank Freight Payment Index, and bankruptcy court documents. Two failure modes are explicitly outside what the tool can defeat: commission structure (3h) and pricing sophistication (3n). Every future PR must state which failure mode it defeats or land in Phase 8+ backlog.
- **Nine transcript-signal additions** folded in (2026-07-15) after a deep re-read of the yard-visit recording: (1) Poornima's exact Alvys click path (waypoint mechanic, dispatch-language panel, zero loaded miles, boolean LTL trip reference, AND/OR report filter); (2) the Nashville → Dallas 2-pallets/week lane-already-run example; (3) the post-tender vs pre-tender automation boundary; (4) OCR on BOL/POD as the paperwork → billing plumbing piece; (5) an Adjacent systems section covering McLeod, Sage/Pecan (Dustin), and Justin's maintenance app; (6) audit-cadence examples (toll deduction lag, IFTA / fuel-tax on wrong truck) tied to Phase 0 and Phase 7; (7) seal integrity + US-side-only as first-class Phase 2.5 risks; (8) the East Coast / NC intermediary consolidation competitor as a Phase 6 signal; (9) Alvys beta best-driver prediction as a Phase 2 dependency — call it, do not reinvent it.

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
