#!/bin/sh
# Runs via the nginx image's /docker-entrypoint.d hook (before template envsubst).
# 1. Provide a default API upstream so the proxy_pass template resolves.
# 2. Emit runtime-config.json from RUNTIME_* env vars so one image works in any env.
set -e

export API_UPSTREAM="${API_UPSTREAM:-http://api:8080}"
# Extract hostname (without scheme, port, or path) so the nginx proxy can
# rewrite the Host header. Azure App Service does host-based routing on the
# public front door — passing the wrong Host yields 400.
export API_HOST="$(echo "$API_UPSTREAM" | sed -E 's#^https?://##; s#/.*##; s#:[0-9]+$##')"

cat > /usr/share/nginx/html/runtime-config.json <<EOF
{
  "tenantId": "${RUNTIME_TENANT_ID:-}",
  "clientId": "${RUNTIME_WEB_CLIENT_ID:-}",
  "apiScope": "${RUNTIME_API_SCOPE:-}",
  "apiBaseUrl": "${RUNTIME_API_BASE_URL:-/api}"
}
EOF
