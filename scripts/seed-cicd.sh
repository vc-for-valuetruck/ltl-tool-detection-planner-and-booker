#!/usr/bin/env bash
# seed-cicd.sh
#
# One-time bootstrap for auto-deploy from GitHub Actions to the ltl-standalone
# stack. Grants the ltl-uat-github-deploy service principal the two least-
# privilege roles it needs to build ACR images and reconfigure App Services,
# then triggers the deploy-standalone-images workflow so the next push to main
# (or any manual dispatch) deploys itself with zero human involvement.
#
# Idempotent — safe to re-run. Existing role assignments are detected and
# skipped. If a role can't be assigned because the caller doesn't have User
# Access Administrator at that scope, the script surfaces exactly which
# assignment failed and stops.
#
# Requires:
#   - Azure CLI logged in as a user with role-assignment rights on the RG.
#     (Contributor is enough to see the resources but you need role-based
#      permissions to grant. Owner OR User Access Administrator works.)
#   - GitHub CLI logged in (for the workflow trigger + status poll).
#
# Usage from Azure Cloud Shell:
#   curl -sL tinyurl.com/24u6vgtn/seed-cicd | bash
#   (or clone the repo and run bash scripts/seed-cicd.sh)

set -euo pipefail

SP_CLIENT_ID="eda7036b-7596-4c2a-836c-599fcd3a5166"  # ltl-uat-github-deploy
SUBSCRIPTION_ID="9dfdd151-fd80-4116-8c57-16f1d7156ded"
RG="ltl-standalone-rg"
ACR_NAME="ltlstandaloneacr"
REPO="vc-for-valuetruck/ltl-tool-detection-planner-and-booker"
WORKFLOW="deploy-standalone-images.yml"

log() { printf "\n[seed-cicd] %s\n" "$*"; }
ok()  { printf "[seed-cicd] \033[32m✓\033[0m %s\n" "$*"; }
err() { printf "[seed-cicd] \033[31m✗\033[0m %s\n" "$*" >&2; }

# 1. Confirm Azure login + subscription
log "Verifying Azure context..."
if ! az account show >/dev/null 2>&1; then
    err "Not signed in to Azure. Run 'az login' first."
    exit 1
fi
az account set --subscription "$SUBSCRIPTION_ID"
current=$(az account show --query name -o tsv)
ok "Signed in to '$current' ($SUBSCRIPTION_ID)"

# 2. Look up the SP object id (needed for role assignments — client id alone
#    doesn't always resolve on newer az CLI versions).
log "Resolving service principal object id..."
SP_OBJECT_ID=$(az ad sp show --id "$SP_CLIENT_ID" --query id -o tsv 2>/dev/null || true)
if [[ -z "$SP_OBJECT_ID" ]]; then
    err "Could not resolve service principal '$SP_CLIENT_ID'. Check the client id."
    exit 1
fi
ok "Service principal object id: $SP_OBJECT_ID"

# 3. Look up ACR resource id.
log "Resolving ACR..."
ACR_ID=$(az acr show -n "$ACR_NAME" -g "$RG" --query id -o tsv 2>/dev/null || true)
if [[ -z "$ACR_ID" ]]; then
    err "ACR '$ACR_NAME' not found in RG '$RG'. Was the infra provisioned?"
    exit 1
fi
ok "ACR: $ACR_ID"

# 4. Resource group scope for the App Service role.
RG_SCOPE="/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RG}"

# 5. Grant AcrPush on the ACR (idempotent).
assign_role() {
    local role="$1" scope="$2" desc="$3"
    log "Granting '$role' on $desc..."
    existing=$(az role assignment list \
        --assignee-object-id "$SP_OBJECT_ID" \
        --assignee-principal-type ServicePrincipal \
        --role "$role" \
        --scope "$scope" \
        --query "[].id" -o tsv 2>/dev/null || true)
    if [[ -n "$existing" ]]; then
        ok "'$role' already assigned on $desc — skipping"
        return 0
    fi
    if az role assignment create \
        --assignee-object-id "$SP_OBJECT_ID" \
        --assignee-principal-type ServicePrincipal \
        --role "$role" \
        --scope "$scope" \
        --output none 2>/tmp/roleerr; then
        ok "Granted '$role' on $desc"
        return 0
    fi
    err "Failed to grant '$role' on $desc:"
    cat /tmp/roleerr >&2
    if grep -qi "AuthorizationFailed\|does not have authorization" /tmp/roleerr; then
        err "You don't have the 'Microsoft.Authorization/roleAssignments/write' permission at this scope."
        err "Ask an Owner or User Access Administrator on the subscription to run:"
        err ""
        err "  az role assignment create \\"
        err "    --assignee $SP_CLIENT_ID \\"
        err "    --role '$role' \\"
        err "    --scope '$scope'"
    fi
    return 1
}

assign_role "AcrPush" "$ACR_ID" "$ACR_NAME"
assign_role "Website Contributor" "$RG_SCOPE" "resource group $RG"

# 6. Trigger the workflow so we actually see it succeed.
log "Triggering GitHub Actions deploy workflow..."
if ! command -v gh >/dev/null 2>&1; then
    err "GitHub CLI ('gh') not installed. Workflow will trigger on the next push to main."
    exit 0
fi

if ! gh auth status >/dev/null 2>&1; then
    log "gh not authenticated — attempting device-flow login..."
    gh auth login --hostname github.com --git-protocol https --web
fi

gh workflow run "$WORKFLOW" --repo "$REPO" --ref main
ok "Workflow triggered"

sleep 8
RUN_ID=$(gh run list --repo "$REPO" --workflow "$WORKFLOW" --limit 1 --json databaseId --jq '.[0].databaseId')
echo ""
ok "Run id: $RUN_ID"
echo "    Watch it live at: https://github.com/${REPO}/actions/runs/${RUN_ID}"
echo ""

log "Polling for completion (up to 6 minutes)..."
deadline=$(( $(date +%s) + 360 ))
while [ $(date +%s) -lt $deadline ]; do
    sleep 20
    status=$(gh run view "$RUN_ID" --repo "$REPO" --json status,conclusion --jq '.status + " " + (.conclusion // "-")')
    printf "    %s @ %s\n" "$status" "$(date +%H:%M:%S)"
    case "$status" in
        completed*success)
            echo ""
            ok "🎉 Deploy complete."
            echo ""
            echo "  Web:    https://ltl-standalone-web.azurewebsites.net"
            echo "  API:    https://ltl-standalone-api.azurewebsites.net/api"
            echo "  Health: https://ltl-standalone-api.azurewebsites.net/api/health"
            echo ""
            echo "  From now on, every push to main auto-deploys."
            echo "  Manually re-run any time from GitHub Actions UI or:"
            echo "    gh workflow run $WORKFLOW --repo $REPO --ref main"
            exit 0
            ;;
        completed*failure|completed*cancelled|completed*timed_out)
            echo ""
            err "Workflow failed. See run for details:"
            echo "    https://github.com/${REPO}/actions/runs/${RUN_ID}"
            exit 1
            ;;
    esac
done

err "Timed out waiting for workflow completion. Check status manually."
exit 1
