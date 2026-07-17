#!/usr/bin/env bash
# Mint an Alvys MCP access token using the LTL tool's existing client-credentials.
# Requires: ALVYS_CLIENT_ID, ALVYS_CLIENT_SECRET in the environment (or a .env file).
# Prints the access token to stdout. Token TTL is ~1 hour.
#
# Usage:
#   export ALVYS_CLIENT_ID=... ALVYS_CLIENT_SECRET=...
#   token=$(./scripts/alvys-mcp-token.sh)
#   curl -H "Authorization: Bearer $token" https://mcp.alvys.com/mcp/...
set -euo pipefail

if [ -z "${ALVYS_CLIENT_ID:-}" ] || [ -z "${ALVYS_CLIENT_SECRET:-}" ]; then
  echo "ALVYS_CLIENT_ID and ALVYS_CLIENT_SECRET must be set." >&2
  exit 1
fi

response=$(curl -sSf --request POST \
  --url https://auth.alvys.com/oauth/token \
  --header 'Content-Type: application/json' \
  --data "{
    \"client_id\": \"$ALVYS_CLIENT_ID\",
    \"client_secret\": \"$ALVYS_CLIENT_SECRET\",
    \"audience\": \"https://api.alvys.com/public/\",
    \"grant_type\": \"client_credentials\"
  }")

# Extract access_token without requiring jq.
echo "$response" | python3 -c "import sys, json; print(json.load(sys.stdin)['access_token'])"
