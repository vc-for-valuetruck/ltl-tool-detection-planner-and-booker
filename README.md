# LTL Tool Detection, Planner, and Booker

Internal app for Value Truck dispatchers, built from the Value Truck UAT template.
On top of the renamed template plumbing it now ships the first **LTL
decision-support slice**: a normalized read model over live Alvys data, explainable
driver/equipment match scoring, billing-readiness detection, an internal (audited,
non-Alvys) assignment boundary, and an Angular search workspace. See section 11.

It ships a pre-wired full-stack starter:

- **.NET 10** Web API (vertical-slice features)
- **Angular 20** SPA (standalone components)
- **Microsoft Entra ID** (MSAL) authentication plumbing
- **SQL Server 2022** in Docker (no cloud backend required)
- **Docker Compose** local runtime
- **GitHub Codespaces** support for instant UAT sharing
- **ngrok** public-URL demo path (single origin serves SPA + API)
- Safe, generic `.env.example` and setup docs

---

## 1. What this template consists of

```text
.
├── docker-compose.yml          # sqlserver + api + web
├── docker-compose.demo.yml     # + ngrok tunnel (public demo URL)
├── start-demo.sh / stop-demo.sh# one-command ngrok demo
├── Makefile                    # up / build / down / logs / reset / demo-up
├── .env.example                # generic environment variables
├── TEMPLATE_SETUP.md           # step-by-step checklist for a new app
├── init/01-seed.sql            # SQL Server init/seed script
├── .devcontainer/              # Codespaces / VS Code dev container
├── docs/                       # codespaces-demo.md, demo-ngrok.md runbooks
├── scripts/                    # start-codespaces-demo.sh
├── .github/workflows/ci.yml    # build + test API and build web
├── LtlTool.sln                   # .NET solution
├── src/
│   ├── LtlTool.Api/              # .NET 10 Web API
│   └── LtlTool.Api.Tests/        # xUnit tests
└── web/                        # Angular 20 SPA
```

| Layer | Technology |
|---|---|
| Frontend | Angular 20 + MSAL Angular |
| Backend | .NET 10 Web API |
| Auth | Microsoft Entra ID |
| Database | SQL Server 2022 (Docker) |
| CI | GitHub Actions |
| UAT | GitHub Codespaces |

---

## 2. How to create a new repo from this template

```bash
# On GitHub: click "Use this template" → Create a new repository
# Or via CLI:
gh repo create my-new-app --template valuetruck-vc/uat-template --private --clone
```

Then follow [`TEMPLATE_SETUP.md`](TEMPLATE_SETUP.md) and the rename checklist in section 5.

---

## 3. First run

```bash
cp .env.example .env        # then fill in values (section 4)
make build                  # build images and start the stack
```

Endpoints:

```text
Web: http://localhost:4200
API: http://localhost:5072
SQL: localhost,14333
```

The SPA loads even before Entra is configured (it shows "Auth not configured")
so you can verify the stack end-to-end first, then wire up authentication.

You can also run the pieces directly without Docker:

```bash
dotnet run --project src/LtlTool.Api      # API on http://localhost:5072
cd web && npm install && npm start      # SPA on http://localhost:4200
```

---

## 4. Required configuration

Copy `.env.example` to `.env` and fill in:

| Variable | Purpose |
|---|---|
| `AZURE_AD_TENANT_ID` | Entra tenant (directory) ID |
| `AZURE_AD_API_CLIENT_ID` | App registration for the API |
| `AZURE_AD_CLIENT_SECRET` | API app client secret |
| `AZURE_AD_WEB_CLIENT_ID` | App registration for the SPA |
| `AZURE_AD_API_SCOPE` | Exposed API scope the SPA requests |
| `ALLOWED_EMAIL_DOMAIN` | Email domain allow-list (default `example.com`) |
| `MSSQL_SA_PASSWORD` | SQL Server `sa` password |
| `EXTERNAL_API_BASE_URL` | Optional outbound API base URL |
| `EXTERNAL_API_KEY` | Optional outbound API key |
| `ALVYS_*` | Alvys TMS integration (API only; blank until Phase 2). Never exposed to the SPA |
| `LTL_*` | LTL tool app settings (API only; safe defaults) |

These map to .NET configuration (`AzureAd__*`, `AccessPolicy__*`, `ExternalApi__*`,
`ConnectionStrings__DefaultConnection`) and Angular runtime config (`RUNTIME_*`) in
`docker-compose.yml`. Never commit `.env`.

> If `AccessPolicy:AllowedEmailDomains` is empty, any authenticated user is allowed
> (handy for first-run/local UAT). Set it to lock access to your org's domain.

