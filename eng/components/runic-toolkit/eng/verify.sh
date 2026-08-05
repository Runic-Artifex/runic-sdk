#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

configuration="Release"
build_properties=(
  -p:RunicToolkitBuildMode=Verification
  -p:RunicToolkitFrontendBuild=false
  -p:RunicToolkitFrontendInstall=false
)

pwsh -NoProfile -File eng/verify-namespaces.ps1
pwsh -NoProfile -File eng/verify-solution.ps1
pwsh -NoProfile -File eng/verify-architecture.ps1

npm ci
npm run verify

dotnet restore RunicToolkit.slnx "${build_properties[@]}"
dotnet build RunicToolkit.slnx \
  --configuration "$configuration" \
  --no-restore \
  "${build_properties[@]}"

pwsh -NoProfile \
  -File eng/run-contract-tests.ps1 \
  -Configuration "$configuration"
