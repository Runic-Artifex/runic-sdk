#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <package-version> <package-directory>" >&2
  exit 2
fi

package_version="$1"
package_directory="$2"
dist_tag="latest"
if [[ "$package_version" == *-* ]]; then
  dist_tag="preview"
fi

archives=(
  "runic-artifex-mvvm-$package_version.tgz"
  "runic-artifex-mvvm-conformance-$package_version.tgz"
  "runic-artifex-mvvm-angular-$package_version.tgz"
  "runic-artifex-mvvm-react-$package_version.tgz"
  "runic-artifex-mvvm-svelte-$package_version.tgz"
  "runic-artifex-mvvm-vue-$package_version.tgz"
)

for archive in "${archives[@]}"; do
  package="$package_directory/$archive"
  if [[ ! -f "$package" ]]; then
    echo "Expected npm package was not produced: $package" >&2
    exit 1
  fi
  npm publish "$package" \
    --registry https://registry.npmjs.org \
    --access public \
    --tag "$dist_tag" \
    --provenance
done
