#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
test_root="$(mktemp -d)"
trap 'rm -rf "$test_root"' EXIT
package_version="${1:-1.0.0}"
package_feed="${2:-$test_root/feed}"
consumer_root="$test_root/consumer"

if [[ $# -lt 2 ]]; then
  dotnet pack "$repository_root/src/RunicAssets/RunicAssets.csproj" \
    --configuration Release \
    --output "$package_feed" \
    -p:PackageVersion="$package_version"
fi

export NUGET_PACKAGES="$test_root/packages"
mkdir -p "$consumer_root"
cp "$repository_root/tests/RunicAssets.PackageConsumer/Program.cs" "$consumer_root/Program.cs"
cp "$repository_root/tests/RunicAssets.PackageConsumer/index.html" "$consumer_root/index.html"
cp "$repository_root/tests/RunicAssets.PackageConsumer/RunicAssets.PackageConsumer.csproj" \
  "$consumer_root/RunicAssets.PackageConsumer.csproj"

dotnet restore "$consumer_root/RunicAssets.PackageConsumer.csproj" \
  --source "$package_feed" \
  --source "https://api.nuget.org/v3/index.json" \
  -p:RunicAssetsPackageVersion="$package_version" \
  --no-cache
dotnet publish "$consumer_root/RunicAssets.PackageConsumer.csproj" \
  --configuration Release \
  --no-restore \
  -p:RunicAssetsPackageVersion="$package_version" \
  --output "$test_root/publish"
"$test_root/publish/RunicAssets.PackageConsumer"