---

## 5. Naming

This repo has already been renamed from the template placeholders to the app
identity:

- PascalCase project / namespace: `LtlTool` (`src/LtlTool.Api`, `LtlTool.sln`)
- Database name: `LtlTool`
- Web package / Angular project: `ltl-tool-detection-planner-and-booker-web`
- Docker Compose project / image / container prefix: `ltl-tool-detection-planner-and-booker`
- Browser title / app heading: `LTL Tool Detection, Planner, and Booker`

The original template placeholders (`MyApp` / `myapp`) should no longer appear.
A quick check:

```bash
grep -rIl --exclude-dir=node_modules --exclude-dir=bin --exclude-dir=obj --exclude-dir=dist 'MyApp\|myapp' .
```

After any further renames, run the commands in section 9 to confirm everything still builds.

---

## 6. Backend structure

```text
src/LtlTool.Api/
├── Program.cs               # auth, CORS, EF, options wiring
├── appsettings.json         # default config (overridden by env in Docker)
├── Dockerfile
├── Options/                 # AccessPolicyOptions, ExternalApiOptions
├── Security/                # AllowedEmailDomain authorization policy
├── Data/AppDbContext.cs     # EF Core context (add your DbSets here)
└── Features/                # vertical-slice features
    ├── Health/              # GET /api/health (anonymous liveness)
    ├── Me/                  # GET /api/me (protected sample endpoint)
    ├── Alvys/               # POST /api/alvys/{loads,trips,trailers,trucks,dispatch-preferences,locations,drivers,customers,users,tenders}/search + GET /api/alvys/tenders/{id} + GET /api/alvys/{loads,trips}?… + GET /api/alvys/loads/{loadNumber}/{documents,notes} + GET /api/alvys/trips/{tripId}/stops (protected, read-only)
    ├── Ltl/                 # LTL decision-support layer (normalization, billing readiness, match scoring, search) — see section 11
    └── Integrations/Alvys/  # server-side Alvys client (IAlvysClient) — credentials never leave the API
```

### Internal read-only Alvys endpoints

The dispatcher SPA never talks to Alvys directly — it calls these protected,
server-side endpoints, which proxy `IAlvysClient` so Alvys OAuth credentials stay on
the API. They are **read-only** endpoints (queries only; no Alvys writeback, no
`PUT`/`PATCH`/`DELETE`). Alvys models searches as `POST` (the filter set is the body),
so the search endpoints are `POST`; single-record lookups (e.g. a tender by id) are
`GET`. All require the `AllowedEmailDomain` policy (401 when unauthenticated). Live
Alvys remains the default source of truth.

| Endpoint | Request body | Returns |
|---|---|---|
| `POST /api/alvys/loads/search` | `LoadSearchRequest` | paged open-freight loads |
| `POST /api/alvys/trips/search` | `TripSearchRequest` | paged trips |
| `POST /api/alvys/trailers/search` | `TrailerSearchRequest` | paged trailer equipment |
| `POST /api/alvys/trucks/search` | `TruckSearchRequest` | paged truck equipment |
| `POST /api/alvys/dispatch-preferences/search` | `DispatchPreferenceSearchRequest` | dispatcher/driver/truck/trailer assignment pairings (bare array) |
| `POST /api/alvys/locations/search` | `LocationSearchRequest` | paged locations (geography / shipper-consignee-warehouse) |
| `POST /api/alvys/drivers/search` | `DriverSearchRequest` | paged drivers (assignment/readiness) |
| `POST /api/alvys/customers/search` | `CustomerSearchRequest` | paged customers (billing/matching context) |
| `POST /api/alvys/users/search` | `UserSearchRequest` | paged users (dispatcher names/roles) |
| `POST /api/alvys/tenders/search` | `TenderSearchRequest` | paged inbound tenders (EDI offers) |
| `GET /api/alvys/tenders/{tenderId}` | _(path param)_ | single tender (404 when not found) |
| `GET /api/alvys/loads?id=…\|loadNumber=…\|orderNumber=…` | _(query)_ | single load detail (400 if no criterion, 404 when not found) |
| `GET /api/alvys/trips?id=…\|tripNumber=…[&includeDeleted=…]` | _(query)_ | single trip detail (400 if no criterion, 404 when not found) |
| `GET /api/alvys/trips/{tripId}/stops` | _(path param)_ | polymorphic trip stops — route assembly (bare array) |
| `GET /api/alvys/loads/{loadNumber}/documents` | _(path param)_ | load documents — rate con / POD / customer backup (bare array) |
| `GET /api/alvys/loads/{loadNumber}/notes` | _(path param)_ | load notes — operational comments / audit context (bare array) |

