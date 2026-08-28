#!/usr/bin/env bash
set -euo pipefail

repository_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
runtime_id=${1:-linux-x64}
package_version=0.1.0-w90
work_dir=$(mktemp -d "${TMPDIR:-/tmp}/runic-desktop-package.XXXXXXXX")
package_dir="$work_dir/packages"
consumer_dir="$work_dir/consumer"
nuget_dir="$work_dir/nuget"
publish_dir="$work_dir/publish"

cleanup() {
  if [[ "$work_dir" == "${TMPDIR:-/tmp}/runic-desktop-package."* ]]; then
    rm -rf -- "$work_dir"
  fi
}
trap cleanup EXIT

mkdir -p "$package_dir" "$consumer_dir" "$nuget_dir" "$publish_dir"

dotnet pack "$repository_dir/src/Runic.Desktop/Runic.Desktop.csproj" \
  --configuration Release \
  --output "$package_dir" \
  -p:PackageVersion="$package_version"

cp -R "$repository_dir/tests/Runic.Desktop.PackageConsumer/." "$consumer_dir/"

dotnet restore "$consumer_dir/Runic.Desktop.PackageConsumer.csproj" \
  --packages "$nuget_dir" \
  --source "$package_dir" \
  --source https://api.nuget.org/v3/index.json \
  --runtime "$runtime_id" \
  -p:PublishAot=true \
  -p:RunicDesktopPackageVersion="$package_version"

dotnet build "$consumer_dir/Runic.Desktop.PackageConsumer.csproj" \
  --configuration Release \
  --no-restore \
  -p:RunicDesktopPackageVersion="$package_version"

dotnet run --project "$consumer_dir/Runic.Desktop.PackageConsumer.csproj" \
  --configuration Release \
  --no-build \
  --no-restore \
  -p:RunicDesktopPackageVersion="$package_version"

dotnet publish "$consumer_dir/Runic.Desktop.PackageConsumer.csproj" \
  --configuration Release \
  --no-restore \
  --runtime "$runtime_id" \
  --output "$publish_dir" \
  -p:PublishAot=true \
  -p:StripSymbols=true \
  -p:RunicDesktopPackageVersion="$package_version"

consumer_executable="$publish_dir/Runic.Desktop.PackageConsumer"
if [[ ! -x "$consumer_executable" && -f "$consumer_executable.exe" ]]; then
  consumer_executable="$consumer_executable.exe"
fi
if [[ ! -x "$consumer_executable" ]]; then
  printf 'NativeAOT publish did not produce the expected executable:\n' >&2
  find "$publish_dir" -maxdepth 1 -type f -printf '%f\n' >&2
  exit 1
fi

"$consumer_executable"
printf 'Exact package and NativeAOT consumer passed for %s\n' "$runtime_id"
