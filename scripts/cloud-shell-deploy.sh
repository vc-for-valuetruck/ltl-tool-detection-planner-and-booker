#!/usr/bin/env bash
# cloud-shell-deploy.sh
# =====================
# ONE-SHOT LTL pilot deployment for Azure Cloud Shell (browser terminal).
#
# Provisions a fully isolated ltl-standalone-rg with its own ACR, SQL server,
# App Service Plan, and two App Services; builds the API + Web images via
# ACR Tasks (no local Docker needed); wires app settings for Demo mode by
# default so the stack is publicly reachable at a real Azure URL under a
# synthetic demo@valuetruck.com identity, with live Alvys reads against va336
# and writeback hard-disabled.
#
# HOW TO USE
# ----------
# 1. Open https://shell.azure.com (Bash mode).
# 2. Paste this script into a file, save it, chmod +x, and run:
#        curl -O https://raw.githubusercontent.com/vc-for-valuetruck/ltl-tool-detection-planner-and-booker/main/scripts/cloud-shell-deploy.sh
#        chmod +x cloud-shell-deploy.sh
#        ./cloud-shell-deploy.sh
# 3. When prompted, paste your Alvys va336 client secret and pick a SQL admin
#    password. Nothing is persisted to disk in Cloud Shell beyond the run.
#
# The script prints the public web URL, API URL, and /api/health URL at the
# end. Verify /api/health returns {"authMode":"Demo"} to confirm the stack
# is up correctly.
#
# WHY DEMO MODE BY DEFAULT
# ------------------------
# Entra Contributor role assignment for ltl-uat-rg is still pending. The demo
# handler (DemoAuthenticationHandler, PR #61) admits every request under a
# synthetic identity so leadership can walk the pilot workflow today without
# waiting on IT. Four independent guardrails prevent accidental Demo shipping
# to production surfaces:
#   1. Code default is EntraId; Demo requires explicit opt-in via env.
#   2. API startup logs a multi-line warning banner.
#   3. /api/health publishes authMode: "Demo" | "EntraId".
#   4. DemoAuthenticationHandler is a separate .cs file, wired via the
#      AuthenticationSchemeRouter only when mode matches.
#
# TO FLIP TO ENTRAID LATER
# ------------------------
# Re-run with ACCESS_POLICY_MODE=EntraId set:
#     export ACCESS_POLICY_MODE=EntraId
#     export AZURE_AD_TENANT_ID="<tenant guid>"
#     export AZURE_AD_API_CLIENT_ID="<api client guid>"
#     export AZURE_AD_WEB_CLIENT_ID="<web client guid>"
#     export AZURE_AD_CLIENT_SECRET="<web app secret>"
#     ./cloud-shell-deploy.sh
# Same script, same infra, different config. No rebuild.
#
# TEAR DOWN
# ---------
#     az group delete --name ltl-standalone-rg --yes --no-wait

set -euo pipefail

# ---- 0. banner --------------------------------------------------------------

cat <<'EOF'
============================================================
  LTL Pilot — Azure Cloud Shell one-shot deploy
============================================================
Provisions ltl-standalone-rg, builds images via ACR Tasks (no
local Docker), wires app settings for Demo mode + live va336
Alvys reads. Total time: ~10-15 minutes.
EOF
echo ""

# ---- 1. parameters (env vars override defaults) -----------------------------

: "${SUBSCRIPTION_ID:=9dfdd151-fd80-4116-8c57-16f1d7156ded}"
: "${LOCATION:=centralus}"
: "${BASE_NAME:=ltl-standalone}"
: "${RESOURCE_GROUP:=ltl-standalone-rg}"

# Alvys — read-only against va336. Client id is public; secret comes from prompt.
: "${ALVYS_TENANT_ID:=va336}"
: "${ALVYS_CLIENT_ID:=MZEDvQYVZmcEtk3f17QFgYTg19pa3eJL}"
: "${ALVYS_API_BASE_URL:=https://integrations.alvys.com}"
: "${ALVYS_API_VERSION:=v1}"
: "${ALVYS_PROVIDER:=Live}"
: "${ALVYS_WRITEBACK_MODE:=Disabled}"

# Access policy: Demo by default. EntraId requires the four AZURE_AD_* vars.
: "${ACCESS_POLICY_MODE:=Demo}"
: "${AZURE_AD_TENANT_ID:=00000000-0000-0000-0000-000000000000}"
: "${AZURE_AD_API_CLIENT_ID:=00000000-0000-0000-0000-000000000000}"
: "${AZURE_AD_WEB_CLIENT_ID:=00000000-0000-0000-0000-000000000000}"
: "${AZURE_AD_CLIENT_SECRET:=}"
: "${ALLOWED_EMAIL_DOMAIN:=valuetruck.com}"

