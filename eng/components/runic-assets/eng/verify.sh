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

./tests/RunicAssets.PackageConsumer/Test-PackageConsumer.sh
