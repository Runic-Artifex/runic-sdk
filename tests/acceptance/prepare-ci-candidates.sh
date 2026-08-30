#!/usr/bin/env bash
set -euo pipefail

# PR validation consumes the release train as packages without publishing any
# of them. Every source checkout uses the same explicit branch so the feed is
# a coherent, disposable candidate set.
readonly candidate_ref="${RUNIC_CANDIDATE_REF:-codex/pre-v1-w105-w110}"
readonly workspace="${GITHUB_WORKSPACE:-$PWD}"
readonly sources="$workspace/.ci/candidate-sources"
readonly feed="${RUNNER_TEMP:-$workspace/.ci}/runic-candidate-feed"
readonly npm_feed="${RUNNER_TEMP:-$workspace/.ci}/runic-candidate-npm-feed"
readonly packages="${RUNNER_TEMP:-$workspace/.ci}/runic-candidate-packages"
readonly config="${RUNNER_TEMP:-$workspace/.ci}/runic-candidate.NuGet.config"
readonly version="1.0.0-preview.1"
readonly candidate_set="${RUNIC_CANDIDATE_SET:-samples}"

mkdir -p "$sources" "$feed" "$npm_feed" "$packages"

clone_source() {
  local repository="$1"
  git clone --depth 1 --branch "$candidate_ref" "https://github.com/Runic-Artifex/$repository.git" "$sources/$repository"
  git -C "$sources/$repository" rev-parse HEAD
}

cat > "$config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="candidate" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="candidate">
      <package pattern="Runic.*" />
      <package pattern="dotnet-runic*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
  <config>
    <add key="globalPackagesFolder" value="$packages" />
  </config>
</configuration>
EOF

pack() {
  local mode_property="$1"
  local project="$2"
  dotnet pack "$project" \
    --configuration Release \
    --output "$feed" \
    --configfile "$config" \
    -p:Version="$version" \
    -p:"$mode_property"=Development
}

restore_assets_packer() {
  dotnet restore "$sources/runic-assets/src/Runic.Assets.Packer/Runic.Assets.Packer.csproj" \
    --configfile "$config" \
    -p:RunicAssetsBuildMode=Development
}

pack_npm_candidates() {
  npm --prefix "$sources/runic-toolkit" ci --ignore-scripts
  (cd "$sources/runic-toolkit" && npm pack --workspace @runic-artifex/application-bridge --pack-destination "$npm_feed")
  npm --prefix "$sources/runic-desktop" ci --ignore-scripts
  (cd "$sources/runic-desktop" && npm pack --workspace @runic-artifex/desktop --pack-destination "$npm_feed")
  npm --prefix "$sources/runic-svelte" ci --ignore-scripts
  (cd "$sources/runic-svelte" && npm run build --workspace @runic-artifex/svelte)
  (cd "$sources/runic-svelte" && npm pack --workspace @runic-artifex/svelte --pack-destination "$npm_feed")
  (cd "$sources/runic-svelte" && npm run build --workspace @runic-artifex/sveltekit)
  (cd "$sources/runic-svelte" && npm pack --workspace @runic-artifex/sveltekit --pack-destination "$npm_feed")
  npm --prefix "$sources/runic-vite" ci --ignore-scripts
  (cd "$sources/runic-vite" && npm run build)
  (cd "$sources/runic-vite" && npm pack --pack-destination "$npm_feed")
  npm --prefix "$sources/runic-translations/web" ci --ignore-scripts
  (cd "$sources/runic-translations/web" && npm run build)
  (cd "$sources/runic-translations/web" && npm pack --pack-destination "$npm_feed")
}

