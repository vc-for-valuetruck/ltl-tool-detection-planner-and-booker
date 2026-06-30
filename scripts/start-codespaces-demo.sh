#!/usr/bin/env bash
#
# start-codespaces-demo.sh
# ---------------------------------------------------------------------------
# Launch the API and Angular web app directly (no Docker) inside a GitHub
# Codespace, then share the forwarded port 4200 URL with testers.
#
# This is the lightweight Codespaces path. The Docker stack (make up) and the
# ngrok path (./start-demo.sh) remain available for full-stack/local demos.
# ---------------------------------------------------------------------------
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
API_PORT="${API_PORT:-5072}"
WEB_PORT="${WEB_PORT:-4200}"
API_PROJECT="${API_PROJECT:-src/LtlTool.Api/LtlTool.Api.csproj}"

cd "$ROOT"

cleanup() {
  if [ -n "${API_PID:-}" ] && kill -0 "$API_PID" 2>/dev/null; then
    kill "$API_PID" 2>/dev/null || true
  fi
  if [ -n "${WEB_PID:-}" ] && kill -0 "$WEB_PID" 2>/dev/null; then
    kill "$WEB_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet is required in the Codespace. Rebuild the devcontainer and retry."
  exit 1
fi

if ! command -v npm >/dev/null 2>&1; then
  echo "npm is required in the Codespace. Rebuild the devcontainer and retry."
  exit 1
fi

if [ ! -d "web/node_modules/@angular-devkit/build-angular" ]; then
  echo "Web dependencies are missing. Running npm install..."
  (cd web && npm install)
fi

echo "Starting API on http://localhost:${API_PORT}"
dotnet run --project "$API_PROJECT" --urls "http://0.0.0.0:${API_PORT}" &
API_PID=$!

sleep 8

echo "Starting web demo server on http://localhost:${WEB_PORT}"
(cd web && npm run start:demo -- --host 0.0.0.0) &
WEB_PID=$!

cat <<HELP

UAT Codespaces demo is starting.

Open the Ports tab:
- Port ${WEB_PORT}: Web app. Make it Public for demos and open the forwarded URL.
- Port ${API_PORT}: API. Make it Public only if direct API testing is needed.

For Microsoft Entra redirects, add the exact forwarded web URL shown by Codespaces.
Leave this terminal running during the demo.

HELP

wait
