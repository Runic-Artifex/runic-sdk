#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

retired_content_pattern='WebUI[T]oolkit\.Assets|webui[t]oolkit\.assets|RunicAssets\.(CsWebUi|RunicToolkit)'
if git grep -n -E "$retired_content_pattern" -- ':!eng/verify-identities.sh'; then
  echo "Retired Toolkit-owned asset identities remain in tracked content." >&2
  exit 1
fi

retired_paths="$(find . \( -path './.git' -o -name bin -o -name obj -o -name .packages -o -name .direnv \) \
  -prune -o \( -type f -o -type d \) -print | sed 's#^\./##' | \
  grep -E 'WebUI[T]oolkit\.Assets|webui[t]oolkit\.assets|RunicAssets\.(CsWebUi|RunicToolkit)' || true)"
if [[ -n "$retired_paths" ]]; then
  echo "Retired Toolkit-owned asset identities remain in tracked paths:" >&2
  echo "$retired_paths" >&2
  exit 1
fi

for project in Runic.Assets Runic.Assets.AspNetCore Runic.Assets.Desktop; do
  project_file="src/$project/$project.csproj"
  grep -Fq "<AssemblyName>$project</AssemblyName>" "$project_file"
  grep -Fq "<RootNamespace>$project</RootNamespace>" "$project_file"
  grep -Fq "<PackageId>$project</PackageId>" "$project_file"
done

grep -Fq 'runic.assets/1' src/Runic.Assets/AssetContracts.cs
grep -Fq 'runic.assets.archive/1' src/Runic.Assets/AssetArchive.cs

echo "Runic Assets identity boundary verified."
