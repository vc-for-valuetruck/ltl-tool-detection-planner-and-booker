# Azure standalone deploy runbook

Provision + deploy the LTL pilot into an isolated Azure resource group
(`ltl-standalone-rg`) that doesn't touch any existing UAT infrastructure.
Ships in **Demo mode by default** so leadership sees the pilot working on a
real public Azure URL right away, without waiting on Entra Contributor role
assignment. Flipping to EntraId later is a one-command reconfigure — no
rebuild.

## Why standalone

`ltl-uat-rg` was blocked all week on the Entra Contributor role. The
standalone RG side-steps that entirely:

- Its own resource group, App Service Plan, ACR, SQL server, Log Analytics,
  App Insights. Names are all `ltl-standalone-*` so nothing collides with
  prior `ltl-uat-*` resources.
- Uses `AccessPolicy:Mode=Demo` initially so the API admits every request
  under a synthetic `demo@valuetruck.com` identity. No Entra tenant needed.
- Live Alvys reads against va336 (same credentials your MCP token uses).
- **Alvys writeback is hard-off** (`Alvys__Writeback__Mode=Disabled`). The
  click card is text the dispatcher pastes into Alvys manually.

Four independent guardrails prevent accidental Demo shipping to any
production-facing surface:

1. Default `AccessPolicy:Mode` in code is `EntraId`; Demo requires explicit
   opt-in via env var / app setting.
2. API startup logs a multi-line warning banner when Demo mode arms.
3. `/api/health` publishes `authMode: "Demo" | "EntraId"` as a second
   independent check operators can smoke-test.
4. `DemoAuthenticationHandler` is a separate `.cs` file, only wired into the
   pipeline when the mode gate matches.

## Prerequisites

- Azure CLI on the Windows box you're running from (`winget install -e --id Microsoft.AzureCLI`).
- Docker Desktop running locally (used to build the container images).
- **Contributor role on the subscription** (`9dfdd151-fd80-4116-8c57-16f1d7156ded`).
  This is the *subscription-level* role, not the specific-RG role \u2014 you need
  it to create the new resource group. If your access is scoped to
  `ltl-uat-rg` only, ask your Azure admin to grant Contributor on the
  subscription temporarily, or on a parent management group.
- Your va336 Alvys client secret (same one the local demo runner uses).

## Step 1: provision infrastructure

From the LTL repo root in PowerShell:

```powershell
.\scripts\setup-ltl-standalone.ps1
```

The script will:

1. Verify `az` CLI + login.
2. Prompt for a **SQL admin password** (interactive, hidden). Save it \u2014 you'll
   need it in step 2. Requirements: 8-128 chars, at least three of upper /
   lower / digit / symbol.
3. Create `ltl-standalone-rg` in `centralus`.
4. Deploy the Bicep template at `infra/uat/main.bicep` with the standalone
   naming. Takes 4-8 minutes.

At the end you'll see a table of provisioned resources.

Optional switches:

```powershell
.\scripts\setup-ltl-standalone.ps1 -WhatIf                    # preview only
.\scripts\setup-ltl-standalone.ps1 -Location eastus2
.\scripts\setup-ltl-standalone.ps1 -BaseName ltl-pilot-01
```

## Step 2: build + deploy images

```powershell
$env:ALVYS_CLIENT_SECRET = "<your va336 client secret>"
$env:SQL_ADMIN_PASSWORD  = "<the SQL password from step 1>"
.\scripts\deploy-ltl-standalone.ps1
```

The script will:

1. Verify Docker + Azure CLI + login.
2. Discover the ACR, SQL server, and App Services in `ltl-standalone-rg`.
3. Build the API image (`src/LtlTool.Api/Dockerfile`) and Web image
   (`web/Dockerfile`) locally.
4. Push both to `ltlstandaloneacr`.
5. Point the two App Services at the fresh images.
6. Configure app settings with `AccessPolicy:Mode=Demo`, blank
   `RUNTIME_TENANT_ID` (so the SPA's MSAL guard suppresses redirects), live
   Alvys credentials, and writeback disabled.
7. Restart both App Services.

Expect ~5-10 minutes total (mostly Docker build + push).

## Step 3: verify

```powershell
# Wait ~90s after the script prints "Deployed" so App Service pulls the images.
curl https://ltl-standalone-api.azurewebsites.net/api/health
# Expected: {"status":"ok","utc":"...","authMode":"Demo"}
```

Open the Web URL from the script's summary output (it prints
`https://ltl-standalone-web.azurewebsites.net`). You should land directly in
the LTL Operating Console with the "Auth configured" chip in the top right \u2014
no MSAL redirect.

Walk the same 5-minute demo script from `docs/LOCAL_DEMO.md`:

1. `/ltl` \u2014 search Laredo \u2192 Dallas.
2. `/ltl/consolidate` \u2014 corridor picker with live open-load count.
3. Enter a seed \u2192 Find candidates \u2192 select a sibling \u2192 Build plan preview.
4. Click card shows both Customer-side (billing) and Driver-side (RPM math)
   sections.

## Flipping to EntraId

Once the Entra Contributor role assignment lands, switch off Demo mode with a
single re-run \u2014 no rebuild:

```powershell
$env:AZURE_AD_CLIENT_SECRET = "<web app client secret>"
.\scripts\deploy-ltl-standalone.ps1 `
    -AccessPolicyMode EntraId `
    -AzureAdTenantId    "<tenant guid>" `
    -AzureAdApiClientId "<api client guid>" `
    -AzureAdWebClientId "<web client guid>"
```

This rebuilds and redeploys the images (with the same app code) and updates
the App Service settings. The startup banner warning goes silent because the
`AuthenticationSchemeRouter` now forwards to real JwtBearer instead of the
demo handler.

Confirm with:

```powershell
curl https://ltl-standalone-api.azurewebsites.net/api/health
# Expected: {"status":"ok","utc":"...","authMode":"EntraId"}
```

## Common errors

- **"Deployment failed \u2014 authorization"** on setup: you don't have
  subscription-level Contributor. Ask your Azure admin.
- **"ACR name already taken"**: someone else in the tenant took
  `ltlstandaloneacr` (globally unique). Re-run with
  `-BaseName ltl-standalone-02`.
- **API `authMode` returns `EntraId` instead of `Demo`**: docker-compose env
  didn't override; check the app settings on `ltl-standalone-api` in the
  portal. `AccessPolicy__Mode` should equal `Demo`.
- **Web loads but grid is empty**: bad `ALVYS_CLIENT_SECRET`. Redeploy with
  the correct value; the API logs will say so.

## Tear-down

If you need to burn the whole thing to the ground and start over:

```powershell
az group delete --name ltl-standalone-rg --yes --no-wait
```

Nothing outside this RG is affected.

## What lands on Azure

Given the standalone RG is a fresh, cheap sandbox for the pilot, cost is
minimal:

- App Service Plan (Linux B1): ~$13/mo
- Two Linux App Services: included in plan
- Azure SQL S0: ~$15/mo
- Azure Container Registry Basic: ~$5/mo
- Log Analytics + App Insights: pay-per-GB, negligible for pilot volume
- Storage / networking: negligible

Total: ~$35/mo. Kill it any time with the tear-down command above.
