# Azure UAT Deployment

This is the persistent launch path for the LTL tool. It is ported from the `freight-dna` sibling
repo's UAT provisioning pattern: Azure App Service (not Container Apps) fronted by a shared
Container Registry, plus an Azure SQL server/database this API needs to run.

## What this gives us

After one-time setup (below):

1. Run **Provision LTL Tool UAT infrastructure** once — creates the resource group, App Service
   Plan, two Web Apps (API + Web), Container Registry, Log Analytics, App Insights, Azure SQL
   server/database, and the two Microsoft Entra app registrations (API + SPA sign-in).
2. Merge to `main`, or manually run **Deploy LTL Tool UAT** — builds the API and Web Docker images
   into that Container Registry, points both Web Apps at the new images, configures their
   application settings, restarts them, and smoke-tests both.
3. **Verify LTL Tool UAT health** runs automatically right after a successful deploy — it checks
   every Azure resource in the resource group for a `Succeeded` provisioning state, confirms both
   App Services report `Running`, and hits both containers over HTTP to confirm they're actually
   serving real content (the API's `/api/health` JSON payload, the Angular shell's `<app-root>`
   element in the Web response) — not just that the deploy step exited `0`. It can also be run on
   demand any time to re-check status without a new build.
4. The deploy workflow's summary prints the public API and Web URLs. The app keeps running in
   Azure until stopped or redeployed.

Workflow files:

```text
.github/workflows/provision-ltl-uat-infra.yml
.github/workflows/deploy-ltl-uat.yml
.github/workflows/verify-ltl-uat-health.yml
```

Bicep template:

```text
infra/uat/main.bicep
```

Deploy script (called by the deploy workflow, and runnable locally):

```text
scripts/deploy-azure-uat.sh
```

## Required GitHub environment

Create a GitHub environment named:

```text
uat
```

GitHub path: **Repo → Settings → Environments → New environment**.

## Azure login requirement

Both workflows use OpenID Connect through `azure/login`, not a stored Azure password.

You need one Azure app registration/service principal with a federated GitHub credential and
enough permissions to create/update a resource group, App Service Plan, Web Apps, Container
Registry, Azure SQL server/database, plus enough Entra privilege to create app registrations and
service principals (**Application Developer** at minimum, **Application Administrator** if you
also want the admin-consent step in the provisioning workflow to succeed automatically).

A common role assignment for UAT is `Contributor` scoped to the target resource group or
subscription.

## One-time Azure setup

Run from Azure Cloud Shell or any machine with Azure CLI:

```bash
SUBSCRIPTION_ID="<subscription-id>"
TENANT_ID="<tenant-id>"
APP_NAME="ltl-uat-github-deploy"
RG="ltl-uat-rg"
LOCATION="centralus"

az account set --subscription "$SUBSCRIPTION_ID"
az group create --name "$RG" --location "$LOCATION"

APP_ID=$(az ad app create --display-name "$APP_NAME" --query appId -o tsv)
OBJECT_ID=$(az ad sp create --id "$APP_ID" --query id -o tsv)

az role assignment create \
  --assignee-object-id "$OBJECT_ID" \
  --assignee-principal-type ServicePrincipal \
  --role Contributor \
  --scope "/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RG"

az ad app federated-credential create \
  --id "$APP_ID" \
  --parameters '{
    "name": "github-uat-main",
    "issuer": "https://token.actions.githubusercontent.com",
    "subject": "repo:vc-for-valuetruck/ltl-tool-detection-planner-and-booker:environment:uat",
    "description": "GitHub Actions deploy to LTL Tool UAT",
    "audiences": ["api://AzureADTokenExchange"]
  }'

echo "AZURE_CLIENT_ID=$APP_ID"
echo "AZURE_TENANT_ID=$TENANT_ID"
echo "AZURE_SUBSCRIPTION_ID=$SUBSCRIPTION_ID"
```

