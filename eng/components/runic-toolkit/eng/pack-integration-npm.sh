#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 6 ]]; then
  echo "Usage: $0 <package-version> <package-directory> <output-directory> <desktop-worktree> <svelte-worktree> <vite-worktree>" >&2
  exit 2
fi

package_version="$1"
package_directory="$2"
output_directory="$3"
desktop_worktree="$4"
svelte_worktree="$5"
vite_worktree="$6"

mkdir -p "$output_directory"

(
  cd "$desktop_worktree"
  bun install --frozen-lockfile
  bun run build
  cd web/packages/desktop
  bun pm pack --ignore-scripts --destination "$output_directory"
)
(
  cd "$svelte_worktree"
  APPLICATION_BRIDGE_ARCHIVE="$package_directory/runic-artifex-application-bridge-$package_version.tgz" \
    node --input-type=module --eval '
      import { readFileSync, writeFileSync } from "node:fs";
      const path = "package.json";
      const manifest = JSON.parse(readFileSync(path, "utf8"));
      manifest.devDependencies["@runic-artifex/application-bridge"] =
        `file:${process.env.APPLICATION_BRIDGE_ARCHIVE}`;
      writeFileSync(path, `${JSON.stringify(manifest, null, 2)}\n`);
    '
  bun install --ignore-scripts
  bun run --filter @runic-artifex/svelte build
  cd packages/svelte
  bun pm pack --destination "$output_directory"
)
(
  cd "$vite_worktree"
  bun install --frozen-lockfile
  bun run build
  bun pm pack --ignore-scripts --destination "$output_directory"
)
