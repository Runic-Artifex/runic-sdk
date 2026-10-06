#!/usr/bin/env bash
set -euo pipefail

# Packs Runic.Assets and its in-repository dependencies from this checkout, or
# uses the .nupkg files in an existing feed, then restores the consumer from a
# fresh package cache.
# Usage: Test-PackageConsumer.sh [package-version] [existing-feed]
repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
fixture_root="$repository_root/tests/fixtures/assets/Runic.Assets.PackageConsumer"
test_root="$(mktemp -d)"
trap 'rm -rf "$test_root"' EXIT
package_version="${1:-0.0.0-local.1}"
package_feed="$test_root/feed"
consumer_root="$test_root/consumer"
nuget_config="$test_root/NuGet.config"
configuration="${CONFIGURATION:-Release}"

mkdir -p "$package_feed"
if [[ $# -ge 2 ]]; then
  cp "$2"/*.nupkg "$package_feed"
else
  # Runic.Assets packs the packer's build output; restore it for a clean clone.
  dotnet restore "$repository_root/tools/Runic.Assets.Packer/Runic.Assets.Packer.csproj"
  for project in \
    packages/dotnet/Runic.Desktop/Runic.Desktop.csproj \
    packages/dotnet/Runic.Assets/Runic.Assets.csproj \
    packages/dotnet/Runic.Assets.AspNetCore/Runic.Assets.AspNetCore.csproj \
    packages/dotnet/Runic.Assets.Desktop/Runic.Assets.Desktop.csproj; do
    dotnet pack "$repository_root/$project" \
      --configuration "$configuration" \
      -p:PackageVersion="$package_version" \
      --output "$package_feed"
  done
fi

export NUGET_PACKAGES="$test_root/packages"
mkdir -p "$consumer_root"
cp "$fixture_root/Program.cs" "$consumer_root/Program.cs"
cp -R "$fixture_root/vite-dist" "$consumer_root/vite-dist"
cp "$fixture_root/Runic.Assets.PackageConsumer.csproj" \
  "$consumer_root/Runic.Assets.PackageConsumer.csproj"
sed "s|__LOCAL_FEED__|$package_feed|g" \
  "$fixture_root/NuGet.config.template" > "$nuget_config"

dotnet restore "$consumer_root/Runic.Assets.PackageConsumer.csproj" \
  --configfile "$nuget_config" \
  -p:RunicAssetsPackageVersion="$package_version" \
  --no-cache
dotnet publish "$consumer_root/Runic.Assets.PackageConsumer.csproj" \
  --configuration Release \
  --no-restore \
  -p:RunicAssetsPackageVersion="$package_version" \
  --output "$test_root/publish"
"$test_root/publish/Runic.Assets.PackageConsumer"

packer_path="$test_root/packages/runic.assets/$package_version/tools/net10.0/Runic.Assets.Packer.dll"
if [[ ! -f "$packer_path" ]]; then
  echo "Packaged Runic Assets packer was not extracted." >&2
  exit 1
fi

tool_fixture="$test_root/packer-fixture"
mkdir -p "$tool_fixture"
printf '<main>packaged tool</main>' > "$tool_fixture/index.html"
tool_archive="$tool_fixture/output.runic-assets"
dotnet "$packer_path" "$tool_fixture" "$tool_archive" --trusted-generated-output
cp "$tool_archive" "$test_root/packaged-tool-first.runic-assets"
dotnet "$packer_path" "$tool_fixture" "$tool_archive" --trusted-generated-output
cmp "$test_root/packaged-tool-first.runic-assets" "$tool_archive"

usage="RCLI1005: A required command argument is missing. (source-directory)"
check_failure() {
  local expected_exit="$1"
  shift
  local stderr_path="$test_root/packer.stderr"

  set +e
  "$@" > /dev/null 2> "$stderr_path"
  local actual_exit=$?
  set -e

  if [[ "$actual_exit" -ne "$expected_exit" ]]; then
    echo "Expected packer exit code $expected_exit, got $actual_exit." >&2
    exit 1
  fi
}

check_failure 2 dotnet "$packer_path"
if [[ "$(<"$test_root/packer.stderr")" != "$usage" ]]; then
  echo "Packer usage diagnostics changed." >&2
  exit 1
fi

missing_source="$test_root/missing-source"
check_failure 3 dotnet "$packer_path" "$missing_source" "$tool_fixture/missing.runic-assets" --trusted-generated-output
if [[ "$(<"$test_root/packer.stderr")" != "Source directory '$missing_source' does not exist." ]]; then
  echo "Packer missing-source diagnostic changed." >&2
  exit 1
fi

check_failure 4 dotnet "$packer_path" "$tool_fixture" "$tool_fixture/missing-entry.runic-assets" --entry-point missing.html --trusted-generated-output
if [[ "$(<"$test_root/packer.stderr")" != "Entry point 'missing.html' does not exist below '$tool_fixture' or was excluded." ]]; then
  echo "Packer missing-entry diagnostic changed." >&2
  exit 1
fi

repeat_fixture="$test_root/packer-repeat-fixture"
mkdir -p "$repeat_fixture"
printf '<main>repeated exclusions</main>' > "$repeat_fixture/index.html"
printf 'first excluded asset' > "$repeat_fixture/first.txt"
printf 'second excluded asset' > "$repeat_fixture/second.txt"
included_archive="$test_root/included.runic-assets"
excluded_archive="$test_root/excluded.runic-assets"
dotnet "$packer_path" "$repeat_fixture" "$included_archive" --trusted-generated-output
dotnet "$packer_path" "$repeat_fixture" "$excluded_archive" --exclude first.txt --exclude second.txt --trusted-generated-output
if cmp -s "$included_archive" "$excluded_archive"; then
  echo "Repeated packer exclusions did not affect the archive." >&2
  exit 1
fi
