# UAT Template

This repository is a reusable internal application template. It is not an application
itself. Application-specific product goals and workflows belong in repositories created
from this template.

It ships a pre-wired full-stack starter:

- **.NET 10** Web API (vertical-slice features)
- **Angular 20** SPA (standalone components)
- **Microsoft Entra ID** (MSAL) authentication plumbing
- **SQL Server 2022** in Docker (no cloud backend required)
- **Docker Compose** local runtime
- **GitHub Codespaces** support for instant UAT sharing
- **ngrok** public-URL demo path (single origin serves SPA + API)
- Safe, generic `.env.example` and setup docs

---

## 1. What this template consists of

```text
.
├── docker-compose.yml          # sqlserver + api + web
├── docker-compose.demo.yml     # + ngrok tunnel (public demo URL)
├── start-demo.sh / stop-demo.sh# one-command ngrok demo
├── Makefile                    # up / build / down / logs / reset / demo-up
├── .env.example                # generic environment variables
├── TEMPLATE_SETUP.md           # step-by-step checklist for a new app
├── init/01-seed.sql            # SQL Server init/seed script
├── .devcontainer/              # Codespaces / VS Code dev container
├── docs/                       # codespaces-demo.md, demo-ngrok.md runbooks
├── scripts/                    # start-codespaces-demo.sh
├── .github/workflows/ci.yml    # build + test API and build web
├── MyApp.sln                   # .NET solution
├── src/
│   ├── MyApp.Api/              # .NET 10 Web API
│   └── MyApp.Api.Tests/        # xUnit tests
└── web/                        # Angular 20 SPA
```

| Layer | Technology |
|---|---|
| Frontend | Angular 20 + MSAL Angular |
| Backend | .NET 10 Web API |
| Auth | Microsoft Entra ID |
| Database | SQL Server 2022 (Docker) |
| CI | GitHub Actions |
| UAT | GitHub Codespaces |

---

## 2. How to create a new repo from this template

```bash
# On GitHub: click "Use this template" → Create a new repository
# Or via CLI:
gh repo create my-new-app --template valuetruck-vc/uat-template --private --clone
```

Then follow [`TEMPLATE_SETUP.md`](TEMPLATE_SETUP.md) and the rename checklist in section 5.

---

## 3. First run

```bash
cp .env.example .env        # then fill in values (section 4)
make build                  # build images and start the stack
```

Endpoints:

```text
Web: http://localhost:4200
API: http://localhost:5072
SQL: localhost,14333
```

The SPA loads even before Entra is configured (it shows "Auth not configured")
so you can verify the stack end-to-end first, then wire up authentication.

You can also run the pieces directly without Docker:

```bash
dotnet run --project src/MyApp.Api      # API on http://localhost:5072
cd web && npm install && npm start      # SPA on http://localhost:4200
```

---

## 4. Required configuration

Copy `.env.example` to `.env` and fill in:

| Variable | Purpose |
|---|---|
| `AZURE_AD_TENANT_ID` | Entra tenant (directory) ID |
| `AZURE_AD_API_CLIENT_ID` | App registration for the API |
| `AZURE_AD_CLIENT_SECRET` | API app client secret |
| `AZURE_AD_WEB_CLIENT_ID` | App registration for the SPA |
| `AZURE_AD_API_SCOPE` | Exposed API scope the SPA requests |
| `ALLOWED_EMAIL_DOMAIN` | Email domain allow-list (default `example.com`) |
| `MSSQL_SA_PASSWORD` | SQL Server `sa` password |
| `EXTERNAL_API_BASE_URL` | Optional outbound API base URL |
| `EXTERNAL_API_KEY` | Optional outbound API key |

These map to .NET configuration (`AzureAd__*`, `AccessPolicy__*`, `ExternalApi__*`,
`ConnectionStrings__DefaultConnection`) and Angular runtime config (`RUNTIME_*`) in
`docker-compose.yml`. Never commit `.env`.

> If `AccessPolicy:AllowedEmailDomains` is empty, any authenticated user is allowed
> (handy for first-run/local UAT). Set it to lock access to your org's domain.

---

## 5. Rename checklist

The template uses the placeholders `MyApp` (Pascal case) and `myapp` (lower case).
Replace them with your application name in:

```text
src/MyApp.Api/                  # rename the folder
src/MyApp.Api/MyApp.Api.csproj  # rename the file + RootNamespace/AssemblyName
src/MyApp.Api.Tests/            # rename the folder + csproj
MyApp.sln                       # solution name + project paths
docker-compose.yml              # name:, image:, container_name:, Database=, Dockerfile paths
web/package.json                # "name": "myapp-web"
web/src/index.html              # <title>MyApp</title>
web/angular.json                # project name + outputPath / buildTarget
init/01-seed.sql                # database name
.devcontainer/devcontainer.json # "name"
docker-compose.demo.yml         # ngrok container_name
scripts/start-codespaces-demo.sh# API_PROJECT default path
```

