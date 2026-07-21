# CLAUDE.md — LTL Market-Parity Search, Match, Assign, and Bill

Working instruction set for Claude Code when continuing the Value Truck **LTL Tool
Detection, Planner, and Booker** project. Read this before writing code.

## Product objective

Build the LTL tool into a modern dispatch and revenue-protection workbench that
competes with paid logistics tools doing the same practical job: find available
freight, match it to the best driver/truck/trailer, support an auditable assignment
decision, and help operations/accounting bill accurately.

The product is **not** a raw Alvys grid. It is the decision-support layer on top of
Alvys. The target workflow is **Search → Match → Assign → Bill → Billed**:

1. **Search** live Alvys freight and expose meaningful dispatch worklists.
2. **Match** the load to the best driver/truck/trailer using explainable scoring.
3. **Assign** internally with validation, blockers, warnings, override reasons, and
   audit history.
4. **Bill** effectively by surfacing missing billing data, invoice state, documents,
   POD, exceptions, visibility risks, and accessorial review needs.

The "secret sauce" is converting Alvys operational data into prioritized work,
defensible recommendations, and revenue protection.

## Safety principles (do not violate)

- **Alvys is the ONLY source of truth for operational data.** The LTL tool must never
  ingest load, driver, truck, trailer, customer, invoice, tender, dispatch, visibility,
  or accessorial context from any other system. The only permitted additional inputs
  are **DOT-tier public regulatory APIs** (FMCSA, SAFER, DOT registry, ELD provider APIs
  when a real provider is wired) — and only when absolutely necessary for a specific
  compliance signal. No open-web scraping. No cross-database joins with partner TMSes
  (McLeod, Sage/Pecan stay adjacent, never ingested). No fabricated demo or seed data
  masquerading as live. This rule ranks equal to "credentials are server-side only" and
  "missing data is surfaced not invented" — a PR that introduces a non-Alvys / non-DOT
  data path is blocked regardless of value.
- **Never** expose Alvys credentials to the SPA. Alvys credentials are server-side only.
- **Never** invent missing operational or billing values. Missing data must be
  surfaced as `missing` / `—`, never coerced to `0`, `false`, or "good".
- Keep Alvys writeback **gated and explicit**. Writeback defaults to Disabled and is
  config-gated (recognised non-production sandbox environment + sandbox base URL +
  credentials). Flipping the mode alone can never reach a live/production tenant. The
  UI must state whether an action was pushed to Alvys or recorded internally only.
- **Do not** claim live booking/writeback to Alvys unless the supported write API
  contract is confirmed and implemented safely. Do not create a fake booking path.
  Extending writeback from sandbox to a **production** Alvys tenant is tracked
  separately in `docs/ltl-tool.md` (per-operation contract confirmation, business
  sign-off, and a dedicated production gate) — do not implement production execution
  without a filled-in sign-off row there.
- **Do not** seed fake "live" production data to make demos look real. Fallback/empty
  states are acceptable; fake live data is not.
- **Do not** remove auditability. Assignment and billing decisions need traceable reasons.
- **Do not** put secrets in source, tests, screenshots, docs, or committed env files.
- **Do not** fake HOS, location, lane history, POD, accessorials, or revenue.
- **Do not** replace deterministic business rules with vague "AI" wording.

## Required reads before writing code

**Product / demo:** `README.md`, `docs/LTL_DEMO_RUNBOOK.md`,
`docs/ALVYS_INTEGRATION.md`, `.env.example`

**Backend LTL slice:** `src/LtlTool.Api/Features/Ltl/LtlController.cs`,
`LtlLoadService.cs`, `LtlNormalizationService.cs`, `LtlReadModels.cs`,
`LtlSearchQuery.cs`, `MatchService.cs`, `MatchScoringService.cs`,
`BillingReadinessService.cs`, `WorkflowStageService.cs`,
`Assignment/AssignmentValidationService.cs`, `SavedViews/*`,
`src/LtlTool.Api/Features/Integrations/Alvys/*`

**Frontend LTL workspace:** `web/src/app/features/ltl/ltl-search.ts` / `.html` /
`.css`, `ltl.models.ts`, `ltl.service.ts`, `saved-views.ts`, `*spec.ts`

**Tests / CI:** `src/LtlTool.Api.Tests/Ltl/*`, `.github/workflows/ci.yml`,
`web/package.json`, `LtlTool.sln`

## Current API surface (preserve)

