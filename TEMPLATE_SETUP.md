# Template Setup Checklist

Complete these steps when creating a new app from this template.

## 1. Register Entra App Registrations

- [ ] Create **API app registration** in Azure Portal
  - Note the `Application (client) ID` → `AZURE_AD_API_CLIENT_ID`
  - Add a client secret → `AZURE_AD_CLIENT_SECRET`
  - Expose an API → add scope `access_as_user` → set `AZURE_AD_API_SCOPE`
    to `api://<API_CLIENT_ID>/access_as_user`
- [ ] Create **SPA app registration** in Azure Portal
  - Note the `Application (client) ID` → `AZURE_AD_WEB_CLIENT_ID`
  - Add a **Single-page application** redirect URI: `http://localhost:4200`
  - Grant API permission to the API app's `access_as_user` scope
- [ ] Note your `Tenant ID` → `AZURE_AD_TENANT_ID`

## 2. Configure Environment

- [ ] Copy `.env.example` → `.env`
- [ ] Fill in all `AZURE_AD_*` values
- [ ] Set `ALLOWED_EMAIL_DOMAIN` to your org's email domain (or leave default for open UAT)
- [ ] Set `MSSQL_SA_PASSWORD` to a strong password
- [ ] (Optional) Set `EXTERNAL_API_BASE_URL` / `EXTERNAL_API_KEY`
- [ ] Leave `ALVYS_*` / `LTL_*` blank/defaulted until Phase 2 (API only; never expose Alvys secrets to the SPA)
- [ ] (Optional, for ngrok demo) Set `NGROK_AUTHTOKEN` — never commit it

## 3. App Naming (already applied)

This repo has already been renamed from the template placeholders to the
`LtlTool` / `ltl-tool-detection-planner-and-booker` identity (see README
section 5). The placeholders `MyApp` / `myapp` should no longer appear:

- [x] `src/LtlTool.Api/` (folder, csproj, `RootNamespace`, `AssemblyName`)
- [x] `src/LtlTool.Api.Tests/` (folder + csproj + `ProjectReference`)
- [x] `LtlTool.sln` (name + project paths)
- [x] `docker-compose.yml` (`name:`, `image:`, `container_name:`, `Database=`, Dockerfile paths)
- [x] `web/package.json` (`"name": "ltl-tool-detection-planner-and-booker-web"`)
- [x] `web/angular.json` (project name, `outputPath`, `buildTarget`)
- [x] `web/Dockerfile` (`dist/ltl-tool-detection-planner-and-booker-web/browser` copy path)
- [x] `web/src/index.html` (`<title>`)
- [x] `init/01-seed.sql` (database name)
- [x] `.devcontainer/devcontainer.json` (`name`)
- [x] `docker-compose.demo.yml` (ngrok `container_name`)
- [x] `scripts/start-codespaces-demo.sh` (`API_PROJECT` default path)

## 4. Start and Verify

- [ ] `make build`
- [ ] Confirm SQL Server starts healthy
- [ ] Confirm `GET http://localhost:5072/api/health` returns `200` (anonymous liveness)
- [ ] Confirm `GET http://localhost:5072/api/me` returns `401` when unauthenticated
- [ ] Confirm the Angular app loads at `http://localhost:4200`
- [ ] After Entra config, confirm sign-in redirects to Entra and the token flow works

## 5. Seed Demo Data

- [ ] Edit `init/01-seed.sql` with your app-specific demo data
- [ ] Run `make reset` to rebuild with fresh seed data

## 6. Share for UAT

Pick one (see [`docs/codespaces-demo.md`](docs/codespaces-demo.md) and
[`docs/demo-ngrok.md`](docs/demo-ngrok.md)):

- [ ] **Codespaces:** open the repo in a Codespace, `make up`, set port `4200`
      to **Public**, share the forwarded URL, and add it to the SPA app
      registration's redirect URIs. Store secrets via Codespaces secrets.
- [ ] **ngrok:** set `NGROK_AUTHTOKEN` in `.env`, run `make demo-up`, then add
      the printed public URL to the SPA app registration's redirect URIs.
