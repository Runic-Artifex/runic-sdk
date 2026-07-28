#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
test_root="$(mktemp -d)"
trap 'rm -rf "$test_root"' EXIT

dotnet pack "$repository_root/src/WebUIToolkit.Assets/WebUIToolkit.Assets.csproj" \
  --configuration Release \
  --output "$test_root/feed" \
  -p:PackageVersion=1.0.0
dotnet restore "$repository_root/tests/WebUIToolkit.Assets.PackageConsumer/WebUIToolkit.Assets.PackageConsumer.csproj" \
  --source "$test_root/feed"
dotnet publish "$repository_root/tests/WebUIToolkit.Assets.PackageConsumer/WebUIToolkit.Assets.PackageConsumer.csproj" \
  --configuration Release \
  --no-restore \
  --output "$test_root/publish"
"$test_root/publish/WebUIToolkit.Assets.PackageConsumer"
