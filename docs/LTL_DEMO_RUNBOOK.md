# LTL Tool — Demo Runbook

Operator-facing runbook for the LTL Detection, Planner & Booker demo. Scope is frozen
to current `main` (through PR #18). **This is a decision-support demo, not a booking
demo: nothing is written back to Alvys in this phase.**

---

## 1. Objective & audience

**Objective.** Show how a Value Truck dispatcher moves an LTL load through
**Search → Match → Assign → Bill** in one workbench, with every recommendation
*explained* and every missing data point *surfaced rather than invented*.

**Audience.** Dispatch / operations stakeholders and product reviewers evaluating the
first product slice on top of the read-only Alvys integration.

**The one-sentence framing.** "We turn live Alvys load data into explainable match
recommendations, billing-readiness signals, and an audited internal assignment — without
ever mutating Alvys until a writeback contract is formally signed off."

---

## 2. What to demo (current merged capabilities)

- LTL **Search → Match → Assign → Bill** workbench (`/ltl` route, tabbed console).
- Enhanced filters, **saved views/presets**, workflow stage model, blocked-only filters.
- **Explainable matching**: weighted factors, per-factor breakdown, warnings, evidence
  coverage; unavailable factors are *excluded from the score denominator*.
- Internal **assignment / preflight / audit-only** boundary (no Alvys writeback).
- **Billing readiness**: invoice / visibility / equipment evidence signals.
- Durable EF Core **saved views** backed by SQL Server, with SQL migration verification
  in CI.
- **Sandbox-gated Alvys operations** boundary: Disabled / Simulation / Sandbox posture,
  dry-run / status / execute endpoints, readiness panel. Live mutations are blocked.

---

## 3. Setup prerequisites

### Tooling
- Docker + Docker Compose v2 (the demo stack runs SQL Server + API + Web).
- For local (non-Docker) runs: .NET 10 SDK and Node 20+ (CI uses Node 22).

### One-command stack
```bash
cp .env.example .env        # then set the values below
make build                  # build images + start SQL Server, API, Web
# Web: http://localhost:4200   API: http://localhost:5072   SQL: localhost,14333
```

Health check: `curl http://localhost:5072/api/health` (anonymous liveness).

### Shared URL for a remote audience
For a remote audience, demo against the **Azure-hosted** environment (Container Apps)
rather than your laptop — testers open the deployed Web URL directly. Deploy and URL
details are in [`AZURE_HOSTING.md`](AZURE_HOSTING.md). Set `Alvys__Provider=Fallback`
on the environment to boot with no live tenant. See §7 for what that means on screen.

### Environment variables that matter for the demo

| Variable | Demo value | Why |
|---|---|---|
| `ALVYS_PROVIDER` | `Fallback` (demo default) or `Live` | `Fallback` = empty results, no tenant needed. `Live` = real Alvys data (needs creds). |
| `ALVYS_TENANT_ID` / `ALVYS_CLIENT_ID` / `ALVYS_CLIENT_SECRET` | set only for live data | Server-side only; never exposed to the SPA. |
| `ALVYS_WRITEBACK_MODE` | `Disabled` (default) | Audit-only. `Simulation` = dry-run preview. `Sandbox` = gated, still Unsupported this phase. |
| `ALVYS_WRITEBACK_ENVIRONMENT` | empty | Must be `sandbox`/`uat`/`staging`/`test` to arm Sandbox mode. |
| `ALVYS_WRITEBACK_SANDBOX_BASE_URL` | empty | Must be a non-production host; the production host is rejected. |
| `ALLOWED_EMAIL_DOMAIN` | your org domain, or empty for first-run | Empty allows any authenticated user (handy for the demo). |
| `AZURE_AD_*` | your Entra app reg values | Required for real sign-in; SPA shows "Auth not configured" until set. |
| `LTL_DETECTION_ENABLED` | `false` (safe default) | Leave as-is for the demo. |

> Locally, credentials live only in the gitignored `.env` — never commit `.env`.
> In Azure, secrets live in Key Vault / GitHub environment secrets, never in the repo.

---

## 4. Demo flow (exact sequence)

Open the SPA and go to **`/ltl`**. Drive the tabs in this order.

### A. Search
1. Land on the **Search** tab — normalized LTL loads (not a raw Alvys grid).
2. Apply a **saved view** chip: *Unassigned LTL*, *High Revenue / Low Complexity*,
   *Today's Pickup*, *This Week's Deliveries*, *Missing Billing Data*, *Ready to Bill*,
   or *Exceptions*. **Talking point:** presets encode a dispatcher's recurring questions.
3. Expand the **filter form** (keyword, customer, origin/destination city + state,
   equipment, assignment state, pickup/delivery date ranges, billing badge, and the
   LTL-only / ready-to-bill / missing-billing / exceptions toggles).
4. Point out **removable applied-filter chips**, sortable sticky columns, and that
   **Miles shows `—`** when Alvys omits mileage. **Talking point:** missing data is shown
   as `missing`, never coerced to `$0`.

### B. Saved views (durable)
1. Create / rename / delete a saved view and reload the page to show it **persists**.
2. **Talking point:** saved views are durable EF Core entities backed by SQL Server, and
   the SQL migration is verified in CI (`migration-sqlserver` job) — not browser storage.
   Endpoints: `GET/POST /api/ltl/saved-views`, `PUT/DELETE /api/ltl/saved-views/{id}`.

### C. Match explanation
1. Select a load to open the **detail drawer**; matches load on demand
   (`GET /api/ltl/loads/{id}/matches?top=`).
2. Expand a recommendation's **per-factor breakdown**: Equipment, Weight capacity,
   Driver readiness, Fleet alignment, Geography (weights in `appsettings.json` → `Ltl:Match`).
3. Show the **Equipment availability** factor reading `Unavailable` (excluded from the
   score) when equipment events weren't fetched, and `Weak` when a maintenance/OOS event
   overlaps the load window.
4. **Talking point:** score = `earned / availableMax × 100`. Factors with no data are
   *not scored* (excluded from the denominator) so they never inflate or deflate the
   result. Hard disqualifiers (expired license/medical, terminated driver, over capacity)
   cap the label at **Not Recommended** with a stated reason.

### D. Assign — preflight + audit-only
1. In the **internal assignment panel**, pick a recommended match to prefill the form;
   it validates live (`POST /api/ltl/loads/{id}/assign/validate`).
2. Show a **blocker** disabling the Assign action (e.g. no driver, terminated driver,
   expired credential, over capacity → would return **422**).
3. Show a **warning** (equipment mismatch, expiring credential, passed pickup window,
   missing rate/weight/lane) that can be **overridden with a stated reason**.
4. Record the assignment (`POST /api/ltl/loads/{id}/assign`). The panel is explicitly
   labelled **"Not pushed to Alvys"**, and each audit entry shows
   `AlvysWriteback = NotPerformed`. **Talking point:** this is the deliberate writeback
   boundary — the decision is audited locally; Alvys is untouched.
5. Show the audit trail (`GET /api/ltl/loads/{id}/assignments`).

### E. Bill — readiness
1. Open the **Billing** tab (`GET /api/ltl/billing/worklist?badge=`), readiness-first.
2. On a load detail, show billing-readiness badges and **risks**: already-invoiced state
   and unpaid balance from the invoice record, the **tracking visibility** section
   (failed shares as blocking risks with an expandable milestone timeline), and exceptions.
3. **Talking point:** billing readiness is invoice/visibility/POD-aware; a posted invoice
   is never "ready to bill", and a positive remaining balance surfaces as a risk.

### F. Sandbox ops posture
1. In the Assign/Bill drawer, show the **Alvys writeback-readiness panel**
   (consumes `/api/alvys/ops/*`).
2. Show the headline posture ("Audit only" with default config), the explicit
   **blockers**, **per-operation eligibility**, and a **dry-run payload preview** for the
   create-load-note operation.
3. **Talking point:** every write operation is currently **Unsupported** for live
   execution by design — the captured Alvys docs cover read endpoints only, so we do not
   invent mutating routes. The panel states exactly what each operation needs to go live.

---

## 5. Key talking points (the narrative spine)

1. **Read-only, explainable, honest.** Live Alvys is the source of truth; we never write
   back, never invent data, and always explain a score.
2. **Operational leverage.** Saved views + explainable matching + billing readiness turn a
   raw load list into a prioritized, defensible dispatcher worklist.
3. **A real safety boundary, not a TODO.** The assignment endpoint and the sandbox-gated
   ops boundary are where Alvys writeback will plug in — gated, audited, and currently
   inert by design.
4. **Production-safe defaults.** A fresh clone, CI, and production all default to
   `Writeback=Disabled` and never mutate Alvys.

---

## 6. Behavior with vs. without live Alvys

| | `ALVYS_PROVIDER=Fallback` (demo default) | `ALVYS_PROVIDER=Live` (+ creds) |
|---|---|---|
| Search results | Empty (shape-preserving) | Real LTL loads |
| Matches / billing | Empty / not evaluated | Populated from live data |
| App boots | Yes | Yes (warns if creds missing) |
| Alvys mutated | No | No (read-only phase) |

To demo **with real data**, set `ALVYS_PROVIDER=Live` plus `ALVYS_TENANT_ID`,
`ALVYS_CLIENT_ID`, `ALVYS_CLIENT_SECRET` in `.env` and restart. Credentials stay
server-side; the SPA never receives them.

---

## 7. If Alvys sandbox credentials / data are unavailable (fallback path)

The demo does **not** require a live tenant. If creds or sandbox data are missing:

1. **Run in Fallback** (the demo default). Narrate the workflow against empty states —
   the UI's loading/empty/error states are themselves part of the "missing data is
   surfaced" story.
2. **Drive the API directly** to prove the endpoints are live and protected:
   ```bash
   curl http://localhost:5072/api/health                 # 200 (anonymous)
   curl -i http://localhost:5072/api/ltl/search          # 401 when unauthenticated → route mapped + protected
   curl -i http://localhost:5072/api/alvys/ops/status    # 401 unauth; 200 with auth → posture snapshot
   ```
3. **Show the ops posture** via `GET /api/alvys/ops/status` and
   `GET /api/alvys/ops/operations` to demonstrate the writeback boundary even with no data.
4. **Screenshots / recorded walkthrough.** If the live UI can't render representative
   data in time, fall back to a screen recording of the `/ltl` console captured against a
   live tenant beforehand. (Do **not** seed fake production data or browser storage to
   fake live results — that contradicts the "never invent data" message.)

---

## 8. Known caveats & do-not-demo items

**Do NOT demo / do NOT claim:**
- **No live Alvys writeback exists.** Do not click toward "send to Alvys" or imply a load
  was booked upstream. Every `ops` operation is `Unsupported` for live execution.
- **Do not flip `ALVYS_WRITEBACK_MODE=Sandbox` live on stage** expecting an upstream call —
  it stays `Unsupported` and is further gated by environment + non-production base URL.
- **Cancelled slice:** the outbox/idempotency work is intentionally out of scope and not
  on `main`. Don't reference it as shipped.

**Caveats to mention proactively if asked:**
- **Detail-path-only context.** Invoice billing refinement and per-load visibility are
  fetched when a load is *opened*; list/worklist rows carry `NotEvaluated` visibility and
  load-only billing inference until then.
- **Bounded visibility enrichment.** `/api/ltl/exceptions` enriches only the first
  `Ltl:MaxVisibilityEnriched` loads (default **25**) with visibility history; visibility-only
  failures beyond that cap appear on the load **detail** path only.
- **Auth.** With `AZURE_AD_*` unset, the SPA shows "Auth not configured"; set
  `ALLOWED_EMAIL_DOMAIN` empty for a frictionless demo, or to your org domain to show the
  allow-list.

---

## 9. Pre-demo readiness checklist

- [ ] `make build` brings up SQL Server + API + Web; `GET /api/health` returns 200.
- [ ] `/ltl` loads; Search tab renders (empty in Fallback, populated in Live).
- [ ] Saved view create → reload → still present (proves durable persistence).
- [ ] A load detail opens; match factors expand with an `Unavailable`/`Weak` example.
- [ ] Assignment panel shows a blocker (disabled Assign) and a warning (override).
- [ ] Billing tab loads; a load shows readiness badges / risks.
- [ ] Ops panel shows "Audit only" posture + a dry-run note preview.
- [ ] Decide Fallback vs. Live and set `.env` accordingly; restart if changed.
- [ ] (Remote audience) Azure deployment is live; deployed Web URL added as an Entra SPA redirect URI.

---

## 10. Build / test status at scope freeze

- **Web (Angular 20):** `npm ci && npm run build -- --configuration production` →
  **succeeds** (one non-blocking CSS budget warning on `ltl-search.css`).
- **API (.NET 10):** xUnit suite + SQL Server migration verification runs in CI
  (`.github/workflows/ci.yml`). As of the Phase 0 Stability slice, CI runs **four jobs**:
  `api` (`dotnet test -c Release`), `migration-sqlserver` (`Category=SqlServerMigration`),
  `web` (`npm run build` production), and `web-test` (`npm test`,
  `ng test --watch=false --browsers=ChromeHeadless`). Phase 0 also added a contract-
  preserving test that asserts every LTL endpoint in `CLAUDE.md` § "Current API surface
  (preserve)" stays mapped and behind `AllowedEmailDomain`. The .NET SDK was **not
  available in the runbook authoring environment**, so `dotnet test` was not re-run
  locally — rely on the green CI on `main` for API/test and migration verification.

---

## 11. What would make the 8 AM demo stronger

- **Live (or sandbox) Alvys credentials** (`ALVYS_TENANT_ID/CLIENT_ID/CLIENT_SECRET`) so
  Search/Match/Bill render real loads instead of empty Fallback states.
- A **valid Entra app registration** (`AZURE_AD_*`) for real sign-in, or confirmation that
  an empty `ALLOWED_EMAIL_DOMAIN` is acceptable for the audience.
- One or two **known representative load numbers** in the demo tenant that exercise a
  blocker, a warning, and a ready-to-bill state, so the match/assign/bill story lands.
- If remote: demo against the **Azure-hosted** environment so testers get a stable URL
  (see [`AZURE_HOSTING.md`](AZURE_HOSTING.md)); optionally map a custom domain.

---

## 12. Phase 0 Stability checklist (Search → Match → Assign → Bill)

Mirrors the Phase 0 exit criteria in [`ROADMAP.md`](../ROADMAP.md). Every net-new LTL
feature (Phase 1+) is deferred until every box below is checked on `main`.

**Search stable**
- [ ] `/ltl` Search renders in both `Fallback` and `Live` provider modes without console errors.
- [ ] Saved views: create / rename / delete / reload survives across app restart (locked by `EfSavedViewStoreTests`).
- [x] Sort keeps missing/null values last in both directions (locked by `LtlLoadServiceSortTests`).
- [ ] Billing badge filter uses signal-safe binding (Angular spec pending in the next Phase 0 PR).
- [ ] Bounded-sweep truncation message reflects `Ltl:MaxLoadsScanned` honestly.
- [ ] Loading / empty / error / paginated states render cleanly under real Alvys 401 / 429 / 500 responses.

**Match stable**
- [x] Exclude-when-Unavailable denominator is locked (`MatchScoringServiceTests`).
- [x] Hard-disqualifier caps produce `Not Recommended` (`MatchScoringServiceTests`).
- [x] Equipment availability factor reads `Unavailable` when events not fetched, `Weak` on OOS overlap.

**Assign stable**
- [x] Every blocker returns 422 and never records (`AssignmentValidationServiceTests`).
- [x] Every warning records and persists `overrideReason`.
- [x] Every assignment audit entry carries `AlvysWriteback = NotPerformed` in this phase.
- [ ] SPA panel label reads "Not pushed to Alvys" on every path (verified in template `ltl-search.html`; Angular assertion pending in the next Phase 0 PR).

**Bill stable**
- [x] POD-aware readiness: POD absence blocks Ready-to-Bill when documents supplied; not-evaluated when absent (`BillingReadinessServiceTests`).
- [x] Invoice aging + carrier-payable margin render on load detail (PR #30 tests).
- [ ] `Ltl:MaxVisibilityEnriched` bounded-enrichment banner shows on the Exceptions tab.

**Cross-cutting**
- [x] CI matrix is four jobs (api / migration-sqlserver / web / web-test).
- [x] LTL API surface is contract-locked (`LtlApiSurfaceContractTests`).
- [x] `AlvysWriteOptions` production-host rejection is locked (`AlvysWriteOptionsTests`).
- [x] UAT health workflow probes `/api/ltl/search` and `/api/alvys/ops/status` (expect 401 on unauth).
- [x] Alvys is the sole source of truth per `CLAUDE.md` Safety principles (no non-Alvys / non-DOT data paths).
- [ ] One clean end-to-end deploy has completed to Azure UAT App Service (blocked on Azure/Entra secrets landing in the `uat` environment).
- [x] No open TODO/FIXME/HACK in `src/LtlTool.Api/Features/Ltl/*` or `web/src/app/features/ltl/*` (swept 2026-07-15).