> ⚠️ **The `az role assignment create` line above requires the caller to hold `Owner` or
> `User Access Administrator` on the subscription (or on the resource group). If you get
> `AuthorizationFailed` here — that's the entire blocker; keep reading.**

## Step 0 — Grant the deploy service principal the Contributor role

Everything downstream (Bicep, ACR, App Service, SQL) fails with cryptic `AuthorizationFailed`
errors when the `ltl-uat-github-deploy` service principal does not hold `Contributor` on the
resource group. This step exists to make that blocker legible and give you three concrete paths
to unblock it depending on what permission you already have in the tenant.

**Values you'll need in every path below:**

| Name | Value |
| --- | --- |
| `SUBSCRIPTION_ID` | `9dfdd151-fd80-4116-8c57-16f1d7156ded` |
| `TENANT_ID` | `99d7bd71-9046-4915-be1c-3aae2baf1645` |
| Service principal display name | `ltl-uat-github-deploy` |
| Service principal `APP_ID` (client id) | `eda7036b-7596-4c2a-836c-599fcd3a5166` |
| Resource group | `ltl-uat-rg` (in `centralus`) |
| Assign role | **Contributor** |
| Assign scope | The resource group `ltl-uat-rg` — **not the whole subscription** |

Always prefer resource-group scope. It's the least-privilege choice and it's the same scope every
Bicep file in `infra/uat/` deploys to.

### Path A — You have Owner or User Access Administrator (Cloud Shell / az CLI, ~30 seconds)

```bash
SUBSCRIPTION_ID="9dfdd151-fd80-4116-8c57-16f1d7156ded"
RG="ltl-uat-rg"
APP_ID="eda7036b-7596-4c2a-836c-599fcd3a5166"

az account set --subscription "$SUBSCRIPTION_ID"

# Look up the service principal's object id (safer than trusting a stale one).
OBJECT_ID=$(az ad sp list --filter "appId eq '$APP_ID'" --query "[0].id" -o tsv)
echo "Service principal object id: $OBJECT_ID"

az role assignment create \
  --assignee-object-id "$OBJECT_ID" \
  --assignee-principal-type ServicePrincipal \
  --role Contributor \
  --scope "/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RG"

# Verify.
az role assignment list \
  --assignee "$APP_ID" \
  --scope "/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RG" \
  --query "[].roleDefinitionName" -o tsv
```

Expected verify output: `Contributor` on its own line. Done — skip to Step 1.

If you get `AuthorizationFailed` on the `az role assignment create` line, you don't have the
required role. Use Path B or Path C.

### Path B — You have Owner or User Access Administrator on the resource group only (Portal, ~90 seconds)

This is the recommended path for Value Truck users who are not subscription Owners but have been
granted Owner or User Access Administrator on `ltl-uat-rg` specifically.

1. Portal → **Resource groups** → `ltl-uat-rg`.
2. Left nav → **Access control (IAM)**.
3. **+ Add** → **Add role assignment**.
4. **Role** tab → pick **Contributor** → **Next**.
5. **Members** tab → **Assign access to** = *User, group, or service principal*.
6. **+ Select members** → search **`ltl-uat-github-deploy`** → pick the app-registration entry
   whose Application (client) id equals `eda7036b-7596-4c2a-836c-599fcd3a5166` → **Select**.
7. **Review + assign** → **Review + assign**.

Verification (still in Portal): stay on `ltl-uat-rg` → **Access control (IAM)** → **Role
assignments** tab → filter by *Service principal* → `ltl-uat-github-deploy` should show one
row with role **Contributor** and scope **This resource**.

### Path C — You don't have either role (delegate the click)

If both Path A and Path B fail with `AuthorizationFailed`, send the following two-line request to
somebody in Value Truck who holds subscription Owner or User Access Administrator on
subscription `9dfdd151-fd80-4116-8c57-16f1d7156ded`:

