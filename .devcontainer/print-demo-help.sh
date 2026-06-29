#!/usr/bin/env bash
set -euo pipefail

cat <<'HELP'

UAT Codespaces workspace is ready.

Choose a demo path:
  Docker stack (full SQL + API + Web):
    make up
  Lightweight (API + Web, no Docker):
    bash scripts/start-codespaces-demo.sh

Then open the Ports tab and use the forwarded URL for port 4200.
To test API health, use the forwarded port 4200 URL + /api/health.

For a public URL from a local machine instead of Codespaces, the ngrok
path is documented in docs/demo-ngrok.md.

HELP
