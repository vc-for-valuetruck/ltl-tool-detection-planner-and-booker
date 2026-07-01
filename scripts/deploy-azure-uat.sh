#!/usr/bin/env bash
set -euo pipefail

# Builds the API and Web container images into the ACR provisioned by infra/uat/main.bicep,
# points the two ltl-uat-* App Services at the new images, and configures their application
# settings. Run this after infra/uat/main.bicep has been deployed once (see
# .github/workflows/provision-ltl-uat-infra.yml) — it does not create the resource group, App
# Service Plan, ACR, SQL server, or the apps themselves, only builds/deploys into what already
# exists. Ported from the freight-dna sibling repo's deploy-configuration pattern, adapted to
# build/push images (that repo's apps were already built elsewhere) and to this repo's actual
# appsettings.json/docker-entrypoint.sh contract.
#
# Secrets must come from your shell/session or CI secret store. Do not hard-code values here.

RG=${RG:-ltl-uat-rg}
BASE_NAME=${BASE_NAME:-ltl-uat}
API_APP=${API_APP:-${BASE_NAME}-api}
WEB_APP=${WEB_APP:-${BASE_NAME}-web}
SQL_DB=${SQL_DB:-${BASE_NAME}-sqldb}
SQL_ADMIN=${SQL_ADMIN:-ltlsqladmin}
IMAGE_TAG=${IMAGE_TAG:-$(date +%Y%m%d%H%M%S)}

