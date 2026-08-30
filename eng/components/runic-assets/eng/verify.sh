#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

solution="Runic.Assets.slnx"
configuration="Release"
restore_options=()
if [[ -n "${NUGET_CONFIG_FILE:-}" ]]; then
  restore_options+=(--configfile "$NUGET_CONFIG_FILE")
fi

./eng/verify-identities.sh

dotnet restore "$solution" -p:RunicAssetsBuildMode=Verification "${restore_options[@]}"
dotnet build "$solution" --configuration "$configuration" --no-restore \
  -p:RunicAssetsBuildMode=Verification
dotnet run \
  --project tests/Runic.Assets.Tests/Runic.Assets.Tests.csproj \
  --configuration "$configuration" \
  --no-build

aot_publish_root="$(mktemp -d)"
trap 'rm -rf "$aot_publish_root"' EXIT
packer_fixture="$aot_publish_root/packer-fixture"
mkdir -p "$packer_fixture"
printf '<main>NativeAOT packer</main>' > "$packer_fixture/index.html"
dotnet restore \
  src/Runic.Assets.Packer/Runic.Assets.Packer.csproj \
  -p:RunicAssetsBuildMode=Verification \
  -p:PublishAot=true \
  "${restore_options[@]}"
dotnet publish \
  src/Runic.Assets.Packer/Runic.Assets.Packer.csproj \
  --configuration "$configuration" \
  --no-restore \
  -p:PublishAot=true \
  -p:InvariantGlobalization=true \
  --output "$aot_publish_root/packer"
"$aot_publish_root/packer/Runic.Assets.Packer" \
  "$packer_fixture" \
  "$packer_fixture/output.runic-assets"

./tests/Runic.Assets.PackageConsumer/Test-PackageConsumer.sh
