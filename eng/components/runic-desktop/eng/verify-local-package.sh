#!/usr/bin/env bash
set -euo pipefail

repository_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
runtime_id=${1:-linux-x64}
package_version=1.0.0-preview.1
work_dir=$(mktemp -d "${TMPDIR:-/tmp}/runic-desktop-package.XXXXXXXX")
package_dir="$work_dir/packages"
consumer_dir="$work_dir/consumer"
windows_consumer_dir="$work_dir/windows-consumer"
nuget_dir="$work_dir/nuget"
publish_dir="$work_dir/publish"

cleanup() {
  if [[ "$work_dir" == "${TMPDIR:-/tmp}/runic-desktop-package."* ]]; then
    rm -rf -- "$work_dir"
  fi
}
trap cleanup EXIT

mkdir -p "$package_dir" "$consumer_dir" "$windows_consumer_dir" "$nuget_dir" "$publish_dir"

dotnet pack "$repository_dir/src/Runic.Desktop/Runic.Desktop.csproj" \
  --configuration Release \
  --output "$package_dir" \
  -p:PackageVersion="$package_version"

package_archive="$package_dir/Runic.Desktop.$package_version.nupkg"
if [[ ! -f "$package_archive" ]]; then
  printf 'The expected Runic.Desktop package archive was not produced.\n' >&2
  exit 1
fi
package_hash=$(node --input-type=module --eval '
  import { createHash } from "node:crypto";
  import { readFileSync } from "node:fs";
  process.stdout.write(createHash("sha512").update(readFileSync(process.argv[1])).digest("base64"));
' "$package_archive")

cp -R "$repository_dir/tests/Runic.Desktop.PackageConsumer/." "$consumer_dir/"
cp -R "$repository_dir/tests/Runic.Desktop.PackageConsumer.Windows/." "$windows_consumer_dir/"

dotnet restore "$consumer_dir/Runic.Desktop.PackageConsumer.csproj" \
  --packages "$nuget_dir" \
  --source "$package_dir" \
  --source https://api.nuget.org/v3/index.json \
  --runtime "$runtime_id" \
  -p:PublishAot=true \
  -p:RunicDesktopPackageVersion="$package_version"

extracted_package="$nuget_dir/runic.desktop/$package_version"
package_hash_file=$(find "$extracted_package" -maxdepth 1 -name '*.nupkg.sha512' -print -quit)
if [[ -z "$package_hash_file" ]] || [[ "$(tr -d '[:space:]' < "$package_hash_file")" != "$package_hash" ]]; then
  printf 'NuGet did not preserve the packed archive SHA-512 while extracting the clean consumer package.\n' >&2
  exit 1
fi
for package_file in \
  'lib/net10.0/Runic.Desktop.dll' \
  'README.md' \
  'NOTICE' \
  'licenses/WebUI/WebUI-LICENSE.txt' \
  'buildTransitive/Runic.Desktop.targets'; do
  if [[ ! -f "$extracted_package/$package_file" ]]; then
    printf 'The extracted package is missing required release content: %s\n' "$package_file" >&2
    exit 1
  fi
done

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

dotnet restore "$windows_consumer_dir/Runic.Desktop.PackageConsumer.Windows.csproj" \
  --packages "$nuget_dir" \
  --source "$package_dir" \
  --source https://api.nuget.org/v3/index.json \
  --runtime win-x64 \
  -p:RunicDesktopPackageVersion="$package_version"

dotnet build "$windows_consumer_dir/Runic.Desktop.PackageConsumer.Windows.csproj" \
  --configuration Release \
  --no-restore \
  --runtime win-x64 \
  -p:RunicDesktopPackageVersion="$package_version"

windows_output_dir="$windows_consumer_dir/bin/Release/net10.0/win-x64"
for windows_asset in \
  'Microsoft.Web.WebView2.Core.dll' \
  'runtimes/win-x64/native/WebView2Loader.dll'; do
  if [[ ! -f "$windows_output_dir/$windows_asset" ]]; then
    printf 'The clean Windows package consumer is missing required WebView2 runtime content: %s\n' "$windows_asset" >&2
    exit 1
  fi
done
for excluded_control in \
  'Microsoft.Web.WebView2.Wpf.dll' \
  'Microsoft.Web.WebView2.WinForms.dll' \
  'WindowsBase.dll'; do
  if [[ -f "$windows_output_dir/$excluded_control" ]]; then
    printf 'The clean Windows package consumer unexpectedly received a WebView2 UI-framework control: %s\n' "$excluded_control" >&2
    exit 1
  fi
done
if ! grep --extended-regexp --quiet 'Microsoft\.Web\.WebView2\.Core' "$windows_output_dir/Runic.Desktop.PackageConsumer.Windows.deps.json"; then
  printf 'The clean Windows package consumer dependency manifest does not name Microsoft.Web.WebView2.Core.\n' >&2
  exit 1
fi
printf 'Exact package, Linux NativeAOT, and Windows runtime-asset consumers passed for %s\n' "$runtime_id"
