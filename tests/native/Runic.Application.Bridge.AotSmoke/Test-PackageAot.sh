#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <package-version> <package-directory>" >&2
  exit 2
fi

package_version="$1"
package_directory="$(cd "$2" && pwd)"
repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
runtime_identifier="${RUNTIME_IDENTIFIER:-linux-x64}"
aot_tmp="$(mktemp -d /tmp/runic-toolkit-application-bridge-aot.XXXXXXXXXX)"

cleanup() {
  case "$aot_tmp" in
    /tmp/runic-toolkit-application-bridge-aot.*) rm -rf -- "$aot_tmp" ;;
    *) echo "Refusing to remove unexpected path: $aot_tmp" >&2 ;;
  esac
}
trap cleanup EXIT

cp "$repository_root/tests/Runic.Application.Bridge.AotSmoke/Program.cs" "$aot_tmp/Program.cs"
cp "$repository_root/tests/Runic.Application.Bridge.AotSmoke/Runic.Application.Bridge.AotSmoke.csproj" "$aot_tmp/Runic.Application.Bridge.AotSmoke.csproj"
cp -a "$repository_root/protocol/application-bridge/setup/generated" "$aot_tmp/Contract"
export NUGET_PACKAGES="$aot_tmp/nuget"

restore_options=()
if [[ -n "${NUGET_CONFIG_FILE:-}" ]]; then
  restore_options+=(--configfile "$NUGET_CONFIG_FILE")
fi
dotnet restore "$aot_tmp/Runic.Application.Bridge.AotSmoke.csproj" \
  "${restore_options[@]}" \
  --runtime "$runtime_identifier" \
  -p:ApplicationBridgeUsePackages=true \
  -p:ApplicationBridgeNativeAot=true \
  -p:ApplicationBridgeContractDirectory="$aot_tmp/Contract" \
  -p:PackageDirectory="$package_directory" \
  -p:PackageVersion="$package_version"
dotnet publish "$aot_tmp/Runic.Application.Bridge.AotSmoke.csproj" \
  --configuration Release \
  --no-restore \
  --runtime "$runtime_identifier" \
  --output "$aot_tmp/publish" \
  -p:ApplicationBridgeUsePackages=true \
  -p:ApplicationBridgeNativeAot=true \
  -p:ApplicationBridgeContractDirectory="$aot_tmp/Contract" \
  -p:PackageDirectory="$package_directory" \
  -p:PackageVersion="$package_version"

"$aot_tmp/publish/Runic.Application.Bridge.AotSmoke" | grep -Fx "application-bridge-aot-ok"
