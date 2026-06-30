# Azure infrastructure (Bicep)

Declarative infrastructure for hosting the LTL tool in Azure. This is an
alternative to letting the deploy workflow create resources imperatively with
`az containerapp` commands — use whichever your team prefers. The deployed
topology is identical and documented in [`../docs/AZURE_HOSTING.md`](../docs/AZURE_HOSTING.md).

## What it provisions

`main.bicep` (scope: resource group) creates:

| Resource | Purpose |
|---|---|
| Log Analytics workspace | Backs Container Apps logging |
| Container Apps managed environment | Runs the API and Web container apps |
| User-assigned managed identity | Lets the API read secrets from Key Vault (no passwords in env) |
| Key Vault | Stores SQL connection string, Entra client secret, Alvys credentials |
| Azure SQL Server + Database (`LtlTool`, serverless GP) | EF Core saved views + operation outbox |
| API container app | The .NET 10 API (`:8080`), external ingress, Key Vault secret refs |
| Web container app | nginx SPA (`:80`) that reverse-proxies `/api` to the API app |

## Secrets are never committed

`main.parameters.json` holds **non-secret** values only. Every credential is a
`@secure()` parameter with no committed value and must be passed at deploy time:

- `registryPassword` — GHCR token (or a GitHub PAT with `read:packages`)
- `entraApiClientSecret` — API app registration client secret
- `sqlAdminPassword` — SQL administrator password
- `alvysTenantId`, `alvysClientId`, `alvysClientSecret` — Alvys credentials (leave
  empty when `alvysProvider=Fallback`)

## Deploy

```bash
RG=ltl-tool-uat-rg
az group create -n "$RG" -l centralus

az deployment group create \
  -g "$RG" \
  -f infra/main.bicep \
  -p @infra/main.parameters.json \
  -p registryPassword="$GHCR_TOKEN" \
     entraApiClientSecret="$ENTRA_API_CLIENT_SECRET" \
     sqlAdminPassword="$SQL_ADMIN_PASSWORD" \
     alvysTenantId="$ALVYS_TENANT_ID" \
     alvysClientId="$ALVYS_CLIENT_ID" \
     alvysClientSecret="$ALVYS_CLIENT_SECRET"
```

Outputs include the API URL, Web URL, Key Vault name, and SQL FQDN.

## After deploy

1. Add the Web URL to the SPA app registration's **Single-page application**
   redirect URIs in Entra.
2. Apply EF Core migrations against the new Azure SQL database (see
   [`../docs/AZURE_HOSTING.md`](../docs/AZURE_HOSTING.md#database-migrations)).
3. Validate `GET <api-url>/api/health` returns `200`.

## Safety posture

- Alvys credentials live in Key Vault and reach only the API container — never the SPA.
- `alvysWritebackMode` defaults to `Disabled`; the app performs no live Alvys mutation.
- SQL uses TLS 1.2 and the serverless tier auto-pauses to control idle cost.
- `AllowAllAzureServices` SQL firewall rule is convenient for UAT; lock it down to
  the Container Apps environment outbound IP (or use a Private Endpoint) for production.