# Where to fetch source from. Cloud Shell can either clone the repo, or use a
# tarball if the repo is private. We default to a shallow clone.
: "${REPO_URL:=https://github.com/vc-for-valuetruck/ltl-tool-detection-planner-and-booker.git}"
: "${REPO_REF:=main}"

IMAGE_TAG="$(date +%Y%m%d%H%M%S)"

log()  { printf "\033[36m[deploy]\033[0m %s\n" "$*"; }
warn() { printf "\033[33m[deploy]\033[0m %s\n" "$*"; }
err()  { printf "\033[31m[deploy]\033[0m %s\n" "$*" >&2; }

# ---- 2. sanity: az login + subscription -------------------------------------

log "Verifying Azure login..."
if ! az account show >/dev/null 2>&1; then
    warn "Not logged in. Running 'az login'..."
    az login --output none
fi
CURRENT_SUB=$(az account show --query id -o tsv)
if [[ "$CURRENT_SUB" != "$SUBSCRIPTION_ID" ]]; then
    log "Switching to subscription $SUBSCRIPTION_ID..."
    az account set --subscription "$SUBSCRIPTION_ID"
fi
CURRENT_NAME=$(az account show --query name -o tsv)
log "Signed in on subscription: $CURRENT_NAME ($SUBSCRIPTION_ID)"

# ---- 3. prompt for secrets --------------------------------------------------

if [[ -z "${ALVYS_CLIENT_SECRET:-}" ]]; then
    echo ""
    echo "Enter your Alvys va336 client secret (input hidden):"
    read -rs ALVYS_CLIENT_SECRET
    echo ""
fi
if [[ -z "${ALVYS_CLIENT_SECRET:-}" ]]; then
    err "ALVYS_CLIENT_SECRET is required."
    exit 1
fi

# Stable SQL admin password for the ltl-standalone pilot. Keeping this fixed lets
# subsequent runs be fully non-interactive; Bicep is idempotent so re-passing the
# same value is a no-op after the first provision. Override by exporting
# SQL_ADMIN_PASSWORD before running the script. Meets Azure SQL rules:
# 8-128 chars, three of upper/lower/digit/symbol, no 'ltlsqladmin'.
# Use SINGLE quotes for the default so $ characters are treated literally by bash.
SQL_ADMIN_PASSWORD="${SQL_ADMIN_PASSWORD:-$(printf '%s' 'Vt#Ltl-Pilot-2026-Az')}"

if [[ "$ACCESS_POLICY_MODE" == "EntraId" && -z "${AZURE_AD_CLIENT_SECRET:-}" ]]; then
    err "ACCESS_POLICY_MODE=EntraId requires AZURE_AD_CLIENT_SECRET (env var)."
    exit 1
fi

# ---- 4. clone or refresh the repo in Cloud Shell -----------------------------

# Cloud Shell has a persistent $HOME across sessions. Clone to a stable path so
# re-runs re-use the working tree instead of re-cloning every time.
WORK_DIR="$HOME/ltl-tool-detection-planner-and-booker"
if [[ -d "$WORK_DIR/.git" ]]; then
    log "Refreshing existing repo at $WORK_DIR..."
    git -C "$WORK_DIR" fetch --depth 1 origin "$REPO_REF"
    git -C "$WORK_DIR" reset --hard "origin/$REPO_REF"
else
    log "Cloning $REPO_URL @ $REPO_REF into $WORK_DIR..."
    git clone --depth 1 --branch "$REPO_REF" "$REPO_URL" "$WORK_DIR"
fi
cd "$WORK_DIR"

# ---- 5. provision infra via Bicep -------------------------------------------

log "Ensuring resource group $RESOURCE_GROUP exists in $LOCATION..."
if ! az group show --name "$RESOURCE_GROUP" >/dev/null 2>&1; then
    az group create --name "$RESOURCE_GROUP" --location "$LOCATION" \
        --tags "project=ltl-standalone" "createdBy=cloud-shell-deploy.sh" \
        --output none
    log "Created resource group."
else
    log "Resource group already exists."
fi

DEPLOYMENT_NAME="ltl-standalone-$(date +%Y%m%d-%H%M%S)"
log "Deploying Bicep template ($DEPLOYMENT_NAME). Takes 4-8 min..."
az deployment group create \
    --resource-group "$RESOURCE_GROUP" \
    --name "$DEPLOYMENT_NAME" \
    --template-file "$WORK_DIR/infra/uat/main.bicep" \
    --parameters "baseName=$BASE_NAME" "sqlAdminPassword=$SQL_ADMIN_PASSWORD" "location=$LOCATION" \
    --output none

log "Infra provisioned."

# ---- 6. discover ACR + apps -------------------------------------------------

