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

dotnet restore "$project" \
  --no-cache \
  -p:PackageVersion="$package_version" \
  -p:PackageDirectory="$package_directory"
dotnet run \
  --project "$project" \
  --configuration Release \
  --no-restore \
  -p:PackageVersion="$package_version" \
  -p:PackageDirectory="$package_directory"

dotnet tool install RunicToolkit.MVVM.BindingCompiler \
  --tool-path "$canary_tmp/tools" \
  --version "$package_version" \
  --add-source "$package_directory"
dotnet tool install RunicToolkit.DotNet.RunicToolkit \
  --tool-path "$canary_tmp/tools" \
  --version "$package_version" \
  --add-source "$package_directory"

"$canary_tmp/tools/runic-toolkit-bindings" --help >/dev/null
"$canary_tmp/tools/dotnet-runic-toolkit" --help >/dev/null