A quick find (review before replacing):

```bash
grep -rIl --exclude-dir=node_modules --exclude-dir=bin --exclude-dir=obj --exclude-dir=dist 'MyApp\|myapp' .
```

After renaming, run the commands in section 9 to confirm everything still builds.

---

## 6. Backend structure

```text
src/MyApp.Api/
├── Program.cs               # auth, CORS, EF, options wiring
├── appsettings.json         # default config (overridden by env in Docker)
├── Dockerfile
├── Options/                 # AccessPolicyOptions, ExternalApiOptions
├── Security/                # AllowedEmailDomain authorization policy
├── Data/AppDbContext.cs     # EF Core context (add your DbSets here)
└── Features/                # vertical-slice features
    ├── Health/              # GET /api/health (anonymous liveness)
    └── Me/                  # GET /api/me (protected sample endpoint)
```

Add new functionality as a folder under `Features/` (controller + service + DTOs).

---

## 7. Frontend structure

```text
web/
├── package.json
├── angular.json
├── Dockerfile               # build → nginx, proxies /api → api service
├── nginx.conf.template
├── docker-entrypoint.sh     # writes runtime-config.json from RUNTIME_* env
├── public/runtime-config.json
└── src/
    ├── index.html
    ├── main.ts              # loads runtime config, then bootstraps
    └── app/
        ├── app.ts           # root standalone component
        ├── app.config.ts    # MSAL + router + http wiring
        ├── app.routes.ts
        ├── runtime-config.ts
        └── pages/home/      # lazy-loaded sample page
```

Auth and API config are loaded at runtime from `runtime-config.json`, so the same
built image works in any environment without a rebuild. Add lazy-loaded pages under
`src/app/pages/`.

---

## 8. Data / seed strategy

SQL Server runs in Docker. On first container start, scripts in `init/` are executed
in alphabetical order (mounted to `/docker-entrypoint-initdb.d`). Edit
`init/01-seed.sql` to create your database, tables, and demo data for UAT.

```bash
make reset   # tears down volumes and re-seeds from init/
```

For schema managed in code, add EF Core migrations in `src/MyApp.Api` and apply them
on startup or via `dotnet ef database update`.

---

## 9. Commands

```bash
# Docker stack
make up        # start in background
make build     # build images and start
make down      # stop
make logs      # tail logs
make reset     # wipe volumes, rebuild, re-seed

# Public demo URL via ngrok (needs NGROK_AUTHTOKEN in .env)
make demo-up   # stack + ngrok tunnel, prints the public URL
make demo-down # tear down the demo stack + tunnel

# Backend
dotnet restore
dotnet build
dotnet test

# Frontend
cd web
npm install
npm run build
npm test -- --watch=false
```

---

## 10. UAT guidance

Two ways to share a running build with testers:

### a) GitHub Codespaces (default)

Open the repo in a **Codespace**, start the stack, and share the forwarded port
`4200` URL. No local setup or cloud subscription required. The devcontainer
installs .NET/Node/Docker/GitHub CLI, seeds `.env`, and restores dependencies.

```bash
make up                                  # or: bash scripts/start-codespaces-demo.sh
```

- Port `4200` (web) → set **Public** and share. Port `5072` (API) stays private;
  `/api` is reachable through the `4200` URL (e.g. `<url>/api/health`).
- Add the exact forwarded `4200` URL to the SPA app registration's redirect URIs.
- Use **Codespaces secrets** for `MSSQL_SA_PASSWORD`, `AZURE_AD_CLIENT_SECRET`, etc.
- Full runbook: [`docs/codespaces-demo.md`](docs/codespaces-demo.md).

### b) ngrok public URL (local backup)

Expose a local Docker stack over one public HTTPS URL. The web container proxies
`/api`, so a single ngrok origin serves both SPA and API.

```bash
cp .env.example .env     # set NGROK_AUTHTOKEN (free token) + Entra values
make demo-up             # prints the public URL + the Entra redirect to add
```

- Full runbook: [`docs/demo-ngrok.md`](docs/demo-ngrok.md).
- **Never commit `NGROK_AUTHTOKEN`** — keep it only in the gitignored `.env`.

### General

- Seed representative demo data in `init/01-seed.sql` so testers see realistic state.
- Keep the email-domain allow-list (`ALLOWED_EMAIL_DOMAIN`) aligned with the testers
  you invite, or leave it empty during early UAT to allow any authenticated user.