ACR_NAME=$(az acr list --resource-group "$RESOURCE_GROUP" --query "[0].name" -o tsv)
SQL_SERVER=$(az sql server list --resource-group "$RESOURCE_GROUP" --query "[0].name" -o tsv)
API_APP="${BASE_NAME}-api"
WEB_APP="${BASE_NAME}-web"
SQL_DB="${BASE_NAME}-sqldb"
SQL_ADMIN="ltlsqladmin"

if [[ -z "$ACR_NAME" ]]; then err "ACR not found in $RESOURCE_GROUP after infra deploy."; exit 1; fi
if [[ -z "$SQL_SERVER" ]]; then err "SQL server not found in $RESOURCE_GROUP."; exit 1; fi

ACR_LOGIN_SERVER=$(az acr show --name "$ACR_NAME" --resource-group "$RESOURCE_GROUP" --query loginServer -o tsv)
log "Discovered: ACR=$ACR_NAME ($ACR_LOGIN_SERVER); SQL=$SQL_SERVER; API=$API_APP; Web=$WEB_APP"

# ---- 7. build images with ACR Tasks (no local Docker required) --------------

log "Building API image via ACR Task ($ACR_NAME/ltl-api:$IMAGE_TAG)..."
az acr build \
    --registry "$ACR_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --image "ltl-api:$IMAGE_TAG" \
    --file "src/LtlTool.Api/Dockerfile" \
    --output none \
    .

log "Building Web image via ACR Task ($ACR_NAME/ltl-web:$IMAGE_TAG)..."
az acr build \
    --registry "$ACR_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --image "ltl-web:$IMAGE_TAG" \
    --file "web/Dockerfile" \
    --output none \
    .

log "Both images built and pushed to $ACR_LOGIN_SERVER."

# ---- 8. point App Services at the new images --------------------------------

ACR_USER=$(az acr credential show --name "$ACR_NAME" --resource-group "$RESOURCE_GROUP" --query username -o tsv)
ACR_PASS=$(az acr credential show --name "$ACR_NAME" --resource-group "$RESOURCE_GROUP" --query "passwords[0].value" -o tsv)

log "Pointing $API_APP at $ACR_LOGIN_SERVER/ltl-api:$IMAGE_TAG..."
az webapp config container set --name "$API_APP" --resource-group "$RESOURCE_GROUP" \
    --docker-custom-image-name    "$ACR_LOGIN_SERVER/ltl-api:$IMAGE_TAG" \
    --docker-registry-server-url  "https://$ACR_LOGIN_SERVER" \
    --docker-registry-server-user "$ACR_USER" \
    --docker-registry-server-password "$ACR_PASS" \
    --output none

log "Pointing $WEB_APP at $ACR_LOGIN_SERVER/ltl-web:$IMAGE_TAG..."
az webapp config container set --name "$WEB_APP" --resource-group "$RESOURCE_GROUP" \
    --docker-custom-image-name    "$ACR_LOGIN_SERVER/ltl-web:$IMAGE_TAG" \
    --docker-registry-server-url  "https://$ACR_LOGIN_SERVER" \
    --docker-registry-server-user "$ACR_USER" \
    --docker-registry-server-password "$ACR_PASS" \
    --output none

# ---- 9. app settings --------------------------------------------------------

SQL_CONN_STR="Server=tcp:${SQL_SERVER}.database.windows.net,1433;Initial Catalog=${SQL_DB};Persist Security Info=False;User ID=${SQL_ADMIN};Password=${SQL_ADMIN_PASSWORD};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

WEB_FQDN=$(az webapp show --name "$WEB_APP" --resource-group "$RESOURCE_GROUP" --query defaultHostName -o tsv)
API_FQDN=$(az webapp show --name "$API_APP" --resource-group "$RESOURCE_GROUP" --query defaultHostName -o tsv)
WEB_URL="https://$WEB_FQDN"
API_URL="https://$API_FQDN"

log "Configuring $API_APP application settings (AccessPolicy:Mode=$ACCESS_POLICY_MODE)..."
az webapp config appsettings set --name "$API_APP" --resource-group "$RESOURCE_GROUP" --output none --settings \
    WEBSITES_PORT=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    "AccessPolicy__Mode=$ACCESS_POLICY_MODE" \
    "AccessPolicy__AllowedEmailDomains__0=$ALLOWED_EMAIL_DOMAIN" \
    "AzureAd__TenantId=$AZURE_AD_TENANT_ID" \
    "AzureAd__ClientId=$AZURE_AD_API_CLIENT_ID" \
    "AzureAd__ClientSecret=$AZURE_AD_CLIENT_SECRET" \
    AzureAd__Instance="https://login.microsoftonline.com/" \
    "AzureAd__Audience=api://$AZURE_AD_API_CLIENT_ID" \
    "ConnectionStrings__DefaultConnection=$SQL_CONN_STR" \
    "Cors__AllowedOrigins__0=$WEB_URL" \
    "Alvys__Provider=$ALVYS_PROVIDER" \
    "Alvys__ApiBaseUrl=$ALVYS_API_BASE_URL" \
    "Alvys__ApiVersion=$ALVYS_API_VERSION" \
    "Alvys__TenantId=$ALVYS_TENANT_ID" \
    "Alvys__ClientId=$ALVYS_CLIENT_ID" \
    "Alvys__ClientSecret=$ALVYS_CLIENT_SECRET" \
    "Alvys__Writeback__Mode=$ALVYS_WRITEBACK_MODE" \
    Ltl__DetectionEnabled=true \
    Ltl__DefaultTimezone=America/Chicago

