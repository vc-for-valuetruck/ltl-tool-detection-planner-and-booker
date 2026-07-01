# Azure UAT Resource Provisioning

This is the manual-trigger pipeline that creates the Azure resources and Microsoft Entra app
registrations the LTL tool's UAT environment needs, so they don't have to be clicked together by
hand in the Azure/Entra portals. It is a companion to
`docs/GHCR_AZURE_CONTAINER_APPS_DEPLOY.md`, which already automates building/pushing images and
creating the two Container Apps — this pipeline covers what that one doesn't: the Container Apps
environment's Log Analytics workspace, the Azure SQL server/database, and the Entra app
registrations for API + SPA sign-in (including Microsoft Graph `User.Read` so the app can read
basic signed-in-user/directory data, not just validate the token).

Workflow file:

```text
.github/workflows/provision-azure-resources.yml
```

Bicep template:

```text
infra/main.bicep
```

## What this does — and does not — automate

| Resource | Created by |
| --- | --- |
| Resource group | This workflow (`az group create`, idempotent) |
| Log Analytics workspace | `infra/main.bicep` |
| Container Apps environment | `infra/main.bicep` (idempotent with the `az containerapp env create` step already in the deploy workflow — either can run first) |
| Azure SQL server + database | `infra/main.bicep` |
| Entra API app registration (`ltl-uat-api`) + `access_as_user` scope + client secret | This workflow, via `az ad`/Microsoft Graph (`az rest`) |
| Entra SPA app registration (`ltl-uat-web`) + Microsoft Graph `User.Read` delegated permission | This workflow |
| The two Container Apps themselves (API, Web) | **Not this workflow** — `deploy-ghcr-azure-container-apps.yml`, unchanged |

Entra app registrations are Microsoft Graph objects, not ARM resources, so Bicep cannot own them —
that's why they're az cli/Graph steps in the workflow rather than part of `main.bicep`.

## Naming convention

Everything this pipeline creates is prefixed `ltl-uat-` (resource group `ltl-uat-rg`, Container
Apps environment `ltl-uat-cae`, SQL server `ltl-uat-sql-<unique>`, database `ltl-uat-sqldb`, Entra
apps `ltl-uat-api`/`ltl-uat-web`).

**Note:** `docs/GHCR_AZURE_CONTAINER_APPS_DEPLOY.md` documents a manually-provisioned environment
named `ltl-tool-uat-*` (e.g. `ltl-tool-uat-rg`). If that environment already exists, decide
whether to point this workflow's `AZURE_RESOURCE_GROUP`/`AZURE_CONTAINERAPPS_ENVIRONMENT`
variables at the existing names (recommended, avoids a duplicate environment) or run this
workflow standalone and migrate the deploy workflow's variables to the new `ltl-uat-*` names once
you're ready to cut over. This was **not** decided automatically — the two naming schemes are not
reconciled by this change.

## Required GitHub environment secrets

Add these under the `uat` environment (same environment the deploy workflow already uses):

```text
AZURE_CLIENT_ID          # same federated-credential app registration as the deploy workflow
AZURE_TENANT_ID
AZURE_SUBSCRIPTION_ID
SQL_ADMIN_PASSWORD       # new — SQL Server admin password for infra/main.bicep, your choice
```

The Azure login service principal needs `Contributor` on the target resource group/subscription
(as documented in `docs/GHCR_AZURE_CONTAINER_APPS_DEPLOY.md`) **plus** enough Entra privilege to
create app registrations and service principals — typically the **Application Developer** Entra
role at minimum, or **Application Administrator** if you also want the admin-consent step to
succeed automatically (see below).

## How to run

```text
Actions → Provision Azure UAT Resources → Run workflow
```

Inputs: `environment_name` (`uat`), `location` (default `centralus`), `sql_database_sku` (default
`S0`). Re-running is safe for the Bicep-managed resources and skips re-creating an Entra app that
already exists by display name — it will **not** rotate an existing API client secret, so you
won't lose access by re-running.

## After it runs

The run summary lists every output (SQL server FQDN, Container Apps environment name, both Entra
app client IDs) and a numbered checklist. This workflow **never writes GitHub secrets itself** —
you review the summary/artifact and set the following secrets/variables yourself, same as the
existing deploy workflow expects:

```text
AZURE_AD_TENANT_ID
AZURE_AD_API_CLIENT_ID
AZURE_AD_CLIENT_SECRET     # from the api-client-secret artifact (download, copy, delete the artifact)
AZURE_AD_WEB_CLIENT_ID
AZURE_AD_API_SCOPE=api://<api-client-id>/access_as_user
SQL_CONNECTION_STRING      # built from the SQL server FQDN output + SQL_ADMIN_PASSWORD
```

If the admin-consent step failed (it will, unless the service principal has Entra admin rights),
grant Microsoft Graph `User.Read` consent manually once: **Entra ID → App registrations →
ltl-uat-web → API permissions → Grant admin consent**.

The SPA app's redirect URI is created as a placeholder (`https://localhost`) — update it to the
real Web Container App URL after the first deploy, exactly as already documented in
`docs/GHCR_AZURE_CONTAINER_APPS_DEPLOY.md`.

## Microsoft Graph scope

Only `User.Read` (delegated) is requested — enough for the SPA to read the signed-in user's own
profile via Graph if/when that's wired into the app. **No code in this repo calls Microsoft Graph
yet** — this pipeline only provisions the permission grant so that a future slice can add a
Graph-backed profile/directory read without a second app-registration change. Do not assume
directory data is already surfaced anywhere in the product from this change alone.

## Safety posture

- **Manual trigger only** (`workflow_dispatch`, no `push` trigger) — this can never run
  automatically on a commit.
- Uses the same OIDC federated-credential Azure login as the existing deploy workflow — no
  long-lived Azure password/secret stored in GitHub.
- The generated API client secret is masked in logs (`::add-mask::`) and only ever leaves the
  pipeline via a 1-day-retention build artifact, never printed in the step summary.
- This workflow does not touch Alvys configuration, the LTL writeback boundary, or any
  application code — it is infrastructure-only. See `docs/ltl-tool.md` for the separate,
  unrelated decision record covering Alvys production writeback.

## What this does not do

- Does not deploy the API/Web Container Apps — run `deploy-ghcr-azure-container-apps.yml`
  afterwards (or it runs automatically on push to `main`, per its existing trigger).
- Does not configure a production Entra tenant or production Azure subscription — `environment_name`
  is currently fixed to `uat` in the workflow's `choice` input; extending to `production` is a
  deliberate follow-up, not a default.
- Does not lock the SQL server down to Container Apps' actual egress IPs — it currently allows all
  Azure-internal traffic (`0.0.0.0` firewall rule), which is adequate for UAT but should be
  tightened (VNet integration + private endpoint) before this environment carries production data.
