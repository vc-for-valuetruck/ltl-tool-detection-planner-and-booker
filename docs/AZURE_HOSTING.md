# Azure Hosting

The LTL tool is hosted in **Microsoft Azure**. This document describes the target
architecture, why each service was chosen, the configuration the platform expects, the
deploy path, and rollback basics.

## Architecture at a glance

```text
        Browser (dispatcher / accounting)
                 │  HTTPS, single origin
                 ▼
   ┌───────────────────────────────┐
   │ Web  → Azure Container Apps    │  nginx serves the Angular SPA and
   │        (web container image)   │  reverse-proxies /api to the API app
   └───────────────┬───────────────┘
                   │  /api (internal)
                   ▼
   ┌───────────────────────────────┐     ┌──────────────────────┐
   │ API  → Azure Container Apps    │────▶│ Azure SQL Database    │
   │        (.NET 10 image)         │     │ (saved views, outbox) │
   └───────────────┬───────────────┘     └──────────────────────┘
                   │ managed identity (no secrets in image)
                   ▼
   ┌───────────────────────────────┐
   │ Azure Key Vault                │  SQL conn string, Entra client secret,
   │ (application secrets)          │  Alvys credentials (server-side only)
   └───────────────────────────────┘

   Microsoft Entra ID (MSAL): the SPA acquires tokens; the API validates them.
   Container images live in GitHub Container Registry (GHCR).
   Logs/metrics flow to a Log Analytics workspace.
```

## Service choices and why

| Concern | Choice | Why this fit the repo |
|---|---|---|
| **Web frontend** | **Azure Container Apps** (not Static Web Apps) | The web tier is a container (`web/Dockerfile`) that injects auth/runtime config at container start (`docker-entrypoint.sh` → `runtime-config.json`) and reverse-proxies `/api` to the API (`nginx.conf.template`). That single-origin model keeps **one** Entra redirect URI and **zero** CORS config. Azure Static Web Apps bakes config at build time and would split the origin, so Container Apps preserves the existing design with no app-code change. Static Web Apps remains a future option if the SPA moves to build-time config. |
| **.NET API** | **Azure Container Apps** (not App Service) | The API already ships a container (`src/LtlTool.Api/Dockerfile`) on `:8080`. Container Apps runs that image directly, scales independently, supports Key Vault secret references via managed identity, and shares one environment with the web app. App Service for Containers would also work; Container Apps keeps both tiers in one environment with consistent scaling and revision rollback. |
| **Database** | **Azure SQL Database** | EF Core migrations are already SQL Server-targeted and CI-verified (`Category=SqlServerMigration`). Azure SQL is the managed, drop-in target for saved views and the operation outbox — no provider change. |
| **AuthN/Z** | **Microsoft Entra ID + MSAL** | The SPA uses `@azure/msal-angular`; the API validates JWTs via the `AzureAd` options. Hosting only supplies tenant/client/scope values. |
| **Secrets** | **Azure Key Vault** | SQL connection string, Entra client secret, and Alvys credentials live in Key Vault and are read by the API through a user-assigned managed identity. Nothing secret is committed; the SPA never receives Alvys credentials. |
| **Images** | **GitHub Container Registry (GHCR)** | CI/CD builds and pushes the API/Web images; Container Apps pulls them. |

Infrastructure can be provisioned declaratively with the Bicep template in
[`../infra/`](../infra/README.md), or imperatively by the deploy workflow below.

## What this gives us

After this is configured:

1. Merge to `main` or manually run the deploy workflow.
2. GitHub Actions builds the API and Web Docker images.
3. Images are pushed to GitHub Container Registry.
4. Azure Container Apps is created or updated from those images.
5. The workflow prints the public API and Web URLs.
6. The app keeps running in Azure until stopped or redeployed.

Workflow file:

```text
.github/workflows/deploy-ghcr-azure-container-apps.yml
```

## Required GitHub environment

Create a GitHub environment named:

```text
uat
```

Optional later:

```text
production
```

GitHub path:

```text
Repo → Settings → Environments → New environment
```

