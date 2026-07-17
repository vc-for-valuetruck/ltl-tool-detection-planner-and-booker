# Alvys MCP — Setup Guide

## What this is

The Alvys Model Context Protocol (MCP) server, launched in beta in July 2026,
exposes the same Alvys Public API surface that the LTL tool already uses — but
through a discoverable, permissioned interface that AI clients (Claude Desktop,
Claude Code, Cursor, Continue.dev) can talk to natively.

Value Truck already holds the credentials MCP needs: the same `ALVYS_CLIENT_ID`
+ `ALVYS_CLIENT_SECRET` the LTL tool uses (against `auth.alvys.com/oauth/token`
with audience `https://api.alvys.com/public/`) also mints a token that
authenticates against MCP. No new OAuth application is required.

**Beta constraint.** In beta, the production MCP server exposes read-only tools
only. Write actions (`trips_assign`, `tenders_accept`, `carriers_set_status`,
etc.) are enabled only against the QA / sandbox tenant during beta and rolled
to prod when the beta lifts.

## Server URLs

| Environment | URL | Tools available |
| --- | --- | --- |
| **Production** | `https://mcp.alvys.com/mcp` | **Read only** in beta |
| **QA / Sandbox** | `https://mcp.qa.alvys.net/mcp` | Read + write (safe for prototyping Phase 2) |

Both use the MCP **Streamable HTTP** transport and authenticate with an Auth0
bearer token in the `Authorization` header.

## Tools you get, grouped by domain

Every tool below maps 1:1 to an Alvys Public API capability and requires the
scope shown. Scopes are already present on the LTL tool's client-credentials
application; if you build a separate app for MCP-only use, mirror them.

- **Loads** — `loads_search`, `loads_get_by_id` (`load:read`)
- **Trips** — `trips_search`, `trips_get_by_id` (`trip:read`); `trips_update_stop_status` (`stop:update`, write), `trips_assign` (`trip:update`, write)
- **Drivers** — `drivers_search`, `drivers_get_by_id`, `drivers_events_search` (`driver:read`)
- **Carriers** — `carriers_search`, `carriers_get_by_id`, `carriers_documents_get` (`carrier:read`); `carriers_set_status`, `carriers_document_upload` (`carrier:update`, write)
- **Customers** — `customers_search`, `customers_get_by_id` (`customer:read`); `customers_create` (`customer:create`, write)
- **Trucks & Trailers** — `trucks_search`, `trucks_get_by_id`, `trailers_search`, `trailers_get_by_id` (`truck:read` / `trailer:read`)
- **Invoices / Fuel / Payments** — `invoices_search`, `invoices_get_by_id`, `fuel_transactions_search` (`invoice:read` / `fuel:read`); `invoices_record_carrier_payment`, `invoices_record_customer_payment`, `invoices_record_financing` (`invoice:update`, write)
- **Visibility** — `visibility_inbound_history`, `visibility_outbound_history` (`visibility:read`)
- **Deductions** — `deductions_search`, `deductions_get_by_id` (`deduction:read`)
- **Tenders** — `tenders_search`, `tenders_get_by_id` (`tender:read`); `tenders_create` (`tender:create`, write), `tenders_accept`, `tenders_accept_updates`, `tenders_reject`, `tenders_accept_cancel` (`tender:update`, write)

There are also **guided prompts** — multi-step workflows the server itself
scripts and exposes as first-class tools:

- `find_and_cover_load_v1` — find an open load and cover it with a carrier via a tender
- `dispatch_driver_v1` — check driver availability and assign a driver, truck, and trailer to a trip
- `carrier_onboarding_v1` — look up a carrier by MC/DOT, review docs, upload the packet, activate
- `settlement_reconciliation_v1` — reconcile a load's invoices, deductions, and carrier/customer payments
- `track_shipment_v1` — pull inbound and outbound tracking history for a shipment

## Prerequisites

1. **Beta enrollment.** The MCP server is invite-only during beta. Confirm with
   your Alvys account rep that Value Truck's tenant is enrolled. Without
   enrollment, every tool call returns `401 — tenant not permitted`.
2. **Organization assignment on the client-credentials app.** The Auth0
   client-credentials application backing the LTL tool must have an
   **organization** assigned in Auth0. Without it, MCP returns
   `401 — missing tenant context` on every call. This is a one-time Alvys
   configuration; ask the account rep to verify.
3. **Access token audience.** The token request must use
   `audience: "https://api.alvys.com/public/"` (with the trailing `/public/`).
   The LTL tool already sets this in `AlvysOptions.Audience`.

## Minting a token (once per hour)

```bash
curl --request POST \
  --url https://auth.alvys.com/oauth/token \
  --header 'Content-Type: application/json' \
  --data '{
    "client_id": "'"$ALVYS_CLIENT_ID"'",
    "client_secret": "'"$ALVYS_CLIENT_SECRET"'",
    "audience": "https://api.alvys.com/public/",
    "grant_type": "client_credentials"
  }' | jq -r .access_token
```

