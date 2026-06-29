#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

echo "Preparing UAT Codespaces workspace..."

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

echo "Codespaces setup complete."
echo "  Docker stack : make up"
echo "  Lightweight  : bash scripts/start-codespaces-demo.sh"
