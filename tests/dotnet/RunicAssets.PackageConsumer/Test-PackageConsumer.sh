#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
fixture_root="$repository_root/tests/RunicAssets.PackageConsumer"
test_root="$(mktemp -d)"
trap 'rm -rf "$test_root"' EXIT
package_version="${1:-0.1.0-preview.local.1}"
package_feed="${2:-$test_root/feed}"
consumer_root="$test_root/consumer"
nuget_config="$test_root/NuGet.config"

if [[ $# -lt 2 ]]; then
  "$repository_root/eng/pack.sh" "$package_version" "$package_feed"
fi

export NUGET_PACKAGES="$test_root/packages"
mkdir -p "$consumer_root"
cp "$repository_root/tests/RunicAssets.PackageConsumer/Program.cs" "$consumer_root/Program.cs"
cp -R "$repository_root/tests/RunicAssets.PackageConsumer/vite-dist" "$consumer_root/vite-dist"
cp "$repository_root/tests/RunicAssets.PackageConsumer/RunicAssets.PackageConsumer.csproj" \
  "$consumer_root/RunicAssets.PackageConsumer.csproj"
sed "s|__LOCAL_FEED__|$package_feed|g" \
  "$fixture_root/NuGet.config.template" > "$nuget_config"

dotnet restore "$consumer_root/RunicAssets.PackageConsumer.csproj" \
  --configfile "$nuget_config" \
  -p:RunicAssetsPackageVersion="$package_version" \
  --no-cache
dotnet publish "$consumer_root/RunicAssets.PackageConsumer.csproj" \
  --configuration Release \
  --no-restore \
  -p:RunicAssetsPackageVersion="$package_version" \
  --output "$test_root/publish"
"$test_root/publish/RunicAssets.PackageConsumer"
