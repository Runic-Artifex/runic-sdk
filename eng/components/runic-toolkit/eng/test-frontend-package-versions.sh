#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$repository_root/eng/frontend-package-versions.sh"

assert_versions() {
  local expected="$1"
  local actual="$APPLICATION_BRIDGE_NPM_VERSION|$RUNIC_SVELTE_NPM_VERSION|$RUNIC_VITE_NPM_VERSION|$RUNIC_DESKTOP_NPM_VERSION"
  if [[ "$actual" != "$expected" ]]; then
    echo "Resolved frontend package versions '$actual', expected '$expected'." >&2
    exit 1
  fi
}

runic_resolve_frontend_package_versions \
  "1.0.0-preview.1" "1.0.0-ci.shaapplication" "0" "" "" ""
assert_versions \
  "1.0.0-ci.shaapplication|1.0.0-preview.1|1.0.0-preview.1|1.0.0-preview.1"

runic_resolve_frontend_package_versions \
  "1.0.0-preview.1" "1.0.0-ci.shaapplication" "0" \
  "1.0.0-ci.shasvelte" "1.0.0-ci.shavite" "1.0.0-ci.shadesktop"
assert_versions \
  "1.0.0-ci.shaapplication|1.0.0-ci.shasvelte|1.0.0-ci.shavite|1.0.0-ci.shadesktop"

runic_resolve_frontend_package_versions \
  "1.0.0-preview.1" "1.0.0-ci.shaapplication" "1" \
  "1.0.0-ci.shasvelte" "1.0.0-ci.shavite" "1.0.0-ci.shadesktop"
assert_versions \
  "1.0.0-ci.shaapplication|1.0.0-preview.1|1.0.0-preview.1|1.0.0-preview.1"

echo "Frontend package version selection checks passed."
