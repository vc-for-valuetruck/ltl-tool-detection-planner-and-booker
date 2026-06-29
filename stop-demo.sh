#!/usr/bin/env bash
# stop-demo.sh -- tear down the UAT demo stack + ngrok tunnel.
set -euo pipefail
COMPOSE=(docker compose -f docker-compose.yml -f docker-compose.demo.yml)
echo "Stopping UAT demo (stack + ngrok)..."
"${COMPOSE[@]}" down
# Clear the ephemeral public URL so the next run starts clean.
if [[ -f .env ]] && grep -q '^PUBLIC_URL=' .env; then
  sed -i.bak '/^PUBLIC_URL=/d' .env && rm -f .env.bak
fi
echo "Demo stopped."
