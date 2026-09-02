#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

configuration="Release"
registry_dependencies="${RUNIC_USE_REGISTRY_DEPENDENCIES:-0}"
verification_root="$(mktemp -d /tmp/runic-application-verification.XXXXXXXXXX)"
verification_feed="$verification_root/feed"
verification_nuget="$verification_root/nuget"
integration_packages="$verification_root/integrations"
command_line_worktree="$verification_root/runic-command-line"
assets_worktree="$verification_root/runic-assets"
translations_worktree="$verification_root/runic-translations"
desktop_worktree="$verification_root/runic-desktop"
svelte_worktree="$verification_root/runic-svelte"
vite_worktree="$verification_root/runic-vite"
command_line_revision="$(node eng/compatibility-set-value.mjs source runic-command-line)"
assets_revision="$(node eng/compatibility-set-value.mjs source runic-assets)"
translations_revision="$(node eng/compatibility-set-value.mjs source runic-translations)"
desktop_revision="$(node eng/compatibility-set-value.mjs source runic-desktop)"
svelte_revision="$(node eng/compatibility-set-value.mjs source runic-svelte)"
vite_revision="$(node eng/compatibility-set-value.mjs source runic-vite)"
cleanup() {
  if [[ "$registry_dependencies" != "1" ]]; then
    git -C "$repository_root/../runic-command-line" worktree remove --force "$command_line_worktree" 2>/dev/null || true
    git -C "$repository_root/../runic-assets" worktree remove --force "$assets_worktree" 2>/dev/null || true
    git -C "$repository_root/../runic-translations" worktree remove --force "$translations_worktree" 2>/dev/null || true
    git -C "$repository_root/../runic-desktop" worktree remove --force "$desktop_worktree" 2>/dev/null || true
    git -C "$repository_root/../runic-svelte" worktree remove --force "$svelte_worktree" 2>/dev/null || true
    git -C "$repository_root/../runic-vite" worktree remove --force "$vite_worktree" 2>/dev/null || true
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
  export RUNIC_VERIFICATION_FEED="$verification_feed"
fi
build_properties=(
  -p:RunicToolkitBuildMode=Verification
  -p:RunicToolkitFrontendBuild=false
  -p:RunicToolkitFrontendInstall=false
)
if [[ -n "$verification_feed" ]]; then
  build_properties+=(-p:RunicVerificationFeed="$verification_feed")
fi
restore_options=()
if [[ -n "${NUGET_CONFIG_FILE:-}" ]]; then
  restore_options+=(--configfile "$NUGET_CONFIG_FILE")
fi

pwsh -NoProfile -File eng/verify-namespaces.ps1
pwsh -NoProfile -File eng/verify-solution.ps1
pwsh -NoProfile -File eng/verify-architecture.ps1

bun install --frozen-lockfile
bun run verify

dotnet restore RunicToolkit.slnx "${restore_options[@]}" "${build_properties[@]}"
dotnet build RunicToolkit.slnx \
  --configuration "$configuration" \
  --no-restore \
  "${build_properties[@]}"

pwsh -NoProfile \
  -File eng/run-contract-tests.ps1 \
  -Configuration "$configuration"
bash eng/run-hosted-browser-e2e.sh
pwsh -NoProfile -File eng/verify-native-aot.ps1 -RuntimeIdentifier linux-x64 -Configuration "$configuration"
tool_native_publish="$verification_root/dotnet-runic-native"
dotnet publish tools/dotnet-runic-toolkit/Runic.Application.Tool.csproj \
  --configuration "$configuration" \
  --runtime linux-x64 \
  --self-contained true \
  -p:PublishAot=true \
  -p:PublishTrimmed=true \
  -p:TrimMode=full \
  -p:IlcTreatWarningsAsErrors=true \
  -p:PublishDir="$tool_native_publish" \
  "${build_properties[@]}"
bash tests/RunicToolkit.PackageCanary/Test-ToolParsePresentation.sh "$tool_native_publish/dotnet-runic"
bash tests/RunicToolkit.PackageCanary/Test-ToolMigration.sh "$tool_native_publish/dotnet-runic"

# Release-facing acceptance is package-only: the canonical seven NuGet artifacts,
# local bridge and Angular archives, and exact official Desktop/Svelte/Vite archives.
release_train_version="$(node eng/compatibility-set-value.mjs release-train-version)"
release_version="${RUNIC_PACKAGE_VERSION:-$release_train_version}"
bridge_npm_version="$release_version"
svelte_release_version="${RUNIC_SVELTE_NPM_VERSION:-$release_train_version}"
vite_release_version="${RUNIC_VITE_NPM_VERSION:-$release_train_version}"
desktop_release_version="${RUNIC_DESKTOP_NPM_VERSION:-$release_train_version}"
release_packages="${RUNIC_PACKAGE_OUTPUT:-$verification_root/packages}"
RUNIC_SVELTE_NPM_VERSION="$svelte_release_version" \
RUNIC_VITE_NPM_VERSION="$vite_release_version" \
APPLICATION_BRIDGE_NPM_VERSION="$bridge_npm_version" \
  bash eng/pack.sh "$release_version" "$release_packages"
bash tests/RunicToolkit.PackageCanary/Test-PackageCanary.sh "$release_version" "$release_packages"
bash tests/Runic.Application.Bridge.AotSmoke/Test-PackageAot.sh "$release_version" "$release_packages"
bash tests/Runic.Application.PackageConsumer/Test-HostileGenerator.sh "$release_version" "$release_packages"
node eng/pack-npm.mjs "$bridge_npm_version" "$release_packages" github
node eng/verify-npm-artifacts.mjs "$bridge_npm_version" "$release_packages"
mkdir -p "$integration_packages"
if [[ "$registry_dependencies" == "1" ]]; then
  node eng/download-github-npm.mjs dependencies "$integration_packages"
else
  git -C "$repository_root/../runic-svelte" worktree add --detach "$svelte_worktree" "$svelte_revision"
  git -C "$repository_root/../runic-vite" worktree add --detach "$vite_worktree" "$vite_revision"
  bash eng/pack-integration-npm.sh \
    "$bridge_npm_version" \
    "$release_packages" \
    "$integration_packages" \
    "$desktop_worktree" \
    "$svelte_worktree" \
    "$vite_worktree"
fi
assert_npm_archive() {
  local archive="$1"
  local expected_name="$2"
  node -e '
    const { execFileSync } = require("node:child_process");
    const archive = process.argv[1];
    const expected = process.argv[2];
    const manifest = JSON.parse(execFileSync("tar", ["-xOf", archive, "package/package.json"], { encoding: "utf8" }));
    if (manifest.name !== expected || !manifest.version || !manifest.exports || !manifest.license) {
      throw new Error(`Expected ${expected} release archive, found ${manifest.name}@${manifest.version}.`);
    }
  ' "$archive" "$expected_name"
}
assert_npm_archive "$release_packages/runic-artifex-application-bridge-$bridge_npm_version.tgz" "@runic-artifex/application-bridge"
assert_npm_archive "$release_packages/runic-artifex-angular-$bridge_npm_version.tgz" "@runic-artifex/angular"
assert_npm_archive "$integration_packages/runic-artifex-svelte-$svelte_release_version.tgz" "@runic-artifex/svelte"
assert_npm_archive "$integration_packages/runic-artifex-vite-plugin-runic-$vite_release_version.tgz" "@runic-artifex/vite-plugin-runic"
assert_npm_archive "$integration_packages/runic-artifex-desktop-$desktop_release_version.tgz" "@runic-artifex/desktop"
if [[ "$registry_dependencies" != "1" ]]; then
  (
    cd "$svelte_worktree"
    bun run test:package-consumers -- \
      "$release_packages/runic-artifex-application-bridge-$bridge_npm_version.tgz" \
      "$integration_packages/runic-artifex-svelte-$svelte_release_version.tgz" \
      "$integration_packages/runic-artifex-vite-plugin-runic-$vite_release_version.tgz"
  )
fi
release_authority="${RUNIC_RELEASE_AUTHORITY_PATH:-$repository_root/../.github/runic.release.json}"
node tests/RunicToolkit.AngularPackageCanary/test-package-consumer.mjs \
  "$release_authority" \
  "$release_packages/runic-artifex-application-bridge-$bridge_npm_version.tgz" \
  "$release_packages/runic-artifex-angular-$bridge_npm_version.tgz" \
  "$verification_root/angular-package-canary-receipt.json"
bash tests/RunicToolkit.TemplateAcceptance/Test-Templates.sh \
  "$release_version" \
  "$release_packages" \
  "$release_packages/runic-artifex-application-bridge-$bridge_npm_version.tgz" \
  "$release_packages/runic-artifex-angular-$bridge_npm_version.tgz" \
  "$integration_packages/runic-artifex-svelte-$svelte_release_version.tgz" \
  "$integration_packages/runic-artifex-vite-plugin-runic-$vite_release_version.tgz" \
  "$integration_packages/runic-artifex-desktop-$desktop_release_version.tgz"
