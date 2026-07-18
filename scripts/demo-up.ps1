#!/usr/bin/env pwsh
# demo-up.ps1 - one-shot local demo runner for the LTL tool (Windows / PowerShell).
#
# Assumes zero infrastructure. Requires only Docker Desktop and, in .env, real
# Alvys credentials for the va336 tenant (the same ones your MCP connector uses).
#
# Boots SQL Server + API + Web with AccessPolicy:Mode=Demo, waits for readiness,
# and prints URLs. Run again to rebuild after code changes.

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

function Write-Log([string]$msg) { Write-Host "[demo-up] $msg" -ForegroundColor Cyan }
function Write-Err([string]$msg) { Write-Host "[demo-up] $msg" -ForegroundColor Red }

# ---- Dependency check ----
try { docker info | Out-Null } catch {
    Write-Err "Docker daemon is not running (or Docker Desktop is not installed). Start Docker Desktop and re-run."
    exit 1
}

# ---- .env check ----
if (-not (Test-Path .env)) {
    Write-Log ".env not found; copying .env.demo.example -> .env"
    Copy-Item .env.demo.example .env
    Write-Err "Now open .env and fill in ALVYS_CLIENT_SECRET before re-running."
    exit 1
}

# Load .env into current session so we can validate values.
Get-Content .env | ForEach-Object {
    if ($_ -match '^\s*#') { return }
    if ($_ -match '^\s*$') { return }
    $parts = $_ -split '=', 2
    if ($parts.Count -eq 2) {
        Set-Item -Path "Env:$($parts[0].Trim())" -Value $parts[1].Trim()
    }
}

if ($env:ACCESS_POLICY_MODE -ne 'Demo') {
    Write-Err "ACCESS_POLICY_MODE must equal 'Demo' in .env for the demo runner. Aborting."
    exit 1
}
if ([string]::IsNullOrWhiteSpace($env:ALVYS_CLIENT_SECRET) -or $env:ALVYS_CLIENT_SECRET -like '*paste*') {
    Write-Err "ALVYS_CLIENT_SECRET is not set in .env. Paste the Value Truck va336 secret and re-run."
    exit 1
}

# ---- Boot ----
Write-Log "Building and starting containers (this takes ~2-3 min on the first run)..."
docker compose up -d --build
if ($LASTEXITCODE -ne 0) { Write-Err "docker compose up failed."; exit 1 }

# ---- Wait for API health ----
Write-Log "Waiting for API to report healthy on http://localhost:5072/api/health ..."
$deadline = (Get-Date).AddSeconds(180)
while ($true) {
    try {
        $r = Invoke-WebRequest -Uri http://localhost:5072/api/health -UseBasicParsing -TimeoutSec 3
        if ($r.StatusCode -eq 200) { Write-Log "API is healthy."; break }
    } catch { }
    if ((Get-Date) -gt $deadline) {
        Write-Err "API failed to report healthy within 180s. Check 'docker compose logs api' for details."
        exit 1
    }
    Start-Sleep -Seconds 3
}

Write-Host @"

============================================================
  LTL demo is running.

  Web UI:       http://localhost:4200
  API:          http://localhost:5072/api
  Swagger:      http://localhost:5072/swagger

  Demo script:
    1. Open http://localhost:4200/ltl
    2. Enter origin=Laredo, dest=Dallas -> Search
    3. Click a load row to open the detail drawer
    4. Click "Consolidate" -> plan preview with driver RPM
    5. Copy the click-card text to paste into Alvys

  Stop the stack:  docker compose down
  Tail logs:       docker compose logs -f api web
============================================================
"@
