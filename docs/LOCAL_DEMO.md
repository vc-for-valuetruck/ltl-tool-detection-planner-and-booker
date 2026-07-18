# Local demo runbook

Show the LTL Phase 1 pilot end-to-end from a single laptop. No Azure resources, no
Entra tenant, no UAT deployment — just Docker Desktop, real Alvys credentials, and a
browser.

## Why this exists

Azure UAT provisioning was held up by tenant Contributor role assignment; the demo
runbook is a parallel path so Value Truck leadership (Ben, Junior, Holly, Brian) can
see live va336 data flowing through the Search → Match → Consolidate → Bill workflow
without waiting on IT.

The tool runs identically to the UAT stack in every respect **except** authentication.
Reads go straight to the live Alvys tenant.

## What you get

- Live Alvys reads from va336 (the same tenant your MCP connector is bound to).
- The full Angular workbench on `http://localhost:4200`.
- API on `http://localhost:5072`, Swagger UI on `/swagger` for API tour-guiding.
- SQL Server 2022 in Docker for internal audit rows (assignment history, saved views).
- All writes to Alvys stay hard-disabled at the writeback boundary.

## What's different from UAT

| Concern | UAT (Azure) | Demo (local) |
|---|---|---|
| Authentication | Entra ID JWT via MSAL | `DemoAuthenticationHandler` grants every request a synthetic `demo@valuetruck.com` identity |
| DB | Azure SQL | SQL Server 2022 container |
| Hosting | Azure Container Apps | Docker Compose |
| Alvys reads | Live va336 | Live va336 (same) |
| Alvys writes | Disabled | Disabled |
| Persistence | Durable | Container lifetime |

## Prerequisites

- Docker Desktop running (macOS, Linux, or Windows).
- Your Alvys `client_id` and `client_secret` for the va336 tenant. These are the
  same credentials that mint your MCP token; find them in your Alvys developer
  portal or reuse the values from your MCP config.

## First-run

```bash
# from repo root
cp .env.demo.example .env
# open .env, paste your ALVYS_CLIENT_SECRET
./scripts/demo-up.sh          # macOS / Linux / WSL
pwsh ./scripts/demo-up.ps1    # Windows PowerShell
```

First build takes ~2-3 minutes (image pull + `dotnet publish` + `ng build`).
Subsequent runs re-use the layer cache.

After the containers boot and `/api/health` reports healthy, the runner does one more
check: it curls `/api/health` and asserts `authMode == "Demo"`. If the API somehow booted
in EntraId mode (typo in `.env`, config precedence surprise, wrong image), the runner
prints a clear failure message and exits non-zero before you ever open the browser. This
is the second of the two independent demo-mode checks; the first is the multi-line
warning banner in the API logs (`docker compose logs api | grep DEMO`).

## Watch the workflow run itself (Playwright E2E)

If you want to see the pilot flow execute without touching the mouse — useful for
your own smoke-tests, or for recording a demo video without live typing — there's a
headed Playwright suite that drives Search → Consolidate against the running demo
stack.

One-time setup (after `demo-up` succeeds):

```bash
cd web
npm install                     # picks up the Playwright dep from package.json
npm run test:e2e:install        # downloads Chromium (~150 MB, one-time)
```

Run the workflow while watching (opens a real Chromium window, ~1.5 s between steps):

```bash
cd web
npm run test:e2e
```

Alternatives:

- `npm run test:e2e:ui` — Playwright's time-travel UI (best for debugging).
- `npm run test:e2e:ci` — headless, for pipelines.

The suite is intentionally tolerant of live-data variance: if no Laredo→Dallas loads
are currently open in Alvys, plan-preview specs `test.skip()` rather than fail. Watch
for `⚠` lines in the console output that call this out honestly.

## Demo script for leadership (5 minutes)

1. **Open** `http://localhost:4200/ltl`. The workbench loads with no auth
   redirect — the "signed in" indicator shows `demo@valuetruck.com`.
2. **Search Laredo → Dallas.** Enter origin `Laredo` and destination `Dallas`,
   press Search. The grid populates with real va336 loads.
3. **Sort by "Missing data" flag** to show the honest data-quality picture the
   pilot promises: no fabricated weights, no invented POD counts.
4. **Click a load row** to open the detail drawer. Point out the source badge
   ("Live Alvys") on each field.
5. **Click "Consolidate"** to open the plan preview. Show the sibling candidate
   list, the corridor gate ("in corridor"), and the customer-tier chip.
6. **Show the click card**: driver RPM section (from `Trip.TripValue.Amount /
   Trip.LoadedMileage.Distance.Value`) sitting next to the customer-billing
   section. That's the number a dispatcher pastes into Alvys.
7. **Stop** with `docker compose down`.

## Live-data caveat

The demo reads from **production Alvys**. Nothing writes back — the writeback
boundary is hard-off — but every read you make counts against your Alvys API
quota. Don't leave the stack up idle overnight.

## Security posture

- `AccessPolicy:Mode = Demo` is the sole switch that arms the demo auth handler.
  It logs a loud warning banner at startup, and the mode is exposed in `/api/health`
  so a wrong deployment fails observably rather than silently.
- The demo handler is a separate `.cs` file (`Security/DemoAuthenticationHandler.cs`)
  that is only registered when `AccessPolicy:Mode = Demo`. In UAT/production the
  handler is compiled in but never wired into the auth pipeline.
- **Never** set `ACCESS_POLICY_MODE=Demo` in a UAT or production `.env`. The
  startup banner and the health endpoint's `authMode` field are your two
  independent checks.

## Troubleshooting

- **API won't start** — `docker compose logs api`. Common: SQL Server not yet
  healthy on very slow laptops; the healthcheck retries for 2 minutes.
- **Web loads but grid is empty** — `docker compose logs api | grep Alvys`.
  Usually a bad `ALVYS_CLIENT_SECRET` or an expired token from a stale image;
  `docker compose down && ./scripts/demo-up.sh` to rebuild.
- **Port 4200 or 5072 already in use** — edit `docker-compose.yml` ports
  section; nothing else in the codebase hardcodes them.
- **First build fails on Windows with symlink errors** — enable long paths in
  Git: `git config --global core.longpaths true`, then re-clone.

## Rolling to UAT later

When Azure UAT is unblocked, the exact same code deploys with:

```bash
ACCESS_POLICY_MODE=EntraId  # or omit; default is EntraId
```

Plus the real `AzureAd__*` values. No code change, no rebuild. The demo
handler is dormant.