- `GET  /api/ltl/search` — normalized, filtered, sorted, paged loads
- `GET  /api/ltl/loads/{idOrNumber}` — detail view
- `GET  /api/ltl/loads/{idOrNumber}/matches` — ranked, explainable matches
- `GET  /api/ltl/loads/{idOrNumber}/billing-readiness` — billing readiness
- `POST /api/ltl/loads/{idOrNumber}/assign/validate` — validate before assignment
- `POST /api/ltl/loads/{idOrNumber}/assign` — record internal audit decision
- `GET  /api/ltl/loads/{idOrNumber}/assignments` — assignment history
- `POST /api/ltl/assign/validate-batch` — Phase 3: preflight-validate top-N proposed assignments in one call; per-row blocker/warning counts; records nothing, read-only against Alvys.
- `GET  /api/ltl/assignments?user={u}&day={yyyy-MM-dd}&reasonType={reason}` — Phase 3: cross-load assignment audit history, newest first, filterable by recording user / UTC day / typed override reason. Read-only; `AlvysWriteback` stays `NotPerformed`.
- `GET  /api/ltl/billing/worklist` — billing attention list
- `GET  /api/ltl/exceptions` — exception-bearing loads
- `GET  /api/ltl/consolidation/candidates?loadId={id}&corridor={code}` — Phase 1 pilot Laredo→Dallas consolidation candidates (read-only)
- `POST /api/ltl/consolidation/plan` — Phase 1 pilot: build a consolidation plan preview (parent + siblings → click-card content). Read-only; nothing writes to Alvys.
- `POST /api/ltl/consolidation/plan/audit` — Phase 1 pilot: record a plan as an internal audit entry (leadership visibility). Read-only against Alvys.
- `GET  /api/ltl/consolidation/plan/audits?parentLoadId={id}` — Phase 1 pilot: audit history for one parent (or all when parentLoadId omitted).
- `GET  /api/ltl/notifications?max={n}` — Phase 6: recent workflow notifications (newest first) + lifetime count + per-channel config state. Read-only.
- `GET  /api/ltl/notifications/channels` — Phase 6: honest per-channel configuration snapshot (in-app always on; Teams/email config-gated).
- `GET  /api/ltl/reporting/margin-rollup?groupBy={Customer|Rep|Lane}` — read-only margin/exception rollup over the same normalized load set as the billing worklist, aggregated by customer, rep (Alvys id only — no rep-name field exists), or lane (derived from origin/destination). No external BI connection.
- `GET  /api/ltl/reporting/margin-rollup/export?groupBy={Customer|Rep|Lane}` — CSV rendering of the same margin rollup, for external reporting tools (e.g. Power BI's Text/CSV connector) to pull Alvys-derived data directly from this tool. Same auth, same read-only data — only the response shape changes.

Angular `/ltl` provides Search, Billing Worklist, Exceptions, detail drawer,
recommended matches, assignment validation, billing readiness, visibility, saved views.

### Alvys writeback boundary

The Alvys write boundary (`src/LtlTool.Api/Features/Integrations/Alvys/Writeback/*`)
exposes gated write operations through `/api/alvys/ops`. Operations:
`create-load-note`, `tender-accept`, `trip-stop-arrival`, `trip-stop-departure`,
`load-update` (only `OrderNumber` writable today), `trip-assign`, `trip-dispatch`,
`carrier-status-update`. Every write is gateway-validated, idempotency-keyed
(resource identity + payload), recorded to a durable outbox storing **no secrets**, and
executed only when writeback config is fully present. Failed writes surface as
`InternalFailed` → HTTP 502, never a false success. Endpoint paths/verbs/bodies are
grounded in the Alvys API docs (for Public-API operations) or in the observed
endpoint table in `docs/ALVYS_API_DECISIONS.md` (for internal-API operations) — do
not invent routes.

**Public API vs. internal API (2026-07-17 pivot).** The Alvys Public API is
read-only for the LTL tool's Phase 2 consolidation writes (Waypoint creation,
`dispatch_miles` zeroing, `LTL` + `main_load_id` references, `trip-assign`).
Confirmed by Alvys lead engineer Reuben Sheyko — see
[`docs/ALVYS_API_DECISIONS.md`](docs/ALVYS_API_DECISIONS.md) and the transcript
at [`docs/transcripts/2026-07-17-reuben-sync.md`](docs/transcripts/2026-07-17-reuben-sync.md).
All Phase 5 writeback runs through the **Alvys internal API** (the endpoints
the Alvys web UI itself calls), authenticated with an active user's Auth0
session token rather than the client-credentials token the Public API + MCP
use. Internal-API endpoints are **observed, not contracted** — they can change
on Alvys' side without notice. Every internal-endpoint call site needs a
regression test that fails loudly (not silently) when the endpoint returns a
differently-shaped response than the recorded snapshot.

