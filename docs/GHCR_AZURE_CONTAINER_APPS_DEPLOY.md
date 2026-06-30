# GHCR → Azure Container Apps Deployment

This is the persistent launch path for the LTL tool.

GHCR stores the built API/Web container images. Azure Container Apps runs those images and gives the team a public URL that stays up after GitHub Actions finishes.

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
