#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 2 || $# -gt 3 ]]; then
  echo "Usage: $0 <package-version> <package-directory> [--preflight-only]" >&2
  exit 2
fi

package_version="$1"
package_directory="$2"
mode="${3:-}"
registry="https://registry.npmjs.org"

if [[ -n "$mode" && "$mode" != "--preflight-only" ]]; then
  echo "Unknown option '$mode'." >&2
  exit 2
fi
if [[ ! "$package_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([+-][0-9A-Za-z.-]+)?$ ]]; then
  echo "'$package_version' is not a SemVer-compatible version." >&2
  exit 2
fi

package="$package_directory/runic-artifex-application-bridge-$package_version.tgz"
if [[ ! -f "$package" ]]; then
  echo "Expected npm package was not produced: $package" >&2
  exit 1
fi

manifest="$(tar -xOf "$package" package/package.json)"
package_name="$(node -e 'process.stdout.write(JSON.parse(process.argv[1]).name)' "$manifest")"
manifest_version="$(node -e 'process.stdout.write(JSON.parse(process.argv[1]).version)' "$manifest")"
if [[ "$package_name" != "@runic-artifex/application-bridge" || "$manifest_version" != "$package_version" ]]; then
  echo "The npm artifact has unexpected identity '$package_name@$manifest_version'." >&2
  exit 1
fi

local_integrity="$(node -e '
  const { createHash } = require("node:crypto");
  const { readFileSync } = require("node:fs");
  process.stdout.write(`sha512-${createHash("sha512").update(readFileSync(process.argv[1])).digest("base64")}`);
' "$package")"

view_output_file="$(mktemp)"
trap 'rm -f "$view_output_file"' EXIT
publish_required=true
if npm view "$package_name@$package_version" dist.integrity \
    --registry "$registry" --json >"$view_output_file" 2>&1; then
  published_integrity="$(node -e '
    const { readFileSync } = require("node:fs");
    const value = JSON.parse(readFileSync(process.argv[1], "utf8"));
    process.stdout.write(Array.isArray(value) ? value[0] : value);
  ' "$view_output_file")"
  if [[ "$published_integrity" != "$local_integrity" ]]; then
    echo "$package_name@$package_version already exists with different artifact integrity." >&2
    exit 1
  fi
  publish_required=false
  echo "$package_name@$package_version already matches the verified artifact; it will be skipped."
else
  if ! grep -q 'E404' "$view_output_file"; then
    cat "$view_output_file" >&2
    echo "Could not determine publication state for $package_name@$package_version." >&2
    exit 1
  fi
fi

if [[ "$mode" == "--preflight-only" || "$publish_required" != "true" ]]; then
  exit 0
fi

dist_tag="latest"
if [[ "$package_version" == *-* ]]; then
  dist_tag="preview"
fi

npm publish "$package" \
  --registry "$registry" \
  --access public \
  --tag "$dist_tag" \
  --provenance
