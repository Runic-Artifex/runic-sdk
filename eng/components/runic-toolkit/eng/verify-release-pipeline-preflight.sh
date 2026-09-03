#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

bash eng/test-frontend-package-versions.sh
node eng/test-application-npm-package-inventory.mjs

if [[ "${1:-}" == "--github-output" ]]; then
  if [[ -z "${GITHUB_OUTPUT:-}" ]]; then
    echo "--github-output requires GITHUB_OUTPUT." >&2
    exit 2
  fi
  npm_package_count="$(node eng/application-npm-packages.mjs)"
  echo "npm-count=$npm_package_count" >> "$GITHUB_OUTPUT"
elif [[ $# -ne 0 ]]; then
  echo "Usage: bash eng/verify-release-pipeline-preflight.sh [--github-output]" >&2
  exit 2
fi