case "$candidate_set" in
  command-line)
    clone_source runic-command-line
    pack RunicCommandLineBuildMode "$sources/runic-command-line/src/Runic.CommandLine/Runic.CommandLine.csproj"
    pack RunicCommandLineBuildMode "$sources/runic-command-line/src/Runic.CommandLine.Processes/Runic.CommandLine.Processes.csproj"
    test -f "$feed/Runic.CommandLine.$version.nupkg"
    ;;
  assets)
    clone_source runic-desktop
    clone_source runic-command-line
    clone_source runic-assets
    pack RunicDesktopBuildMode "$sources/runic-desktop/src/Runic.Desktop/Runic.Desktop.csproj"
    pack RunicCommandLineBuildMode "$sources/runic-command-line/src/Runic.CommandLine/Runic.CommandLine.csproj"
    restore_assets_packer
    pack RunicAssetsBuildMode "$sources/runic-assets/src/Runic.Assets/Runic.Assets.csproj"
    pack RunicAssetsBuildMode "$sources/runic-assets/src/Runic.Assets.AspNetCore/Runic.Assets.AspNetCore.csproj"
    pack RunicAssetsBuildMode "$sources/runic-assets/src/Runic.Assets.Desktop/Runic.Assets.Desktop.csproj"
    test -f "$feed/Runic.Assets.$version.nupkg"
    ;;
  translations)
    clone_source runic-translations
    pack RunicTranslationsBuildMode "$sources/runic-translations/dotnet/src/Runic.Translations/Runic.Translations.csproj"
    pack RunicTranslationsBuildMode "$sources/runic-translations/dotnet/src/Runic.Translations.Build/Runic.Translations.Build.csproj"
    test -f "$feed/Runic.Translations.$version.nupkg"
    ;;
  samples)
    clone_source runic-desktop
    clone_source runic-command-line
    clone_source runic-assets
    clone_source runic-translations
    clone_source runic-toolkit
    clone_source runic-svelte
    clone_source runic-vite
    pack RunicDesktopBuildMode "$sources/runic-desktop/src/Runic.Desktop/Runic.Desktop.csproj"
    pack RunicCommandLineBuildMode "$sources/runic-command-line/src/Runic.CommandLine/Runic.CommandLine.csproj"
    pack RunicCommandLineBuildMode "$sources/runic-command-line/src/Runic.CommandLine.Processes/Runic.CommandLine.Processes.csproj"
    restore_assets_packer
    pack RunicAssetsBuildMode "$sources/runic-assets/src/Runic.Assets/Runic.Assets.csproj"
    pack RunicAssetsBuildMode "$sources/runic-assets/src/Runic.Assets.AspNetCore/Runic.Assets.AspNetCore.csproj"
    pack RunicAssetsBuildMode "$sources/runic-assets/src/Runic.Assets.Desktop/Runic.Assets.Desktop.csproj"
    pack RunicTranslationsBuildMode "$sources/runic-translations/dotnet/src/Runic.Translations/Runic.Translations.csproj"
    pack RunicTranslationsBuildMode "$sources/runic-translations/dotnet/src/Runic.Translations.Build/Runic.Translations.Build.csproj"
    pack RunicTranslationsBuildMode "$sources/runic-translations/dotnet/tools/dotnet-runic-translations/dotnet-runic-translations.csproj"
    pack RunicToolkitBuildMode "$sources/runic-toolkit/src/Runic.Application/Runic.Application.csproj"
    pack RunicToolkitBuildMode "$sources/runic-toolkit/src/Runic.Application.Bridge/Runic.Application.Bridge.csproj"
    pack RunicToolkitBuildMode "$sources/runic-toolkit/src/Runic.Application.Desktop/Runic.Application.Desktop.csproj"
    test -f "$feed/Runic.Application.$version.nupkg"
    test -f "$feed/Runic.Application.Desktop.$version.nupkg"
    test -f "$feed/Runic.Assets.$version.nupkg"
    test -f "$feed/Runic.Translations.$version.nupkg"
    test -f "$feed/dotnet-runic-translations.$version.nupkg"
    pack_npm_candidates
    ;;
  *)
    printf 'Unknown candidate set: %s\n' "$candidate_set" >&2
    exit 2
    ;;
esac

if [[ -n "${GITHUB_ENV:-}" ]]; then
  {
    printf 'RUNIC_CANDIDATE_NUGET_CONFIG=%s\n' "$config"
    printf 'RUNIC_CANDIDATE_NUGET_FEED=%s\n' "$feed"
    printf 'RUNIC_CANDIDATE_NPM_FEED=%s\n' "$npm_feed"
    printf 'NUGET_PACKAGES=%s\n' "$packages"
  } >> "$GITHUB_ENV"
else
  printf 'RUNIC_CANDIDATE_NUGET_CONFIG=%s\n' "$config"
  printf 'RUNIC_CANDIDATE_NUGET_FEED=%s\n' "$feed"
  printf 'RUNIC_CANDIDATE_NPM_FEED=%s\n' "$npm_feed"
  printf 'NUGET_PACKAGES=%s\n' "$packages"
fi
