#!/usr/bin/env bash
# Cloud Shell one-liner launcher for the LTL standalone deploy.
#
# Usage from Azure Cloud Shell (Bash mode):
#   curl -sL https://raw.githubusercontent.com/vc-for-valuetruck/ltl-tool-detection-planner-and-booker/main/scripts/launch.sh | bash
#
# The script will prompt for the Alvys client secret if not already set in env.
set -euo pipefail

REPO_URL="https://github.com/vc-for-valuetruck/ltl-tool-detection-planner-and-booker.git"
REPO_DIR="$HOME/ltl-tool-detection-planner-and-booker"

echo "[launch] Cleaning any prior clone..."
rm -rf "$REPO_DIR"

echo "[launch] Cloning repo..."
git clone --depth=1 "$REPO_URL" "$REPO_DIR"
cd "$REPO_DIR"

if [ -z "${ALVYS_CLIENT_SECRET:-}" ]; then
  echo ""
  echo "[launch] Paste the Alvys va336 client secret (input hidden):"
  read -rs ALVYS_CLIENT_SECRET
  export ALVYS_CLIENT_SECRET
  echo ""
fi

chmod +x scripts/cloud-shell-deploy.sh
exec ./scripts/cloud-shell-deploy.sh