## Required GitHub environment variables

Add these under the `uat` environment as **Variables**:

```text
AZURE_RESOURCE_GROUP=ltl-tool-uat-rg
AZURE_LOCATION=centralus
AZURE_CONTAINERAPPS_ENVIRONMENT=ltl-tool-uat-env
AZURE_API_APP_NAME=ltl-tool-uat-api
AZURE_WEB_APP_NAME=ltl-tool-uat-web
PUBLIC_WEB_ORIGIN=https://<web-container-app-url-after-first-deploy>
ALLOWED_EMAIL_DOMAIN=valuetruck.com
ALVYS_PROVIDER=Fallback
ALVYS_API_BASE_URL=https://integrations.alvys.com
ALVYS_API_VERSION=v1
ALVYS_WRITEBACK_MODE=Disabled
LTL_DETECTION_ENABLED=false
LTL_DEFAULT_TIMEZONE=America/Chicago
```

Notes:

- `PUBLIC_WEB_ORIGIN` is not known until the first deploy completes.
- For the first run, temporarily set it to an empty placeholder or your expected custom domain.
- After the first deploy, copy the Web URL from the workflow summary and update `PUBLIC_WEB_ORIGIN`.
- Re-run the workflow so CORS receives the final web origin.

## Required GitHub environment secrets

Add these under the `uat` environment as **Secrets**:

```text
AZURE_CLIENT_ID=<federated-credential app registration client id>
AZURE_TENANT_ID=<Azure tenant id>
AZURE_SUBSCRIPTION_ID=<Azure subscription id>
AZURE_AD_TENANT_ID=<Entra tenant id used by the app>
AZURE_AD_API_CLIENT_ID=<API app registration client id>
AZURE_AD_CLIENT_SECRET=<API app registration client secret>
AZURE_AD_WEB_CLIENT_ID=<SPA/web app registration client id>
AZURE_AD_API_SCOPE=api://<api-client-id>/access_as_user
SQL_CONNECTION_STRING=<Azure SQL connection string>
ALVYS_TENANT_ID=<Alvys tenant id or blank for fallback>
ALVYS_CLIENT_ID=<Alvys client id or blank for fallback>
ALVYS_CLIENT_SECRET=<Alvys client secret or blank for fallback>
```

## Azure login requirement

The workflow uses OpenID Connect through `azure/login`, not a stored Azure password.

You need one Azure app registration/service principal with a federated GitHub credential and enough permissions to create/update:

- Resource group
- Container Apps environment
- Container Apps

A common role assignment for UAT is `Contributor` scoped to the target resource group or subscription.

## One-time Azure setup example

Run from Azure Cloud Shell or any machine with Azure CLI:

```bash
SUBSCRIPTION_ID="<subscription-id>"
TENANT_ID="<tenant-id>"
APP_NAME="github-ltl-tool-deploy"
RG="ltl-tool-uat-rg"
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
    "subject": "repo:valuetruck-vc/ltl-tool-detection-planner-and-booker:environment:uat",
    "description": "GitHub Actions deploy to UAT",
    "audiences": ["api://AzureADTokenExchange"]
  }'

echo "AZURE_CLIENT_ID=$APP_ID"
echo "AZURE_TENANT_ID=$TENANT_ID"
echo "AZURE_SUBSCRIPTION_ID=$SUBSCRIPTION_ID"
```

Put the printed values in GitHub environment secrets.

## How to deploy manually

Go to:

```text
Actions → Deploy GHCR Images to Azure Container Apps → Run workflow
```

Use:

```text
environment_name=uat
image_tag=<blank>
```

Leaving `image_tag` blank builds and deploys the current commit SHA.

## How automatic deploy works

On every push to `main`, the workflow builds new API/Web images and deploys them to the `uat` environment.

## After first deploy

1. Copy the Web URL from the workflow summary.
2. Add it to the SPA app registration redirect URIs:

```text
Azure Portal → Entra ID → App registrations → Web/SPA app → Authentication → Single-page application → Add redirect URI
```

3. Update GitHub environment variable:

