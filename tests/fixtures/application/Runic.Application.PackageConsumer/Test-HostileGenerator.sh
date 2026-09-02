#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <package-version> <package-directory>" >&2
  exit 2
fi

package_version="$1"
package_directory="$(cd "$2" && pwd)"
repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
hostile_root="$(mktemp -d /tmp/runic-application-hostile.XXXXXXXXXX)"

cleanup() {
  case "$hostile_root" in
    /tmp/runic-application-hostile.*) rm -rf -- "$hostile_root" ;;
    *) echo "Refusing to remove unexpected path: $hostile_root" >&2 ;;
  esac
}
trap cleanup EXIT

export NUGET_PACKAGES="$hostile_root/nuget"

cp "$repository_root/tests/Runic.Application.PackageConsumer/hostile/Hostile.csproj.template" "$hostile_root/Hostile.csproj"
cp "$repository_root/tests/Runic.Application.PackageConsumer/hostile/Program.cs" "$hostile_root/Program.cs"

restore_options=(--no-cache)
if [[ -n "${NUGET_CONFIG_FILE:-}" ]]; then
  restore_options+=(--configfile "$NUGET_CONFIG_FILE")
fi
restore_properties=(-p:PackageVersion="$package_version" -p:PackageDirectory="$package_directory")
if [[ -n "${RUNIC_VERIFICATION_FEED:-}" ]]; then
  restore_properties+=(-p:RunicVerificationFeed="$RUNIC_VERIFICATION_FEED")
fi
dotnet restore "$hostile_root/Hostile.csproj" "${restore_options[@]}" "${restore_properties[@]}"

if dotnet build "$hostile_root/Hostile.csproj" --no-restore \
  -p:PackageVersion="$package_version" \
  -p:PackageDirectory="$package_directory" > "$hostile_root/build.log" 2>&1; then
  echo "The hostile bridge composition unexpectedly compiled." >&2
  exit 1
fi

if ! grep -Fq 'RAPP0002' "$hostile_root/build.log"; then
  echo "The hostile bridge composition did not produce RAPP0002." >&2
  cat "$hostile_root/build.log" >&2
  exit 1
fi
if grep -Eq 'error CS(1503|1729|0144)' "$hostile_root/build.log"; then
  echo "Bridge composition exposed an unowned compiler error." >&2
  cat "$hostile_root/build.log" >&2
  exit 1
fi
