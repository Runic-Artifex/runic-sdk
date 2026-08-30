#!/usr/bin/env bash
set -euo pipefail

repository_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
work_dir=$(mktemp -d "${TMPDIR:-/tmp}/runic-desktop-web-package.XXXXXXXX")
package_dir="$work_dir/package"
consumer_dir="$work_dir/consumer"

cleanup() {
  if [[ "$work_dir" == "${TMPDIR:-/tmp}/runic-desktop-web-package."* ]]; then
    rm -rf -- "$work_dir"
  fi
}
trap cleanup EXIT

mkdir -p "$package_dir" "$consumer_dir"
cd "$repository_dir"
package_version=$(node --input-type=module --eval '
  import { readFileSync } from "node:fs";
  process.stdout.write(JSON.parse(readFileSync("web/packages/desktop/package.json", "utf8")).version);
')
npm pack \
  --workspace @runic-artifex/desktop \
  --pack-destination "$package_dir" \
  --ignore-scripts

archive="$package_dir/runic-artifex-desktop-$package_version.tgz"
if [[ ! -f "$archive" ]]; then
  printf 'The expected TypeScript package archive was not produced.\n' >&2
  exit 1
fi

archive_files=$(tar -tzf "$archive")
if ! grep --extended-regexp --quiet '^package/dist/esm/index\.(js|d\.ts)$' <<<"$archive_files"; then
  printf 'The TypeScript package archive is missing its public entry point.\n' >&2
  exit 1
fi
if grep --extended-regexp --quiet '^package/(src|test)/' <<<"$archive_files"; then
  printf 'The TypeScript package archive contains source-only fixtures.\n' >&2
  exit 1
fi

cd "$consumer_dir"
npm init --yes >/dev/null
npm install --ignore-scripts "$archive" >/dev/null
node --input-type=module --eval '
  import {
    DesktopTransportLive,
    applicationBridgeCapability,
    createDesktopFrameChannel,
    wireProfile
  } from "@runic-artifex/desktop";
  if (typeof DesktopTransportLive !== "function" || typeof createDesktopFrameChannel !== "function") process.exit(1);
  if (applicationBridgeCapability !== "runic.desktop.application-bridge/1") process.exit(1);
  if (wireProfile !== "webui-compat/52f9e75") process.exit(1);
'

printf 'Exact @runic-artifex/desktop package consumer passed.\n'