ALLOWED_EMAIL_DOMAIN=${ALLOWED_EMAIL_DOMAIN:-valuetruck.com}
ALVYS_PROVIDER=${ALVYS_PROVIDER:-Fallback}
ALVYS_API_BASE_URL=${ALVYS_API_BASE_URL:-https://integrations.alvys.com}
ALVYS_API_VERSION=${ALVYS_API_VERSION:-v1}
ALVYS_CLIENT_ID=${ALVYS_CLIENT_ID:-}
ALVYS_CLIENT_SECRET=${ALVYS_CLIENT_SECRET:-}
ALVYS_TENANT_ID=${ALVYS_TENANT_ID:-}
ALVYS_WRITEBACK_MODE=${ALVYS_WRITEBACK_MODE:-Disabled}
LTL_DETECTION_ENABLED=${LTL_DETECTION_ENABLED:-false}
LTL_DEFAULT_TIMEZONE=${LTL_DEFAULT_TIMEZONE:-America/Chicago}

required=(SQL_PASSWORD AZURE_AD_TENANT_ID AZURE_AD_API_CLIENT_ID AZURE_AD_CLIENT_SECRET AZURE_AD_WEB_CLIENT_ID)
for name in "${required[@]}"; do
  if [ -z "${!name:-}" ]; then
    echo "Missing required environment variable: $name" >&2
    echo "Usage: SQL_PASSWORD=... AZURE_AD_TENANT_ID=... AZURE_AD_API_CLIENT_ID=... AZURE_AD_CLIENT_SECRET=... AZURE_AD_WEB_CLIENT_ID=... $0" >&2
    exit 1
  fi
done

for cmd in az jq; do
  command -v "$cmd" >/dev/null 2>&1 || { echo "$cmd is required." >&2; exit 1; }
done

echo "Resolving ACR and SQL server in $RG..."
ACR_NAME=$(az acr list --resource-group "$RG" --query "[0].name" -o tsv)
SQL_SERVER=$(az sql server list --resource-group "$RG" --query "[0].name" -o tsv)
if [ -z "$ACR_NAME" ] || [ -z "$SQL_SERVER" ]; then
  echo "Could not find an ACR and/or SQL server in resource group $RG. Run" >&2
  echo "provision-ltl-uat-infra.yml (infra/uat/main.bicep) first." >&2
  exit 1
fi
echo "  ACR:  $ACR_NAME"
echo "  SQL:  $SQL_SERVER"

echo "Building and pushing API image (tag ${IMAGE_TAG})..."
az acr build --registry "$ACR_NAME" --resource-group "$RG" \
  --image "ltl-api:${IMAGE_TAG}" --file src/LtlTool.Api/Dockerfile .

echo "Building and pushing Web image (tag ${IMAGE_TAG})..."
az acr build --registry "$ACR_NAME" --resource-group "$RG" \
  --image "ltl-web:${IMAGE_TAG}" --file web/Dockerfile .

ACR_LOGIN_SERVER=$(az acr show --name "$ACR_NAME" --resource-group "$RG" --query loginServer -o tsv)
ACR_USER=$(az acr credential show --name "$ACR_NAME" --resource-group "$RG" --query username -o tsv)
ACR_PASS=$(az acr credential show --name "$ACR_NAME" --resource-group "$RG" --query "passwords[0].value" -o tsv)

echo "Pointing $API_APP at the new API image..."
az webapp config container set --name "$API_APP" --resource-group "$RG" \
  --docker-custom-image-name "${ACR_LOGIN_SERVER}/ltl-api:${IMAGE_TAG}" \
  --docker-registry-server-url "https://${ACR_LOGIN_SERVER}" \
  --docker-registry-server-user "$ACR_USER" \
  --docker-registry-server-password "$ACR_PASS" >/dev/null

echo "Pointing $WEB_APP at the new Web image..."
az webapp config container set --name "$WEB_APP" --resource-group "$RG" \
  --docker-custom-image-name "${ACR_LOGIN_SERVER}/ltl-web:${IMAGE_TAG}" \
  --docker-registry-server-url "https://${ACR_LOGIN_SERVER}" \
  --docker-registry-server-user "$ACR_USER" \
  --docker-registry-server-password "$ACR_PASS" >/dev/null

SQL_CONNECTION_STRING="Server=tcp:${SQL_SERVER}.database.windows.net,1433;Initial Catalog=${SQL_DB};Persist Security Info=False;User ID=${SQL_ADMIN};Password=${SQL_PASSWORD};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

echo "Fetching app URLs..."
WEB_FQDN=$(az webapp show --name "$WEB_APP" --resource-group "$RG" --query defaultHostName -o tsv)
API_FQDN=$(az webapp show --name "$API_APP" --resource-group "$RG" --query defaultHostName -o tsv)
WEB_URL="https://${WEB_FQDN}"
API_URL="https://${API_FQDN}"

echo "Configuring $API_APP application settings..."
az webapp config appsettings set --name "$API_APP" --resource-group "$RG" --settings \
  WEBSITES_PORT=8080 \
  ASPNETCORE_ENVIRONMENT=Production \
  AzureAd__TenantId="$AZURE_AD_TENANT_ID" \
  AzureAd__ClientId="$AZURE_AD_API_CLIENT_ID" \
  AzureAd__ClientSecret="$AZURE_AD_CLIENT_SECRET" \
  AzureAd__Instance=https://login.microsoftonline.com/ \
  AzureAd__Audience="api://$AZURE_AD_API_CLIENT_ID" \
  ConnectionStrings__DefaultConnection="$SQL_CONNECTION_STRING" \
  Cors__AllowedOrigins__0="$WEB_URL" \
  AccessPolicy__AllowedEmailDomains__0="$ALLOWED_EMAIL_DOMAIN" \
  Alvys__Provider="$ALVYS_PROVIDER" \
  Alvys__ApiBaseUrl="$ALVYS_API_BASE_URL" \
  Alvys__ApiVersion="$ALVYS_API_VERSION" \
  Alvys__TenantId="$ALVYS_TENANT_ID" \
  Alvys__ClientId="$ALVYS_CLIENT_ID" \
  Alvys__ClientSecret="$ALVYS_CLIENT_SECRET" \
  Alvys__Writeback__Mode="$ALVYS_WRITEBACK_MODE" \
  Ltl__DetectionEnabled="$LTL_DETECTION_ENABLED" \
  Ltl__DefaultTimezone="$LTL_DEFAULT_TIMEZONE" \
  >/dev/null

echo "Configuring $WEB_APP application settings..."
az webapp config appsettings set --name "$WEB_APP" --resource-group "$RG" --settings \
  WEBSITES_PORT=80 \
  API_UPSTREAM="$API_URL" \
  RUNTIME_TENANT_ID="$AZURE_AD_TENANT_ID" \
  RUNTIME_WEB_CLIENT_ID="$AZURE_AD_WEB_CLIENT_ID" \
  RUNTIME_API_SCOPE="api://${AZURE_AD_API_CLIENT_ID}/access_as_user" \
  RUNTIME_API_BASE_URL=/api \
  >/dev/null

echo "Restarting $API_APP and $WEB_APP..."
az webapp restart --name "$API_APP" --resource-group "$RG" >/dev/null
az webapp restart --name "$WEB_APP" --resource-group "$RG" >/dev/null

echo ""
echo "Done. Web:    $WEB_URL"
echo "      API:    $API_URL"
echo "      Health: ${API_URL}/api/health"
echo ""
echo "Next: confirm the ${BASE_NAME}-web Entra app registration's SPA redirect URIs include"
echo "  $WEB_URL and $WEB_URL/ (Entra ID -> App registrations -> ${BASE_NAME}-web -> Authentication)."
