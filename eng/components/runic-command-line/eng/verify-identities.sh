#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

retired_content_pattern='WebUI[T]oolkit|webui[t]oolkit|WUT[C]LI|WEBUI[T]OOLKIT'
if git grep -n -E "$retired_content_pattern" -- .; then
  echo "Retired Toolkit-owned Command Line identities remain in tracked content." >&2
  exit 1
fi

retired_paths="$(git ls-files | grep -E 'WebUI[T]oolkit|webui[t]oolkit' || true)"
if [[ -n "$retired_paths" ]]; then
  echo "Retired Toolkit-owned Command Line identities remain in tracked paths:" >&2
  echo "$retired_paths" >&2
  exit 1
fi

echo "Runic Command Line identity boundary verified."