## Write targets

Prefer focused vertical-slice changes over new architecture.

**Backend:** `src/LtlTool.Api/Features/Ltl/*` (search, read models, normalization,
match, scoring, billing readiness, workflow stage, assignment, saved views),
`src/LtlTool.Api/Features/Integrations/Alvys/*` (read-model coverage or safe
sandbox-gated boundaries only), `src/LtlTool.Api.Tests/Ltl/*`.

**Frontend:** `web/src/app/features/ltl/*` (`ltl-search.ts`/`.html`/`.css`,
`ltl.models.ts`, `ltl.service.ts`, `saved-views.ts`, `*spec.ts`). Keep the UI
enterprise-grade: readable, fast, explainable, audit-friendly, uncluttered.

**Docs:** update `README.md`, `docs/LTL_DEMO_RUNBOOK.md`, `docs/ALVYS_INTEGRATION.md`
when behavior changes; add new `docs/*` only when it helps future development or UAT.

## Functional requirements (summary)

- **Search:** behave like a dispatch console, not a static report. Search by load/order/PO
  number, customer, origin, destination, pickup/delivery windows, equipment, status,
  assignment state, billing state, workflow stage, exception state. Filters for
  unassigned, ready-to-bill, missing-billing-data, exceptions, blocked-only, LTL-only.
  Saved views, sortable columns, clear empty/loading/error states, bounded Alvys sweeps
  with honest truncation messaging.
- **Normalization:** Alvys is the system of record. Normalize, don't blindly mirror.
  Flag missing fields; never default to zero/false/good.
- **Match engine:** explainable scoring over equipment compatibility, weight capacity,
  driver readiness/credentials, fleet alignment, geography, equipment/maintenance/OOS
  conflicts, window feasibility, and (only with real data) HOS, history, revenue/RPM.
  Missing data is not-scored, not a penalty unless a business rule blocks. Hard
  disqualifiers cap the label at **Not Recommended**. Labels: Excellent / Good /
  Possible / Risky / Not Recommended. Every recommendation explains itself.
- **Assignment:** validation-first. Blockers disable assignment and return **422**.
  Warnings are overridable only with a stated reason. The audit record stores selected
  driver/truck/trailer, match score/label, warnings, override reason, notes, user,
  timestamp, and Alvys writeback status. UI states internal-only vs pushed-to-Alvys.
- **Billing:** check customer, rate/revenue, POD/docs, weight, mileage, accessorial
  review, exceptions, invoice state, already-invoiced, unpaid-balance risk, visibility
  failures. Badges: Ready to Bill / Missing Rate / Missing POD / Missing Accessorial
  Review / Missing Weight / Customer Review Needed / Exception Blocking Billing /
  Already Invoiced. Worklist is readiness-first, risk-visible, filterable by badge.

## Testing

```bash
dotnet restore
dotnet build
dotnet test

cd web
npm ci
npm run build -- --configuration production
npm test -- --watch=false
```

CI (`.github/workflows/ci.yml`) runs four jobs: **Build & Test API**
(`dotnet test -c Release`), **Verify EF Migrations on SQL Server**
(`Category=SqlServerMigration`), **Build Web** (`npm run build` production), and
**Test Web** (`npm test`, i.e. `ng test --watch=false --browsers=ChromeHeadless`).

If the local environment lacks the .NET SDK or Node, say so in the PR body and rely on
CI. Do not mark API testing complete unless it actually ran.

Cover: search query/filter/sort, normalization of missing values, billing badges and
blockers, match labels and unavailable factors, assignment blockers/warnings,
assignment audit behavior, saved-view persistence, Angular service/query mapping, and
Angular UI behavior for major workbench flows.

## PR expectations

Every PR includes a business summary (CFO/operations language), a technical summary,
UI notes/screens if the frontend changed, explicit Alvys read/write posture, the test
commands run and their result, and known limitations / next required data or API
contracts. Suggested body structure:

```
## Business outcome
## Technical changes
## Alvys posture
## Reads / writes
## Testing
## Known limitations
```

## Acceptance criteria for the next meaningful slice

A strong next PR delivers at least one measurable improvement: (1) a more
operationally useful search page, (2) materially stronger match scoring using real
Alvys signals, (3) assignment validation that catches more real-world bad assignments
while preserving override auditability, (4) billing readiness that catches more revenue
leakage or billing blockers, or (5) UI that makes Search → Match → Assign → Bill easier
for dispatch/accounting leadership. The goal is to make Value Truck faster, more
accurate, and harder to leak revenue — not just to add features.
