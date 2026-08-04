#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <package-version> <output-directory>" >&2
  exit 2
fi

package_version="$1"
output_directory="$2"
configuration="Release"
repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [[ ! "$package_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([+-][0-9A-Za-z.-]+)?$ ]]; then
  echo "Package version must be a SemVer-compatible version such as 0.1.0-preview.1." >&2
  exit 2
fi

mkdir -p "$output_directory"

package_projects=(
  "$repository_root/src/RunicCommandLine.Abstractions/RunicCommandLine.Abstractions.csproj"
  "$repository_root/src/RunicCommandLine/RunicCommandLine.csproj"
  "$repository_root/src/RunicCommandLine.Hosting/RunicCommandLine.Hosting.csproj"
  "$repository_root/src/RunicCommandLine.Processes/RunicCommandLine.Processes.csproj"
)

for project in "${package_projects[@]}"; do
  dotnet pack "$project" --configuration "$configuration" --no-restore \
    -p:PackageVersion="$package_version" --output "$output_directory"
done

expected_packages=(
  "RunicCommandLine.Abstractions.$package_version.nupkg"
  "RunicCommandLine.$package_version.nupkg"
  "RunicCommandLine.Hosting.$package_version.nupkg"
  "RunicCommandLine.Processes.$package_version.nupkg"
)

for package in "${expected_packages[@]}"; do
  if [[ ! -f "$output_directory/$package" ]]; then
    echo "Expected package was not produced: $package" >&2
    exit 1
  fi
done

actual_package_count="$(find "$output_directory" -maxdepth 1 -type f -name '*.nupkg' | wc -l)"
if [[ "$actual_package_count" -ne "${#expected_packages[@]}" ]]; then
  echo "Expected ${#expected_packages[@]} packages, found $actual_package_count." >&2
  exit 1
fi