> Please grant the service principal **`ltl-uat-github-deploy`** (app id
> `eda7036b-7596-4c2a-836c-599fcd3a5166`) the **Contributor** role on resource group
> **`ltl-uat-rg`** in subscription `9dfdd151-fd80-4116-8c57-16f1d7156ded`. Instructions:
> [`docs/AZURE_UAT_DEPLOY.md` §Path B](./AZURE_UAT_DEPLOY.md#path-b--you-have-owner-or-user-access-administrator-on-the-resource-group-only-portal-90-seconds).

Once the assignment lands, run the Path A verify command from Cloud Shell (that command only
reads role assignments; it does not require any write permission) to confirm before moving on.

### Preflight (built into the workflow)

The **Provision LTL Tool UAT infrastructure** workflow runs a preflight step immediately after
Azure login that fails fast, in one job step, with a clear error message when this role is
missing. If Step 1 fails with:

```text
✖ Deploy service principal ltl-uat-github-deploy (eda7036b-...) does not have
  Contributor on resource group ltl-uat-rg.
```

then you are in exactly this Step 0 blocker — apply Path A, B, or C, then re-run the workflow.
The preflight also passes when the role is granted at broader scope (e.g. subscription-scoped
Contributor also satisfies the RG-scoped check).

Put the printed values in the `uat` GitHub environment as **secrets**, plus a `SQL_ADMIN_PASSWORD`
secret of your choosing (used by `infra/uat/main.bicep` to create the SQL Server admin login):

```text
AZURE_CLIENT_ID
AZURE_TENANT_ID
AZURE_SUBSCRIPTION_ID
SQL_ADMIN_PASSWORD
```

## Step 1 — Provision infrastructure

Go to **Actions → Provision LTL Tool UAT infrastructure → Run workflow**.

Inputs: `resource_group` (default `ltl-uat-rg`), `location` (default `centralus`), `base_name`
(default `ltl-uat` — feeds every resource name: `ltl-uat-plan`, `ltl-uat-api`, `ltl-uat-web`,
`ltl-uat-sql-<unique>`, `ltl-uatacr`), `sql_database_sku` (default `S0`).

Re-running is safe for the Bicep-managed resources and skips re-creating an Entra app that already
exists by display name — it will **not** rotate an existing API client secret, so you won't lose
access by re-running.

The run summary lists every Bicep output (SQL server FQDN, ACR name/login server, App Insights
connection string) and both Entra app client IDs. This workflow **never writes GitHub secrets
itself** — download the `api-client-secret` artifact and set the following in the `uat` GitHub
environment yourself:

```text
AZURE_AD_TENANT_ID
AZURE_AD_API_CLIENT_ID
AZURE_AD_CLIENT_SECRET     # from the api-client-secret artifact — download, copy, delete it
AZURE_AD_WEB_CLIENT_ID
```

If the admin-consent step failed (it will, unless the service principal has Entra admin rights),
grant Microsoft Graph `User.Read` consent manually once: **Entra ID → App registrations →
ltl-uat-web → API permissions → Grant admin consent**.

## Step 2 — Deploy

Go to **Actions → Deploy LTL Tool UAT → Run workflow** (or push to `main` — it runs automatically).

This builds the API and Web images directly in Azure Container Registry (`az acr build`, no local
Docker needed), points both Web Apps at the new images, and configures their application settings
— including the SQL connection string, built from the SQL server FQDN plus `SQL_ADMIN_PASSWORD`.

Optional GitHub environment **variables** (all have safe defaults — set only to override):

```text
AZURE_RESOURCE_GROUP=ltl-uat-rg
LTL_UAT_BASE_NAME=ltl-uat
ALLOWED_EMAIL_DOMAIN=valuetruck.com
ALVYS_PROVIDER=Fallback
ALVYS_API_BASE_URL=https://integrations.alvys.com
ALVYS_API_VERSION=v1
ALVYS_WRITEBACK_MODE=Disabled
LTL_DETECTION_ENABLED=false
LTL_DEFAULT_TIMEZONE=America/Chicago
```

Optional secrets (only if you have a live Alvys sandbox/tenant to point at — leave unset to run
against the `Fallback` provider):

```text
ALVYS_TENANT_ID
ALVYS_CLIENT_ID
ALVYS_CLIENT_SECRET
```

## Step 3 — Verify health

**Verify LTL Tool UAT health** runs automatically after every successful deploy (triggered by the
deploy workflow completing), or on demand via **Actions → Verify LTL Tool UAT health → Run
workflow**. It does not build or deploy anything — it only checks:

- Every Azure resource in the resource group reports a `Succeeded` provisioning state.
- Both App Services (`<base>-api`, `<base>-web`) report a `Running` runtime state.
- The API responds `200` from `/api/health`.
- The Web app responds `200` and actually serves the Angular shell (`<app-root>` present in the
  HTML) — catches the case where the container is "Running" per Azure but crash-looping or
  serving a platform error page instead of the app.

The job fails (red ✗ in the Actions tab) if anything above isn't healthy, and its summary lists
the Web UI URL to open directly. Use this as the "is it actually safe to look at" signal rather
than only the deploy workflow's own smoke test, which only runs once per deploy and doesn't check
Azure resource provisioning states.

## After the first deploy

1. Copy the Web URL from the workflow summary.
2. Add it to the SPA app registration's redirect URIs:

```text
Azure Portal → Entra ID → App registrations → ltl-uat-web → Authentication →
Single-page application → Add redirect URI
```

3. Re-run the deploy workflow so CORS (`Cors__AllowedOrigins__0`) is recomputed against the final
   Web URL.

## Running the deploy script locally

```bash
SQL_PASSWORD=... \
AZURE_AD_TENANT_ID=... \
AZURE_AD_API_CLIENT_ID=... \
AZURE_AD_CLIENT_SECRET=... \
AZURE_AD_WEB_CLIENT_ID=... \
./scripts/deploy-azure-uat.sh
```

Requires `az` login (`az login`) with access to the `ltl-uat-rg` resource group. All other
variables (`RG`, `BASE_NAME`, `ALLOWED_EMAIL_DOMAIN`, `ALVYS_*`, etc.) have the same defaults as
the GitHub workflow and can be overridden the same way.

## Safety posture

- Alvys credentials stay server-side in the API app; the web app only receives public runtime
  config (`RUNTIME_*` application settings, injected into `runtime-config.json` at container
  startup — see `web/docker-entrypoint.sh`).
- `ALVYS_WRITEBACK_MODE=Disabled` remains the recommended default. The Alvys writeback boundary
  only ever activates for a recognised non-production sandbox environment with sandbox
  credentials configured — see `src/LtlTool.Api/Features/Integrations/Alvys/Writeback/AlvysWriteOptions.cs`.
- The LTL assignment flow remains internal/audited unless live writeback is formally approved —
  see `docs/ltl-tool.md`.
- The generated API client secret is masked in provisioning-workflow logs (`::add-mask::`) and
  only ever leaves the pipeline via a 1-day-retention build artifact, never printed in the step
  summary.
- The SQL server currently allows all Azure-internal traffic (`0.0.0.0` firewall rule), which is
  adequate for UAT but should be tightened (VNet integration + private endpoint) before this
  environment carries production data.

## Troubleshooting

### `az acr build` fails with a registry-not-found error

Confirm `provision-ltl-uat-infra.yml` has run successfully at least once in the target resource
group — `scripts/deploy-azure-uat.sh` resolves the ACR name dynamically from the resource group
rather than hard-coding it.

### Microsoft login redirect mismatch

Copy the exact URL from the browser error and add it to the SPA app registration's redirect URIs
(see "After the first deploy" above).

### API CORS failure

Confirm the Web app's actual URL matches what `Cors__AllowedOrigins__0` was set to on the API app
— re-run the deploy workflow after the Web app's URL is known/stable.

### API health check fails

Check the API Web App's Log Stream in the Azure Portal. Most failures are a missing/incorrect SQL
connection string, missing Entra values, or an Alvys provider set to `Live` without valid
credentials.
