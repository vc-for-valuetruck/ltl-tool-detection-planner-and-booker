#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

echo "Preparing dev container workspace..."

if [ -f ".env.example" ] && [ ! -f ".env" ]; then
  cp .env.example .env
  echo "Created .env from .env.example"
fi

echo "Restoring .NET packages..."
dotnet restore

if [ -d "web" ]; then
  echo "Installing web dependencies..."
  (cd web && npm install)
fi

echo "Pre-pulling Docker images (best effort)..."
docker compose pull || true

echo "Dev container setup complete."
echo "  Local stack    : make up   (SQL Server + API + Web)"
echo "  Azure hosting  : see docs/AZURE_HOSTING.md"
