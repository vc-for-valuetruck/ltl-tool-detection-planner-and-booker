# Automated Build and Launch

This repository now has two launch paths:

1. **CI build/package/smoke test through GitHub Actions**
2. **Public UAT demo through Docker Compose + ngrok**

The goal is to make the LTL tool easy to validate and launch without changing the current Alvys safety posture.

---

## 1. GitHub Actions: Build, Package, and Launch Check

Workflow: `.github/workflows/build-launch.yml`

### What it does

On `main` pushes and manual `workflow_dispatch`, the workflow can:

- Restore, build, and test the .NET API.
- Verify EF migrations against a temporary SQL Server container.
- Install and build the Angular web app.
- Build and push API/Web Docker images to GitHub Container Registry.
- Launch the full Docker Compose stack inside the GitHub Actions runner.
- Smoke test:
  - API health at `http://localhost:5072/api/health`
  - Web shell at `http://localhost:4200`

### What it does not do

GitHub-hosted runners are temporary. The Compose stack launched in Actions is destroyed when the workflow finishes. That means the workflow proves the app can build and launch, but it does **not** host a long-running public UAT URL.

For a long-running public UAT URL, use one of these options:

- Run `make demo-up` on a company machine or VM with Docker and ngrok configured.
- Move the published containers to Azure App Service / Container Apps.
- Use a self-hosted GitHub Actions runner on a VM that is allowed to keep services running.

---

## 2. Manual GitHub launch

Go to:

`Actions -> Build, Package, and Launch Check -> Run workflow`

Recommended inputs:

- `publish_images`: `true`
- `run_compose_smoke`: `true`

After it completes, the workflow summary shows whether the stack built and launched successfully.

Published image names:

- `ghcr.io/valuetruck-vc/ltl-tool-detection-planner-and-booker-api`
- `ghcr.io/valuetruck-vc/ltl-tool-detection-planner-and-booker-web`

`latest` is published only from the default branch. Every run also gets a SHA-tagged image.

---

## 3. Public UAT launch with ngrok

This repo already includes:

- `docker-compose.yml`
- `docker-compose.demo.yml`
- `start-demo.sh`
- `stop-demo.sh`
- `Makefile`

### First-time setup

Copy environment settings:

```bash
cp .env.example .env
```

Set at minimum:

```bash
NGROK_AUTHTOKEN=your-ngrok-token
```

For fallback/demo data, keep:

```bash
ALVYS_PROVIDER=Fallback
ALVYS_WRITEBACK_MODE=Disabled
```

For live Alvys reads, set:

```bash
ALVYS_PROVIDER=Live
ALVYS_API_BASE_URL=https://integrations.alvys.com
ALVYS_API_VERSION=v1
ALVYS_TENANT_ID=...
ALVYS_CLIENT_ID=...
ALVYS_CLIENT_SECRET=...
ALVYS_WRITEBACK_MODE=Disabled
```

Do not commit real credentials.

### Launch public UAT

```bash
make demo-up
```

The script will:

1. Build and start SQL Server, API, Web, and ngrok.
2. Discover the public ngrok URL.
3. Inject that URL into API CORS.
4. Print the public demo URL.
5. Print the exact Entra redirect URI to add for sign-in.

### Stop public UAT

```bash
make demo-down
```

---

## 4. Local non-public launch

```bash
make build
```

Then open:

```text
http://localhost:4200
```

API health:

```text
http://localhost:5072/api/health
```

Stop:

```bash
make down
```

Reset all containers and SQL volume:

```bash
make reset
```

---

## 5. Safety posture

The launch automation does not change the Alvys posture:

- Alvys credentials remain server-side only.
- `ALVYS_WRITEBACK_MODE=Disabled` remains the safe default.
- The LTL assignment flow is still internal/audited unless a supported writeback contract is implemented.
- Missing Alvys data is surfaced, not invented.

---

## 6. Recommended next step for persistent hosting

For a real UAT URL that stays up without a laptop running, deploy the published GHCR images to Azure Container Apps or Azure App Service for Containers.

Required production/UAT secrets will include:

- SQL connection string
- Entra tenant/client/scope values
- allowed email domain
- Alvys tenant/client/secret values if using live reads
- Alvys writeback settings, still disabled unless formally approved
