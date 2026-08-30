#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

if [[ -z "${WEBUI_BROWSER_PATH:-}" || ! -x "$WEBUI_BROWSER_PATH" ]]; then
  echo "WEBUI_BROWSER_PATH must name the pinned Chromium executable." >&2
  exit 1
fi

RUNIC_HOSTED_BROWSER_E2E=1 dotnet run \
  --project tests/Runic.Application.Hosting.Tests/Runic.Application.Hosting.Tests.csproj \
  --configuration Release \
  --no-build \
  --no-restore