Tokens are short-lived (~1 hour). For a manual MCP client, re-mint whenever tool
calls start returning `401`. For headless automation, wrap the token request in
the client startup so each session mints fresh.

## Client configurations

### Claude Desktop (interactive login, PKCE — no secret stored)

**Best for you-as-a-human, day-to-day.** Sign in as your Alvys user; the browser
handles PKCE.

1. Open Claude Desktop → **Settings → Connectors → Add custom connector**.
2. URL: `https://mcp.alvys.com/mcp`.
3. Complete the browser login. **When prompted, select Value Truck's
   organization** — required, or every call fails with
   `401 — missing tenant context`.

### Claude Desktop (machine-to-machine, uses LTL tool credentials)

Best when you want the same credentials the LTL tool uses. Claude Desktop can't
attach a custom header directly, so bridge through `mcp-remote`. Add this to
`claude_desktop_config.json` (Settings → Developer → Edit Config):

```json
{
  "mcpServers": {
    "alvys": {
      "command": "npx",
      "args": [
        "-y", "mcp-remote",
        "https://mcp.alvys.com/mcp",
        "--header", "Authorization: Bearer YOUR_ACCESS_TOKEN"
      ]
    }
  }
}
```

Replace `YOUR_ACCESS_TOKEN` with the token you minted above. When calls start
returning 401, re-mint and edit this file.

### Cursor

Edit `~/.cursor/mcp.json` (or the workspace-scoped `.cursor/mcp.json`):

```json
{
  "mcpServers": {
    "alvys": {
      "url": "https://mcp.alvys.com/mcp",
      "headers": {
        "Authorization": "Bearer YOUR_ACCESS_TOKEN"
      }
    }
  }
}
```

Cursor picks it up on next restart. First call opens the browser for interactive
login if you leave `headers` off; keep `headers` in for M2M.

### Claude Code (CLI)

```bash
claude mcp add --transport http alvys \
  https://mcp.alvys.com/mcp \
  --header "Authorization: Bearer YOUR_ACCESS_TOKEN"
```

### Raw transport sanity check

```bash
curl -sD - -X POST https://mcp.alvys.com/mcp \
  -H "Authorization: Bearer $ALVYS_ACCESS_TOKEN" \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
```

Returns the tool catalog if the token is valid and the tenant is enrolled.

## Should the LTL tool itself call MCP?

**Not today.** The LTL tool's `AlvysClient` calls the Alvys Public API directly.
MCP sits in front of the same API, so switching to MCP would trade a stable,
type-safe .NET client for JSON-RPC calls the tool would have to parse — with no
new capability today (beta is read-only, and every read the LTL tool needs is
already implemented against the Public API).

Reconsider when either of these lands:

1. **MCP write tools exit beta on prod.** If `trips_assign`,
   `trips_update_stop_status`, and `tenders_accept` become available in prod
   MCP, the LTL tool's Phase 2 writeback slice
   (`src/LtlTool.Api/Features/Integrations/Alvys/Writeback/`) has a shorter
   path via MCP than via the underlying REST endpoints — the tool wouldn't have
   to implement each write endpoint separately.
2. **A guided prompt matches an LTL workflow.** `find_and_cover_load_v1` or
   `dispatch_driver_v1` may be worth calling from the LTL tool's match / assign
   flow rather than duplicating the ranking. Discuss with the Alvys engineer in
   the Phase 2 planning call.

Until then, MCP is a personal-productivity + AI-agent tool alongside the LTL
tool, not a replacement for `AlvysClient`.

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `401 — missing tenant context` | Client-credentials app has no Auth0 organization assigned, or interactive login skipped org selection | Ask Alvys support to assign the org to your M2M app; for interactive, sign out and re-select org |
| `401` after working for ~1 hour | Access token expired | Re-mint the token (see script above), update the header |
| `401` on every M2M call from the start | Wrong audience | Confirm the token request uses `audience: "https://api.alvys.com/public/"` with the trailing slash |
| Permission error on a specific tool | Token lacks that tool's scope | Add the scope in **Alvys Admin → API Access**, re-issue the token |
| Write tool rejected | Beta — writes disabled on prod | Use the QA URL if you need writes today |
| Response too large | Alvys' response-size cap tripped | Narrow the search filters or paginate the search |

## Related

- Alvys MCP docs: <https://docs.alvys.com/docs/mcp>
- Alvys tool catalog: <https://docs.alvys.com/docs/available-mcp-tools>
- Alvys Public API auth: <https://docs.alvys.com/docs/authentication-1>
- LTL tool's existing Alvys credentials — set in the `uat` GitHub environment
  as `ALVYS_TENANT_ID`, `ALVYS_CLIENT_ID`, `ALVYS_CLIENT_SECRET`; the same
  triple mints an MCP-compatible token.