```text
PUBLIC_WEB_ORIGIN=<web url from workflow summary>
```

4. Re-run the deploy workflow.

## Database migrations

EF Core owns the schema (saved views + operation outbox). Apply migrations against
Azure SQL after the database exists and before/with the first API rollout:

```bash
# From a machine with the .NET SDK and the EF tools (dotnet tool install --global dotnet-ef)
dotnet ef database update \
  --project src/LtlTool.Api \
  --connection "Server=tcp:<sql-fqdn>,1433;Database=LtlTool;User Id=<admin>;Password=<password>;Encrypt=True;"
```

The SQL Server migration path is the same one CI verifies (`Category=SqlServerMigration`),
so a green CI run is a strong signal the migrations apply cleanly to Azure SQL.

## Rollback

Azure Container Apps keeps immutable revisions, which makes rollback fast:

```bash
# List revisions (newest first) for either app
az containerapp revision list -n <app-name> -g <rg> -o table

# Pin 100% of traffic back to the last-known-good revision
az containerapp ingress traffic set -n <app-name> -g <rg> \
  --revision-weight <good-revision>=100
```

Or redeploy a previous image tag by re-running the deploy workflow with `image_tag`
set to an earlier commit SHA. Database changes are **not** reverted by an app rollback —
if a migration must be undone, apply the corresponding down-migration deliberately and
treat it as a separate, reviewed change.

## Dock combine email notifications (Microsoft Graph sendMail)

When a dock worker commits a combine, the API emails a plan summary to the recipients
configured for that yard (`Ltl:Dock:NotifyRecipients`). Delivery uses **Microsoft Graph
`sendMail`** (app-only / client-credentials), which fits the existing Entra stack. Until
the app registration below is provisioned the email channel reports **NotConfigured**
(honest — nothing is sent, and the combine is never blocked). Once configured, real sends
move to **Delivered**; a genuine failure reports **Failed** with a retry chip.

**One-time Entra setup (owner: Dustin):**

1. Entra ID → App registrations → **New registration** (or reuse a dedicated mail app).
2. API permissions → **Microsoft Graph → Application permissions → `Mail.Send`**.
3. **Grant admin consent** for the tenant (required — application permission).
4. Certificates & secrets → create a client secret (store in Key Vault, not source).
5. Choose a real **sender mailbox** the app may send as (licensed or shared mailbox).

**Server-side config the API expects** (env / Key Vault, never client-side):

| Setting | Value |
| --- | --- |
| `Notifications__Email__Enabled` | `true` |
| `Notifications__Email__FromAddress` | the sender mailbox (e.g. `dispatch@valuetruck.com`) |
| `Notifications__Email__Graph__TenantId` | Entra tenant id |
| `Notifications__Email__Graph__ClientId` | app registration (client) id |
| `Notifications__Email__Graph__ClientSecret` | client secret (Key Vault reference) |

Optional: `Notifications__Email__MaxSendAttempts` (default 3) and
`Notifications__Email__RetryBaseDelayMs` (default 500) tune the transient-failure backoff.
Channel health (Configured/NotConfigured + last send result) is readable at
`GET /api/ltl/notifications/channels`. No secrets are ever returned to the SPA.

## Safety posture

- Alvys credentials stay server-side in the API container.
- The web container only receives public runtime config.
- `ALVYS_WRITEBACK_MODE=Disabled` remains the recommended default.
- The LTL assignment flow remains internal/audited unless live writeback is formally approved.

## Troubleshooting

### GHCR image pull fails

Confirm the workflow has:

```yaml
permissions:
  packages: write
```

Also confirm the Container App was created with registry credentials.

### Microsoft login redirect mismatch

Copy the exact URL from the browser error and add it to the SPA app registration redirect URIs.

### API CORS failure

Confirm `PUBLIC_WEB_ORIGIN` exactly matches the Web URL, including `https://` and no trailing slash.

### API health fails

Check Azure Container App logs for the API app. Most failures are missing SQL connection string, Entra values, or Alvys values when provider is set to `Live`.
