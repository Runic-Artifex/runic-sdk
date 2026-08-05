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
repository_commit="$(git -C "$repository_root" rev-parse HEAD)"

if [[ ! "$package_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([+-][0-9A-Za-z.-]+)?$ ]]; then
  echo "Package version must be SemVer-compatible, for example 0.1.0-preview.1." >&2
  exit 2
fi

mkdir -p "$output_directory"
package_projects=(
  src/RunicToolkit.Collections/RunicToolkit.Collections.csproj
  src/RunicToolkit.Desktop/RunicToolkit.Desktop.csproj
  src/RunicToolkit.Frontend.Sdk/RunicToolkit.Frontend.Sdk.csproj
  src/RunicToolkit.Hosting.Abstractions/RunicToolkit.Hosting.Abstractions.csproj
  src/RunicToolkit.Hosting.Build/RunicToolkit.Hosting.Build.csproj
  src/RunicToolkit.Hosting.CsWebUi/RunicToolkit.Hosting.CsWebUi.csproj
  src/RunicToolkit.Hosting.CsWebUi.App/RunicToolkit.Hosting.CsWebUi.App.csproj
  src/RunicToolkit.Hosting.CsWebUi.Mvvm/RunicToolkit.Hosting.CsWebUi.Mvvm.csproj
  src/RunicToolkit.Hosting.Generators/RunicToolkit.Hosting.Generators.csproj
  src/RunicToolkit.Hosting.GenericHost/RunicToolkit.Hosting.GenericHost.csproj
  src/RunicToolkit.Hosting.WebUi/RunicToolkit.Hosting.WebUi.csproj
  src/RunicToolkit.Hosting/RunicToolkit.Hosting.csproj
  src/RunicToolkit.MVVM.Build/RunicToolkit.MVVM.Build.csproj
  src/RunicToolkit.MVVM.CommunityToolkit/RunicToolkit.MVVM.CommunityToolkit.csproj
  src/RunicToolkit.MVVM.ReactiveUI/RunicToolkit.MVVM.ReactiveUI.csproj
  src/RunicToolkit.MVVM/RunicToolkit.MVVM.csproj
  tools/RunicToolkit.MVVM.BindingCompiler/RunicToolkit.MVVM.BindingCompiler.csproj
  tools/dotnet-runic-toolkit/RunicToolkit.DotNet.RunicToolkit.csproj
)

for project in "${package_projects[@]}"; do
  dotnet pack "$repository_root/$project" \
    --configuration "$configuration" \
    --no-restore \
    -p:PackageVersion="$package_version" \
    -p:Version="$package_version" \
    -p:RepositoryCommit="$repository_commit" \
    -p:ContinuousIntegrationBuild=true \
    -p:RunicToolkitBuildMode=Verification \
    -p:RunicToolkitFrontendBuild=false \
    -p:RunicToolkitFrontendInstall=false \
    --output "$output_directory"
done

pwsh -NoProfile \
  -File "$repository_root/eng/verify-package-artifacts.ps1" \
  -PackageVersion "$package_version" \
  -PackageDirectory "$output_directory" \
  -RepositoryCommit "$repository_commit"
