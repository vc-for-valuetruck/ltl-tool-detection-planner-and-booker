#!/usr/bin/env bash
# Configure AccessPolicy__AllowedEmailDomains__* on the LTL UAT API app service from a
# comma-separated list of email domains. Called from:
#   - scripts/deploy-azure-uat.sh (as part of every deploy)
#   - .github/workflows/set-uat-allowed-email-domains.yml (standalone workflow_dispatch)
#
# Behavior:
#   1) Deletes any existing AccessPolicy__AllowedEmailDomains__* keys first — required so
#      shrinking a 3-domain list to 1 doesn't leave stale __1/__2 keys quietly admitting
#      domains you thought you'd removed.
#   2) If the resolved list is non-empty, splits on commas, trims, lowercases, drops blanks,
#      de-dupes, and re-writes AccessPolicy__AllowedEmailDomains__0/__1/__2/…. .NET's config
#      array binder rehydrates those into AccessPolicyOptions.AllowedEmailDomains[].
#   3) If the resolved list is empty, leaves the app setting absent — AllowedEmailDomainHandler.cs
#      treats an empty allow-list as 'admit any authenticated user in the tenant', which is
#      what we want here. Never leaves an empty '' value (the handler would treat that as a
#      one-element list containing '' and reject everyone).
#
# Environment inputs:
#   RG                     required — Azure resource group (e.g. ltl-uat-rg)
#   API_APP                required — API app service name (e.g. ltl-uat-api)
#   DOMAINS                required — comma-separated list of email domains. Empty string is
#                                     legal and means 'admit all authenticated'.
#   DRY_RUN                optional — set to 1 to print the intended az calls without executing.
#
# Exit codes:
#   0 on success (including no-op when the resolved list is already applied).
#   Non-zero on az failure or missing required env.

set -euo pipefail

: "${RG:?RG is required}"
: "${API_APP:?API_APP is required}"
DOMAINS="${DOMAINS-}"
DRY_RUN="${DRY_RUN:-0}"

run_az() {
  if [ "$DRY_RUN" = "1" ]; then
    printf '[dry-run] az %s\n' "$*"
  else
    az "$@"
  fi
}

# Normalize the input list. Empty output means 'admit all authenticated' (skip setting).
normalize_domains() {
  local raw="$1"
  printf '%s' "$raw" | tr ',' '\n' \
    | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' \
    | tr '[:upper:]' '[:lower:]' \
    | awk 'NF && !seen[$0]++'
}

NORMALIZED=$(normalize_domains "$DOMAINS")

# Step 1: delete existing __N keys so a shrinking list doesn't leave stale entries.
EXISTING_KEYS=$(az webapp config appsettings list --name "$API_APP" --resource-group "$RG" \
  --query "[?starts_with(name, 'AccessPolicy__AllowedEmailDomains__')].name" -o tsv 2>/dev/null || true)
if [ -n "$EXISTING_KEYS" ]; then
  echo "Removing existing AccessPolicy__AllowedEmailDomains__* keys:"
  printf '  - %s\n' $EXISTING_KEYS
  # shellcheck disable=SC2086
  run_az webapp config appsettings delete --name "$API_APP" --resource-group "$RG" \
    --setting-names $EXISTING_KEYS >/dev/null
fi

# Step 2: apply the new list (if any).
if [ -z "$NORMALIZED" ]; then
  if [ -z "$DOMAINS" ]; then
    echo "DOMAINS is empty — API will admit any authenticated user in the tenant."
  else
    echo "DOMAINS='$DOMAINS' normalized to an empty list — API will admit any authenticated user in the tenant."
  fi
  exit 0
fi

echo "Restricting $API_APP access to email domain(s):"
printf '  • %s\n' $NORMALIZED

SETTINGS_ARGS=""
INDEX=0
while IFS= read -r DOMAIN; do
  SETTINGS_ARGS="$SETTINGS_ARGS AccessPolicy__AllowedEmailDomains__${INDEX}=${DOMAIN}"
  INDEX=$((INDEX + 1))
done <<EOF
$NORMALIZED
EOF

# shellcheck disable=SC2086
run_az webapp config appsettings set --name "$API_APP" --resource-group "$RG" --settings $SETTINGS_ARGS >/dev/null

echo "::notice::$API_APP: applied $INDEX AllowedEmailDomain entrie(s) (app restarts to pick up config)."
