#!/usr/bin/env bash
# scripts/redeploy.sh
#
# One-shot repo-refresh + redeploy for the LTL standalone Azure stack.
# Designed to be pasted into Azure Cloud Shell (Bash) as a single line:
#
#   curl -sL https://vt.pplx.app/deploy | bash
#
# ...or run from an already-cloned repo:
#
#   bash scripts/redeploy.sh
#
# The script:
#   1. Ensures the repo is present (clones fresh into ~/ltl-tool-detection-planner-and-booker)
#   2. Reads ALVYS_CLIENT_SECRET from env, or from ~/.ltl-secrets, or prompts.
#      Persists it in ~/.ltl-secrets (mode 600) so future runs are non-interactive.
#   3. Calls scripts/cloud-shell-deploy.sh
#
# ~/.ltl-secrets is a simple KEY=VALUE file with permissions 600.

set -euo pipefail

REPO="vc-for-valuetruck/ltl-tool-detection-planner-and-booker"
REPO_DIR="$HOME/ltl-tool-detection-planner-and-booker"
SECRETS_FILE="$HOME/.ltl-secrets"

log()  { echo "[redeploy] $*"; }
err()  { echo "[redeploy] ERROR: $*" >&2; }

# 1. clone or refresh repo
if [[ -d "$REPO_DIR/.git" ]]; then
    log "Refreshing repo at $REPO_DIR..."
    cd "$REPO_DIR"
    git fetch --quiet origin main
    git reset --quiet --hard origin/main
else
    log "Cloning repo to $REPO_DIR..."
    rm -rf "$REPO_DIR"
    git clone --quiet "https://github.com/${REPO}.git" "$REPO_DIR"
    cd "$REPO_DIR"
fi

# 2. resolve ALVYS_CLIENT_SECRET
if [[ -z "${ALVYS_CLIENT_SECRET:-}" && -f "$SECRETS_FILE" ]]; then
    # shellcheck disable=SC1090
    source "$SECRETS_FILE"
    log "Loaded ALVYS_CLIENT_SECRET from $SECRETS_FILE"
fi

if [[ -z "${ALVYS_CLIENT_SECRET:-}" ]]; then
    echo ""
    echo "First-time setup — paste the Alvys va336 client secret (input hidden):"
    read -rs ALVYS_CLIENT_SECRET
    echo ""
    if [[ -z "${ALVYS_CLIENT_SECRET:-}" ]]; then
        err "ALVYS_CLIENT_SECRET is required."
        exit 1
    fi
    umask 077
    cat > "$SECRETS_FILE" <<EOF
# LTL standalone deploy secrets — persisted so redeploy.sh is non-interactive.
# Delete this file if you rotate the Alvys client secret.
ALVYS_CLIENT_SECRET='$ALVYS_CLIENT_SECRET'
EOF
    log "Saved to $SECRETS_FILE (600). Future redeploys will not prompt."
fi
export ALVYS_CLIENT_SECRET

# 3. run the underlying deploy
log "Kicking off cloud-shell-deploy.sh..."
exec bash scripts/cloud-shell-deploy.sh