The tender by-id, load/trip detail lookups, trip-stops and load document/note listings are
`GET` (load/trip detail take query parameters and return 400 with no criterion / 404 when
not found); the rest are `POST` searches. All are read-only (no tender accept/reject, no
note/document creation, no `PUT`/`PATCH`/`DELETE`).

Add new functionality as a folder under `Features/` (controller + service + DTOs).

---

## 7. Frontend structure

```text
web/
├── package.json
├── angular.json
├── Dockerfile               # build → nginx, proxies /api → api service
├── nginx.conf.template
├── docker-entrypoint.sh     # writes runtime-config.json from RUNTIME_* env
├── public/runtime-config.json
└── src/
    ├── index.html
    ├── main.ts              # loads runtime config, then bootstraps
    └── app/
        ├── app.ts           # root standalone component
        ├── app.config.ts    # MSAL + router + http wiring
        ├── app.routes.ts
        ├── runtime-config.ts
        ├── pages/home/      # lazy-loaded sample page
        └── features/ltl/    # LTL search workspace (models, service, lazy /ltl page)
```

Auth and API config are loaded at runtime from `runtime-config.json`, so the same
built image works in any environment without a rebuild. Add lazy-loaded pages under
`src/app/pages/`.

---

## 8. Data / seed strategy

SQL Server runs in Docker. On first container start, scripts in `init/` are executed
in alphabetical order (mounted to `/docker-entrypoint-initdb.d`). Edit
`init/01-seed.sql` to create your database, tables, and demo data for UAT.

```bash
make reset   # tears down volumes and re-seeds from init/
```

For schema managed in code, add EF Core migrations in `src/LtlTool.Api` and apply them
on startup or via `dotnet ef database update`.

---

## 9. Commands

```bash
# Docker stack
make up        # start in background
make build     # build images and start
make down      # stop
make logs      # tail logs
make reset     # wipe volumes, rebuild, re-seed

# Public demo URL via ngrok (needs NGROK_AUTHTOKEN in .env)
make demo-up   # stack + ngrok tunnel, prints the public URL
make demo-down # tear down the demo stack + tunnel

# Backend
dotnet restore
dotnet build
dotnet test

# Frontend
cd web
npm install
npm run build
npm test -- --watch=false
```

---

## 10. UAT guidance

Two ways to share a running build with testers:

### a) GitHub Codespaces (default)

Open the repo in a **Codespace**, start the stack, and share the forwarded port
`4200` URL. No local setup or cloud subscription required. The devcontainer
installs .NET/Node/Docker/GitHub CLI, seeds `.env`, and restores dependencies.

```bash
make up                                  # or: bash scripts/start-codespaces-demo.sh
```

- Port `4200` (web) → set **Public** and share. Port `5072` (API) stays private;
  `/api` is reachable through the `4200` URL (e.g. `<url>/api/health`).
- Add the exact forwarded `4200` URL to the SPA app registration's redirect URIs.
- Use **Codespaces secrets** for `MSSQL_SA_PASSWORD`, `AZURE_AD_CLIENT_SECRET`, etc.
- Full runbook: [`docs/codespaces-demo.md`](docs/codespaces-demo.md).

### b) ngrok public URL (local backup)

Expose a local Docker stack over one public HTTPS URL. The web container proxies
`/api`, so a single ngrok origin serves both SPA and API.

```bash
cp .env.example .env     # set NGROK_AUTHTOKEN (free token) + Entra values
make demo-up             # prints the public URL + the Entra redirect to add
```

- Full runbook: [`docs/demo-ngrok.md`](docs/demo-ngrok.md).
- **Never commit `NGROK_AUTHTOKEN`** — keep it only in the gitignored `.env`.

### General

- Seed representative demo data in `init/01-seed.sql` so testers see realistic state.
- Keep the email-domain allow-list (`ALLOWED_EMAIL_DOMAIN`) aligned with the testers
  you invite, or leave it empty during early UAT to allow any authenticated user.

---

## 11. LTL decision-support layer

This is the first product slice: an operational/revenue-protection layer on top of the
read-only Alvys integration. It is **not** a raw load grid — it normalizes Alvys loads
into an LTL read model, scores driver/equipment matches with explainable labels, and
flags billing readiness and exceptions.

### Design principles

- **Missing data is surfaced, never invented.** Money/weight/mileage are nullable; an
  absent value is rendered as `missing` and tagged with a `MissingDataFlag` rather than
  coerced to `$0`. Fields Alvys does not project (e.g. commodity) are always flagged.
