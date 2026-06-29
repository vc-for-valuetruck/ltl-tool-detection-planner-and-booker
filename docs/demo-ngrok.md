# Public demo URL with ngrok

Use this when you want a public HTTPS URL that exercises the full local stack
(SQL Server + API + Web) before a UAT session — for example when GitHub
Codespaces is unavailable. For the default cloud path, see
[codespaces-demo.md](codespaces-demo.md).

## How it works

`docker-compose.demo.yml` adds an `ngrok` service in front of the web container.
The web container already reverse-proxies `/api` → the API container, so a
**single** public ngrok origin serves both the SPA and the API. That means one
Entra redirect URI and one CORS origin to manage.

## One-command setup

```bash
cp .env.example .env        # fill in Entra values + NGROK_AUTHTOKEN
./start-demo.sh             # or: make demo-up
```

`start-demo.sh`:

1. Boots the stack + ngrok tunnel (`docker compose -f docker-compose.yml -f docker-compose.demo.yml up -d --build`).
2. Reads the public URL from the local ngrok API (`http://localhost:4040`).
3. Writes that URL to `PUBLIC_URL` in `.env`, then recreates the API and web so
   the public origin is added to the API CORS allow-list.
4. Prints the public URL and the exact Entra redirect URI to add.

Stop the demo:

```bash
./stop-demo.sh             # or: make demo-down
```

## Required configuration

| Variable | Purpose |
|---|---|
| `NGROK_AUTHTOKEN` | Free ngrok account token. **Never commit it.** |
| `NGROK_DOMAIN` | Optional ngrok reserved domain (paid) for a stable URL across restarts. |
| `PUBLIC_URL` | Set automatically by `start-demo.sh`; leave blank. |

> Never commit or paste ngrok auth tokens into source control, screenshots, or
> tickets. If a token is exposed, rotate it from the ngrok dashboard.

## Entra redirect values

Add the printed public URL to the SPA app registration under
**Authentication → Single-page application → Add a redirect URI**:

```text
https://<your-ngrok-url>
```

On the free plan the URL changes each restart — re-run `start-demo.sh` and update
the redirect URI, or set `NGROK_DOMAIN` to a reserved domain to keep it stable.
If Microsoft reports a redirect mismatch, copy the exact URI from the error and
add that exact value.

## Checklist

- Open `https://<your-ngrok-url>` in a clean browser profile.
- Confirm API health: `https://<your-ngrok-url>/api/health`.
- Confirm login redirects back to the ngrok URL.
- Keep the local machine awake during the demo.

## Troubleshooting

- **No public URL found:** check `docker compose -f docker-compose.yml -f docker-compose.demo.yml logs ngrok`.
- **Auth redirect fails:** confirm the exact ngrok URL is in the SPA app
  registration's redirect URIs.
- **API calls fail:** verify the web container is up and proxying `/api`
  (`docker compose logs web`).
- **ngrok inspector:** `http://localhost:4040` shows live tunnel traffic.