log "Configuring $WEB_APP application settings..."
if [[ "$ACCESS_POLICY_MODE" == "Demo" ]]; then
    # Blank RUNTIME_* keys suppress the SPA's MSAL redirect.
    az webapp config appsettings set --name "$WEB_APP" --resource-group "$RESOURCE_GROUP" --output none --settings \
        WEBSITES_PORT=80 \
        "API_UPSTREAM=$API_URL" \
        RUNTIME_TENANT_ID="" \
        RUNTIME_WEB_CLIENT_ID="" \
        RUNTIME_API_SCOPE="" \
        RUNTIME_API_BASE_URL=/api
else
    az webapp config appsettings set --name "$WEB_APP" --resource-group "$RESOURCE_GROUP" --output none --settings \
        WEBSITES_PORT=80 \
        "API_UPSTREAM=$API_URL" \
        "RUNTIME_TENANT_ID=$AZURE_AD_TENANT_ID" \
        "RUNTIME_WEB_CLIENT_ID=$AZURE_AD_WEB_CLIENT_ID" \
        "RUNTIME_API_SCOPE=api://$AZURE_AD_API_CLIENT_ID/access_as_user" \
        RUNTIME_API_BASE_URL=/api
fi

# ---- 10. restart --------------------------------------------------------------

log "Restarting App Services..."
az webapp restart --name "$API_APP" --resource-group "$RESOURCE_GROUP" --output none
az webapp restart --name "$WEB_APP" --resource-group "$RESOURCE_GROUP" --output none

# ---- 11. wait for API to report healthy + verify authMode -------------------

log "Waiting for API to report healthy (max 4 minutes)..."
DEADLINE=$(( $(date +%s) + 240 ))
HEALTH_BODY=""
while true; do
    HEALTH_BODY=$(curl -fsS "$API_URL/api/health" 2>/dev/null || true)
    if [[ -n "$HEALTH_BODY" ]]; then
        break
    fi
    if [[ $(date +%s) -gt $DEADLINE ]]; then
        err "API did not report healthy within 4 minutes."
        err "Check portal logs: https://portal.azure.com/#@/resource/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.Web/sites/$API_APP/logStream"
        exit 1
    fi
    sleep 6
done

AUTH_MODE=$(printf '%s' "$HEALTH_BODY" | sed -n 's/.*"authMode"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
if [[ "$AUTH_MODE" != "$ACCESS_POLICY_MODE" ]]; then
    err "SMOKE TEST FAILED: /api/health reports authMode=$AUTH_MODE, expected $ACCESS_POLICY_MODE."
    err "Health body: $HEALTH_BODY"
    exit 1
fi

# ---- 12. summary -------------------------------------------------------------

cat <<EOF

============================================================
  Deployed. Give App Service another ~30s if the SPA is 502.

  Web:    $WEB_URL
  API:    $API_URL
  Health: $API_URL/api/health   (authMode=$AUTH_MODE, verified)

  Tenant: va336 (live Alvys reads)
  Writes: DISABLED (Alvys writeback boundary hard-off)
============================================================
EOF

if [[ "$ACCESS_POLICY_MODE" == "Demo" ]]; then
    cat <<'EOF'

  DEMO MODE ARMED. Every request is authenticated as
  demo@valuetruck.com under a synthetic identity. Do not
  expose this URL to the public internet. Flip to EntraId
  when the Entra Contributor role assignment lands:

    export ACCESS_POLICY_MODE=EntraId
    export AZURE_AD_TENANT_ID="<tenant guid>"
    export AZURE_AD_API_CLIENT_ID="<api client guid>"
    export AZURE_AD_WEB_CLIENT_ID="<web client guid>"
    export AZURE_AD_CLIENT_SECRET="<web app secret>"
    ./cloud-shell-deploy.sh
EOF
fi

cat <<EOF

  Tear down when done:
    az group delete --name $RESOURCE_GROUP --yes --no-wait
EOF