- **Explainable, deterministic scoring.** A match score is `earned / availableMax × 100`.
  Factors whose data is unavailable (Hours-of-Service, historical performance) are reported
  as *not scored* and excluded from the denominator, so they neither inflate nor deflate the
  result. Hard disqualifiers (expired license/medical, terminated driver, over capacity) cap
  the label at **Not Recommended** with a stated reason.
- **Read-only against Alvys.** Every endpoint queries Alvys; nothing is written back.

### Endpoints (`/api/ltl`, protected by `AllowedEmailDomain`)

| Endpoint | Returns |
|---|---|
| `GET /api/ltl/search` | normalized, filtered, sorted, paged loads (`LtlSearchResponse`) |
| `GET /api/ltl/loads/{idOrNumber}` | single normalized load detail (404 when not found) |
| `GET /api/ltl/loads/{idOrNumber}/matches?top=` | ranked, explainable driver/equipment matches |
| `GET /api/ltl/loads/{idOrNumber}/billing-readiness` | billing-readiness evaluation (POD-aware) |
| `POST /api/ltl/loads/{idOrNumber}/assign/validate` | pre-flight validation of a proposed assignment (blockers + warnings) |
| `POST /api/ltl/loads/{idOrNumber}/assign` | records an **internal** assignment decision (422 when blocked — see below) |
| `GET /api/ltl/loads/{idOrNumber}/assignments` | internal assignment audit trail for a load |
| `GET /api/ltl/billing/worklist?badge=` | loads needing billing attention, readiness-first |
| `GET /api/ltl/exceptions` | loads carrying operational/billing exceptions |

### Assignment boundary (no Alvys writeback)

`POST /assign` is the only mutating endpoint, and it is deliberately **internal**: it
records the decision in a local audit store (`IAssignmentAuditStore`) and returns
`AlvysWriteback = "NotPerformed"`. The Alvys integration is read-only in this phase, so no
trip/driver assignment is pushed upstream. The future writeback boundary lives here — when
Alvys writes are enabled, this endpoint is where the upstream call is added and the audit
flag flips.

Before recording, the request runs through `AssignmentValidationService`, which resolves the
proposed driver/truck/trailer against Alvys and produces typed `AssignmentIssue`s split into
**blockers** and **warnings**:

- **Blockers** (e.g. no driver selected, terminated/inactive driver, expired license or medical,
  over trailer capacity) make `POST /assign` return **422 Unprocessable Entity** with the
  validation result — the decision is *not* recorded.
- **Warnings** (e.g. equipment mismatch, expiring credentials, passed pickup window, missing
  rate/weight/lane) do **not** block; they are recorded on the audit entry. A dispatcher can
  supply an `overrideReason` to proceed past warnings, and that reason is persisted on the audit.

`POST /assign/validate` runs the same checks without recording anything, so the SPA can
surface issues live as a match is chosen.

### Configuration (`Ltl` section / `LTL_*` env)

Safe defaults ship in `appsettings.json`: Alvys sweep bound (`MaxLoadsScanned`), page size,
match-candidate bounds (`MaxMatchCandidates`, `DefaultMatchResults`), LTL classification hints,
stale-uninvoiced threshold, and the match scoring weights/thresholds (`Ltl:Match`).

### Frontend

The Angular `/ltl` route (`web/src/app/features/ltl/`) is a tabbed enterprise console:

- **Search** — saved-view chips (Unassigned LTL, High Revenue / Low Complexity, Today's Pickup,
  This Week's Deliveries, Missing Billing Data, Ready to Bill, Exceptions), an expanded filter
  form (keyword, customer, origin/destination city + state, equipment, assignment state,
  pickup/delivery date ranges, billing badge, LTL-only/ready-to-bill/missing-billing/exceptions
  toggles), removable applied-filter chips, sortable sticky-header columns
  (including Miles, shown as `—` when Alvys omits mileage), billing/missing/exception badges,
  loading/error/empty states, and pagination.
- **Billing** — the `billing/worklist` endpoint with a badge filter, readiness-first ordering.
- **Exceptions** — loads carrying operational/billing exceptions.

Selecting a load opens a detail drawer that loads explainable match recommendations on demand
(expandable per-factor breakdown), billing-readiness badges and risks, exceptions, and an
**internal assignment panel**. Choosing a recommended match prefills the form and validates it
live; blockers disable the Assign action while warnings can be overridden with a stated reason.
The panel is explicitly labelled "Not pushed to Alvys", and the assignment history shows each
entry's `AlvysWriteback` status.
