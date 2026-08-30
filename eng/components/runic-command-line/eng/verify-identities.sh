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

for project in Runic.CommandLine Runic.CommandLine.Processes Runic.CommandLine.Testing; do
  project_file="src/$project/$project.csproj"
  grep -Fq "<AssemblyName>$project</AssemblyName>" "$project_file"
  grep -Fq "<RootNamespace>$project</RootNamespace>" "$project_file"
  grep -Fq "<PackageId>$project</PackageId>" "$project_file"
done

if git grep -n -E 'namespace RunicCommandLine|using RunicCommandLine|PackageReference Include="Runic\.CommandLine\.(Abstractions|Hosting)"' -- . ':(exclude)eng/verify-identities.sh'; then
  echo "Retired Runic Command Line namespaces or package references remain." >&2
  exit 1
fi

retired_package_paths="$(git ls-files | grep -E '(^|/)RunicCommandLine\.(Abstractions|Hosting)(/|\.)' || true)"
if [[ -n "$retired_package_paths" ]]; then
  echo "Retired Runic Command Line package paths remain:" >&2
  echo "$retired_package_paths" >&2
  exit 1
fi

echo "Runic Command Line identity boundary verified."
