#!/usr/bin/env bash
# demo-up.sh - one-shot local demo runner for the LTL tool.
#
# Assumes zero infrastructure. Requires only Docker Desktop and, in .env, real
# Alvys credentials for the va336 tenant (the same ones your MCP connector uses).
#
# Boots SQL Server + API + Web with AccessPolicy:Mode=Demo, waits for readiness,
# and prints URLs. Run again to rebuild after code changes.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

log() { printf "\033[36m[demo-up]\033[0m %s\n" "$*"; }
err() { printf "\033[31m[demo-up]\033[0m %s\n" "$*" >&2; }

# ---- Dependency check ----
if ! command -v docker >/dev/null 2>&1; then
  err "Docker is not installed. Install Docker Desktop from https://www.docker.com/products/docker-desktop and re-run."
  exit 1
fi
if ! docker info >/dev/null 2>&1; then
  err "Docker daemon is not running. Start Docker Desktop and re-run."
  exit 1
fi

# ---- .env check ----
if [[ ! -f .env ]]; then
  log ".env not found; copying .env.demo.example -> .env"
  cp .env.demo.example .env
  err "Now open .env and fill in ALVYS_CLIENT_SECRET before re-running."
  exit 1
fi

# shellcheck disable=SC1091
set -a; source .env; set +a

if [[ "${ACCESS_POLICY_MODE:-}" != "Demo" ]]; then
  err "ACCESS_POLICY_MODE must equal 'Demo' in .env for the demo runner. Aborting."
  exit 1
fi
if [[ -z "${ALVYS_CLIENT_SECRET:-}" \
   || "${ALVYS_CLIENT_SECRET}" == *"REPLACE_WITH_YOUR_ALVYS_CLIENT_SECRET"* \
   || "${ALVYS_CLIENT_SECRET}" == *"paste"* ]]; then
  err "ALVYS_CLIENT_SECRET is not set in .env. Paste the Value Truck va336 secret and re-run."
  exit 1
fi

# ---- Boot ----
log "Building and starting containers (this takes ~2-3 min on the first run)..."
docker compose up -d --build

# ---- Wait for API health ----
log "Waiting for API to report healthy on http://localhost:5072/api/health ..."
DEADLINE=$(( $(date +%s) + 180 ))
while true; do
  if curl -fsS http://localhost:5072/api/health >/dev/null 2>&1; then
    log "API is healthy."
    break
  fi
  if [[ $(date +%s) -gt $DEADLINE ]]; then
    err "API failed to report healthy within 180s. Check 'docker compose logs api' for details."
    exit 1
  fi
  sleep 3
done

# ---- Print URLs and demo script ----
cat <<'EOF'

============================================================
  LTL demo is running.

  Web UI:       http://localhost:4200
  API:          http://localhost:5072/api
  Swagger:      http://localhost:5072/swagger

  Demo script:
    1. Open http://localhost:4200/ltl
    2. Enter origin=Laredo, dest=Dallas -> Search
    3. Click a load row to open the detail drawer
    4. Click "Consolidate" -> plan preview with driver RPM
    5. Copy the click-card text to paste into Alvys

  Stop the stack:  docker compose down
  Tail logs:       docker compose logs -f api web
============================================================
EOF
