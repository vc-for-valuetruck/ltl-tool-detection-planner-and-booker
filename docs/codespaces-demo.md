# Codespaces UAT runbook

Use GitHub Codespaces as the default way to share a running UAT build. It avoids
local dependency drift (Node, npm, Angular, .NET SDK, Docker, SQL) and gives every
tester a forwarded HTTPS URL with no local setup.

## Open or rebuild the Codespace

1. Open the repository on GitHub.
2. Select **Code** → **Codespaces**.
3. Open the existing Codespace or create a new one from `main`.
4. If the Codespace predates this setup, run **Codespaces: Rebuild Container**
   from the command palette.

The devcontainer installs .NET, Node, Docker support, and the GitHub CLI, copies
`.env.example` → `.env`, restores .NET packages, and runs `npm install` in `web`.

## Start the demo

Two paths are available in the Codespaces terminal:

**Docker stack (full SQL Server + API + Web):**

```bash
make up
```

**Lightweight (API + Web only, no Docker):**

```bash
bash scripts/start-codespaces-demo.sh
```

Both serve the web app on port `4200` and the API on port `5072`.

## Share the demo URL

Open the **Ports** tab in Codespaces.

- Port `4200` = Web App — set its visibility to **Public** and share that URL.
- Port `5072` = API — keep **Private** unless a tester needs to call the API
  directly. The web app already proxies `/api`, so `/api/health` works through
  the port `4200` URL.

Codespaces generates a unique forwarded URL per Codespace — that is the URL to
share for the demo.

## Microsoft Entra redirect setup

For demo login, add the exact Codespaces forwarded **web** URL (port `4200`) to
the SPA app registration under **Authentication → Single-page application**:

```text
https://<codespace-forwarded-4200-url>
```

If Microsoft reports a redirect mismatch, copy the exact redirect URI from the
error and add that exact value. Redirects belong on the web/SPA app registration
(the Angular app starts the login), not the API app registration.

## Codespaces secrets

For values you don't want in `.env`, use **Settings → Codespaces → Secrets** (repo
or org level). They are injected as environment variables into the Codespace and
are never committed. Good candidates: `MSSQL_SA_PASSWORD`, `AZURE_AD_CLIENT_SECRET`,
and any `EXTERNAL_API_*` keys. Never paste secrets into tracked files.

## Health check

```text
https://<codespace-forwarded-4200-url>/api/health
```

## Backup path: local ngrok

If Codespaces is unavailable, expose a local stack over a public URL with ngrok —
see [demo-ngrok.md](demo-ngrok.md).
