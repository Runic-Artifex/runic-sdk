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
release_train_version="$(node "$repository_root/eng/compatibility-set-value.mjs" release-train-version)"
bun_version="$(node "$repository_root/eng/compatibility-set-value.mjs" toolchain bun)"
npm_version="$(node "$repository_root/eng/compatibility-set-value.mjs" toolchain npm)"
pnpm_version="$(node "$repository_root/eng/compatibility-set-value.mjs" toolchain pnpm)"
application_bridge_npm_version="${APPLICATION_BRIDGE_NPM_VERSION:-$package_version}"
runic_angular_npm_version="${RUNIC_ANGULAR_NPM_VERSION:-$package_version}"
runic_svelte_npm_version="${RUNIC_SVELTE_NPM_VERSION:-$release_train_version}"
runic_vite_npm_version="${RUNIC_VITE_NPM_VERSION:-$release_train_version}"
runic_desktop_npm_version="${RUNIC_DESKTOP_NPM_VERSION:-$release_train_version}"
runic_assets_version="${RunicAssetsPackageVersion:-$release_train_version}"
runic_desktop_version="${RunicDesktopPackageVersion:-$release_train_version}"
registry_dependencies="${RUNIC_USE_REGISTRY_DEPENDENCIES:-0}"
repository_commit="$(git -C "$repository_root" rev-parse HEAD)"
verification_root="$(mktemp -d /tmp/runic-application-pack.XXXXXXXXXX)"
verification_feed="$verification_root/feed"
verification_nuget="$verification_root/nuget"
command_line_worktree="$verification_root/runic-command-line"
assets_worktree="$verification_root/runic-assets"
translations_worktree="$verification_root/runic-translations"
desktop_worktree="$verification_root/runic-desktop"
command_line_revision="$(node "$repository_root/eng/compatibility-set-value.mjs" source runic-command-line)"
assets_revision="$(node "$repository_root/eng/compatibility-set-value.mjs" source runic-assets)"
translations_revision="$(node "$repository_root/eng/compatibility-set-value.mjs" source runic-translations)"
desktop_revision="$(node "$repository_root/eng/compatibility-set-value.mjs" source runic-desktop)"
cleanup() {
  if [[ "$registry_dependencies" != "1" ]]; then
    git -C "$repository_root/../runic-command-line" worktree remove --force "$command_line_worktree" 2>/dev/null || true
    git -C "$repository_root/../runic-assets" worktree remove --force "$assets_worktree" 2>/dev/null || true
    git -C "$repository_root/../runic-translations" worktree remove --force "$translations_worktree" 2>/dev/null || true
    git -C "$repository_root/../runic-desktop" worktree remove --force "$desktop_worktree" 2>/dev/null || true
  fi
  rm -rf -- "$verification_root"
}
trap cleanup EXIT
if [[ "$registry_dependencies" == "1" ]]; then
  verification_feed=""
