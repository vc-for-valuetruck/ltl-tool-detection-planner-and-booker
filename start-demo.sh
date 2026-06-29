#!/usr/bin/env bash
#
# start-demo.sh
# ---------------------------------------------------------------------------
# One-command UAT demo over a public ngrok URL.
#
#   1. Boots the full stack (SQL Server + API + Web) plus an ngrok tunnel
#      in front of the web container.
#   2. Discovers the public ngrok URL from the local ngrok API (port 4040).
#   3. Re-injects that URL into the API's CORS allow-list and recreates the
#      affected services so auth + cross-origin calls work over the tunnel.
#   4. Prints the public URL and the EXACT Entra redirect URI you must add.
#
# Because the web container reverse-proxies /api -> the API container, a SINGLE
# ngrok origin serves both the SPA and the API. That means just ONE Entra
# redirect URI and ONE CORS origin to manage.
# ---------------------------------------------------------------------------

set -euo pipefail

COMPOSE=(docker compose -f docker-compose.yml -f docker-compose.demo.yml)
NGROK_API="http://localhost:4040/api/tunnels"

c_grn() { printf '\033[32m%s\033[0m\n' "$*"; }
c_ylw() { printf '\033[33m%s\033[0m\n' "$*"; }
c_red() { printf '\033[31m%s\033[0m\n' "$*"; }
c_bld() { printf '\033[1m%s\033[0m\n'  "$*"; }
die()   { c_red "ERROR: $*" >&2; exit 1; }

# -- Preflight ---------------------------------------------------------------
command -v docker >/dev/null 2>&1 || die "docker not found."
docker compose version >/dev/null 2>&1 || die "docker compose v2 not found."
[[ -f .env ]] || die "No .env file. Copy .env.example to .env and fill it in."

# shellcheck disable=SC1091
set -a; source .env; set +a
[[ -n "${NGROK_AUTHTOKEN:-}" ]] || die "NGROK_AUTHTOKEN missing in .env (https://dashboard.ngrok.com/get-started/your-authtoken)"

# -- 1. Boot the stack + tunnel ----------------------------------------------
c_bld "Starting UAT stack + ngrok tunnel..."
"${COMPOSE[@]}" up -d --build

# -- 2. Discover the public ngrok URL ----------------------------------------
c_bld "Waiting for ngrok to publish a public URL..."
PUBLIC_URL=""
for _ in $(seq 1 30); do
  PUBLIC_URL="$(curl -s "$NGROK_API" 2>/dev/null \
    | grep -o 'https://[a-zA-Z0-9.-]*\.ngrok[a-zA-Z0-9.-]*' \
    | head -1 || true)"
  [[ -n "$PUBLIC_URL" ]] && break
  sleep 2
done
[[ -n "$PUBLIC_URL" ]] || die "Could not read ngrok URL from $NGROK_API. Is the ngrok container healthy? Try: ${COMPOSE[*]} logs ngrok"
c_grn "Public URL: $PUBLIC_URL"

# -- 3. Re-inject the public origin into the API and recreate it -------------
# Persist PUBLIC_URL so the compose override (${PUBLIC_URL}) resolves on recreate.
if grep -q '^PUBLIC_URL=' .env; then
  sed -i.bak "s#^PUBLIC_URL=.*#PUBLIC_URL=${PUBLIC_URL}#" .env && rm -f .env.bak
else
  printf '\nPUBLIC_URL=%s\n' "$PUBLIC_URL" >> .env
fi
c_bld "Applying public origin to API (CORS)..."
PUBLIC_URL="$PUBLIC_URL" "${COMPOSE[@]}" up -d --no-deps --force-recreate api web

# -- 4. Output: what to do in Entra ------------------------------------------
echo
c_grn "===================================================================="
c_grn " UAT demo is live"
c_grn "===================================================================="
echo
c_bld "  Public demo URL : $PUBLIC_URL"
echo  "  Health check    : $PUBLIC_URL/api/health"
echo  "  API (via proxy) : $PUBLIC_URL/api"
echo  "  ngrok inspector : http://localhost:4040"
echo
c_ylw "  -- ONE-TIME Entra step (required for sign-in over the tunnel) --"
c_ylw "  Azure Portal -> Entra ID -> App registrations -> your web (SPA) app"
c_ylw "    -> Authentication -> Single-page application -> Add a redirect URI:"
echo  "        $PUBLIC_URL"
echo
c_ylw "  The ngrok URL changes each restart on the free plan. Re-run this"
c_ylw "  script and update the redirect URI, OR reserve a static domain"
c_ylw "  (ngrok paid) and set NGROK_DOMAIN in .env to keep it stable."
echo
c_bld "  Stop the demo:  ./stop-demo.sh   (or: ${COMPOSE[*]} down)"
echo
