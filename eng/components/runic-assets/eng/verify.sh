#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

solution="RunicAssets.slnx"
configuration="Release"

./eng/verify-identities.sh

dotnet restore "$solution" -p:RunicAssetsBuildMode=Verification
dotnet build "$solution" --configuration "$configuration" --no-restore \
  -p:RunicAssetsBuildMode=Verification
dotnet run \
  --project tests/RunicAssets.Tests/RunicAssets.Tests.csproj \
  --configuration "$configuration" \
  --no-build

aot_publish_root="$(mktemp -d)"
trap 'rm -rf "$aot_publish_root"' EXIT
dotnet publish \
  tests/RunicAssets.CsWebUiAotSmoke/RunicAssets.CsWebUiAotSmoke.csproj \
  --configuration "$configuration" \
  --no-restore \
  --output "$aot_publish_root"
"$aot_publish_root/RunicAssets.CsWebUiAotSmoke"

./tests/RunicAssets.PackageConsumer/Test-PackageConsumer.sh
