#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <package-version> <package-directory>" >&2
  exit 2
fi

package_version="$1"
package_directory="$(cd "$2" && pwd)"
repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
project="$repository_root/tests/RunicToolkit.PackageCanary/RunicToolkit.PackageCanary.csproj"
canary_tmp="$(mktemp -d /tmp/runic-toolkit-package-canary.XXXXXXXXXX)"

cleanup() {
  case "$canary_tmp" in
    /tmp/runic-toolkit-package-canary.*) rm -rf -- "$canary_tmp" ;;
    *) echo "Refusing to remove unexpected path: $canary_tmp" >&2 ;;
  esac
}
trap cleanup EXIT

export NUGET_PACKAGES="$canary_tmp/nuget"

expected_packages=(
  Runic.Application
  Runic.Application.Desktop
  Runic.Application.Hosting
  Runic.Application.Testing
  Runic.Application.Bridge
  dotnet-runic
  Runic.Application.Templates
)
mapfile -t actual_packages < <(find "$package_directory" -maxdepth 1 -type f -name '*.nupkg' -printf '%f\n' | sort)
if [[ ${#actual_packages[@]} -ne ${#expected_packages[@]} ]]; then
  echo "Expected exactly ${#expected_packages[@]} canonical package artifacts, found ${#actual_packages[@]}." >&2
  exit 1
fi
for package in "${expected_packages[@]}"; do
  if [[ ! -f "$package_directory/$package.$package_version.nupkg" ]]; then
    echo "Missing canonical package artifact: $package.$package_version.nupkg" >&2
    exit 1
  fi
done

restore_properties=(-p:PackageVersion="$package_version" -p:PackageDirectory="$package_directory")
if [[ -n "${RUNIC_VERIFICATION_FEED:-}" ]]; then
  restore_properties+=(-p:RunicVerificationFeed="$RUNIC_VERIFICATION_FEED")
fi

dotnet restore "$project" \
  --no-cache \
  "${restore_properties[@]}"
dotnet run \
  --project "$project" \
  --configuration Release \
  --no-restore \
  "${restore_properties[@]}"

dotnet tool install dotnet-runic \
  --tool-path "$canary_tmp/tools" \
  --version "$package_version" \
  --add-source "$package_directory"

"$canary_tmp/tools/dotnet-runic" --help
bash "$repository_root/tests/RunicToolkit.PackageCanary/Test-ToolParsePresentation.sh" "$canary_tmp/tools/dotnet-runic"
bash "$repository_root/tests/RunicToolkit.PackageCanary/Test-ToolMigration.sh" "$canary_tmp/tools/dotnet-runic"