else
  git -C "$repository_root/../runic-command-line" worktree add --detach "$command_line_worktree" "$command_line_revision"
  git -C "$repository_root/../runic-assets" worktree add --detach "$assets_worktree" "$assets_revision"
  git -C "$repository_root/../runic-translations" worktree add --detach "$translations_worktree" "$translations_revision"
  git -C "$repository_root/../runic-desktop" worktree add --detach "$desktop_worktree" "$desktop_revision"
  mkdir -p "$verification_feed"
  export NUGET_PACKAGES="$verification_nuget"
  dotnet pack "$command_line_worktree/src/Runic.CommandLine/Runic.CommandLine.csproj" --configuration "$configuration" --output "$verification_feed" -p:PackageVersion=1.0.0-preview.1
  dotnet restore "$assets_worktree/Runic.Assets.slnx" --configfile "$repository_root/NuGet.config" --source "$verification_feed" --source https://api.nuget.org/v3/index.json
  for asset_project in Runic.Assets Runic.Assets.AspNetCore Runic.Assets.Desktop; do
    dotnet pack "$assets_worktree/src/$asset_project/$asset_project.csproj" --configuration "$configuration" --no-restore --output "$verification_feed" -p:PackageVersion=1.0.0-preview.1 -p:RepositoryCommit="$assets_revision"
  done
  dotnet pack "$translations_worktree/dotnet/src/Runic.Translations/Runic.Translations.csproj" --configuration "$configuration" --output "$verification_feed" -p:PackageVersion=1.0.0-preview.1
  dotnet pack "$desktop_worktree/src/Runic.Desktop/Runic.Desktop.csproj" --configuration "$configuration" --output "$verification_feed" -p:PackageVersion=1.0.0-preview.1
  if [[ -n "${RUNIC_VERIFICATION_FEED_OUTPUT:-}" ]]; then
    mkdir -p "$RUNIC_VERIFICATION_FEED_OUTPUT"
    cp "$verification_feed"/*.nupkg "$RUNIC_VERIFICATION_FEED_OUTPUT"/
  fi
fi

if [[ ! "$package_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([+-][0-9A-Za-z.-]+)?$ ]]; then
  echo "Package version must be SemVer-compatible, for example 0.1.0-preview.1." >&2
  exit 2
fi

mkdir -p "$output_directory"
package_projects=(
  src/Runic.Application/Runic.Application.csproj
  src/Runic.Application.Hosting/Runic.Application.Hosting.csproj
  src/Runic.Application.Desktop/Runic.Application.Desktop.csproj
  src/Runic.Application.Testing/Runic.Application.Testing.csproj
  src/Runic.Application.Bridge/Runic.Application.Bridge.csproj
  tools/dotnet-runic-toolkit/Runic.Application.Tool.csproj
  templates/RunicToolkit.Templates/RunicToolkit.Templates.csproj
)

restore_options=()
if [[ -n "${NUGET_CONFIG_FILE:-}" ]]; then
  restore_options+=(--configfile "$NUGET_CONFIG_FILE")
fi
verification_properties=()
if [[ -n "$verification_feed" ]]; then
  verification_properties+=(-p:RunicVerificationFeed="$verification_feed")
fi

dotnet restore "$repository_root/RunicToolkit.slnx" \
  "${restore_options[@]}" \
  -p:RunicToolkitBuildMode=Verification \
  -p:RunicToolkitFrontendBuild=false \
  -p:RunicToolkitFrontendInstall=false \
  "${verification_properties[@]}"

dotnet build "$repository_root/RunicToolkit.slnx" \
  --configuration "$configuration" \
  --no-restore \
  -p:RunicToolkitBuildMode=Verification \
  -p:RunicToolkitFrontendBuild=false \
  -p:RunicToolkitFrontendInstall=false \
  "${verification_properties[@]}"

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
    -p:ApplicationBridgeTemplateVersion="$application_bridge_npm_version" \
    -p:RunicAngularTemplateVersion="$runic_angular_npm_version" \
    -p:RunicAssetsTemplateVersion="$runic_assets_version" \
    -p:RunicDesktopTemplateVersion="$runic_desktop_version" \
    -p:RunicDesktopNpmTemplateVersion="$runic_desktop_npm_version" \
    -p:RunicSvelteTemplateVersion="$runic_svelte_npm_version" \
    -p:RunicViteTemplateVersion="$runic_vite_npm_version" \
    -p:BunTemplateVersion="$bun_version" \
    -p:NpmTemplateVersion="$npm_version" \
    -p:PnpmTemplateVersion="$pnpm_version" \
    "${verification_properties[@]}" \
    --output "$output_directory"
done

pwsh -NoProfile \
  -File "$repository_root/eng/verify-package-artifacts.ps1" \
  -PackageVersion "$package_version" \
  -PackageDirectory "$output_directory" \
  -RepositoryCommit "$repository_commit"

consumer_project="$repository_root/tests/Runic.Application.PackageConsumer/Runic.Application.PackageConsumer.csproj"
dotnet restore "$consumer_project" \
  "${restore_options[@]}" \
  -p:PackageVersion="$package_version" \
  -p:PackageDirectory="$output_directory" \
  "${verification_properties[@]}"
dotnet run --project "$consumer_project" \
  --configuration "$configuration" \
  --no-restore \
  -p:PackageVersion="$package_version" \
  -p:PackageDirectory="$output_directory" \
  "${verification_properties[@]}"
