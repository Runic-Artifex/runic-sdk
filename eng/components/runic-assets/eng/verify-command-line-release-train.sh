#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <runic-command-line-package-feed>" >&2
  exit 2
fi

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
package_feed="$1"
if [[ ! -d "$package_feed" ]]; then
  echo "Runic Command Line package feed '$package_feed' does not exist." >&2
  exit 2
fi

nuget_config="$(mktemp)"
trap 'rm -f "$nuget_config"' EXIT
sed "s|__RUN_COMMAND_LINE_FEED__|$package_feed|g" \
  "$repository_root/eng/NuGet.command-line-release-train.config.template" > "$nuget_config"

NUGET_CONFIG_FILE="$nuget_config" "$repository_root/eng/verify.sh"
