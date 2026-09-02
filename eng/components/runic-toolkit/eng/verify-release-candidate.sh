#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "Usage: $0 <package-version> <output-directory> <github|public>" >&2
  exit 2
fi

package_version="$1"
if [[ ! "$package_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([+-][0-9A-Za-z.-]+)?$ ]]; then
  echo "Package version must be SemVer-compatible." >&2
  exit 2
fi
mkdir -p "$2"
package_directory="$(cd "$2" && pwd)"
registry="$3"
if [[ "$registry" != "github" && "$registry" != "public" ]]; then
  echo "Registry target must be 'github' or 'public'." >&2
  exit 2
fi

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
workspace_root="$(cd "$repository_root/.." && pwd)"
candidate_root="$(mktemp -d /tmp/runic-release-candidate.XXXXXXXXXX)"
verification_feed="$candidate_root/verification-feed"
integration_packages="$candidate_root/integrations"
desktop_worktree="$candidate_root/runic-desktop"
svelte_worktree="$candidate_root/runic-svelte"
vite_worktree="$candidate_root/runic-vite"

cleanup() {
  git -C "$workspace_root/runic-desktop" worktree remove --force "$desktop_worktree" 2>/dev/null || true
  git -C "$workspace_root/runic-svelte" worktree remove --force "$svelte_worktree" 2>/dev/null || true
  git -C "$workspace_root/runic-vite" worktree remove --force "$vite_worktree" 2>/dev/null || true
  case "$candidate_root" in
    /tmp/runic-release-candidate.*) rm -rf -- "$candidate_root" ;;
    *) echo "Refusing to remove unexpected path: $candidate_root" >&2 ;;
  esac
}
trap cleanup EXIT

desktop_revision="$(node "$repository_root/eng/compatibility-set-value.mjs" source runic-desktop)"
svelte_revision="$(node "$repository_root/eng/compatibility-set-value.mjs" source runic-svelte)"
vite_revision="$(node "$repository_root/eng/compatibility-set-value.mjs" source runic-vite)"
integration_version="$(node "$repository_root/eng/compatibility-set-value.mjs" release-train-version)"

git -C "$workspace_root/runic-desktop" worktree add --detach "$desktop_worktree" "$desktop_revision"
git -C "$workspace_root/runic-svelte" worktree add --detach "$svelte_worktree" "$svelte_revision"
git -C "$workspace_root/runic-vite" worktree add --detach "$vite_worktree" "$vite_revision"
mkdir -p "$integration_packages"
export npm_config_cache="$candidate_root/npm-cache"

RUNIC_VERIFICATION_FEED_OUTPUT="$verification_feed" \
  bash "$repository_root/eng/pack.sh" "$package_version" "$package_directory"
RUNIC_VERIFICATION_FEED="$verification_feed" \
  bash "$repository_root/tests/RunicToolkit.PackageCanary/Test-PackageCanary.sh" \
    "$package_version" "$package_directory"
bash "$repository_root/tests/Runic.Application.Bridge.AotSmoke/Test-PackageAot.sh" \
  "$package_version" "$package_directory"
node "$repository_root/eng/pack-npm.mjs" "$package_version" "$package_directory" "$registry"
node "$repository_root/eng/verify-npm-artifacts.mjs" "$package_version" "$package_directory"
if [[ "$registry" == "public" ]]; then
  authority_validator="$workspace_root/.github/.github/actions/validate-release-artifacts"
  repository_commit="$(git -C "$repository_root" rev-parse HEAD)"
  pwsh -NoProfile -File "$authority_validator/verify-nuget.ps1" \
    -Version "$package_version" \
    -ArtifactDirectory "$package_directory" \
    -RepositoryUrl "https://github.com/Runic-Artifex/runic-toolkit" \
    -RepositoryCommit "$repository_commit" \
    -ExpectedCount 7
  node "$authority_validator/verify-npm.mjs" \
    "$package_version" \
    "$package_directory" \
    "https://github.com/Runic-Artifex/runic-toolkit" \
    "$repository_commit" \
    2 \
    "https://registry.npmjs.org" \
    public
fi

(
  cd "$desktop_worktree"
  npm ci
  npm run build --workspace @runic-artifex/desktop
  npm pack --workspace @runic-artifex/desktop --ignore-scripts \
    --pack-destination "$integration_packages"
)
(
  cd "$svelte_worktree"
  npm ci
  npm run build --workspace @runic-artifex/svelte
  npm pack --workspace @runic-artifex/svelte \
    --pack-destination "$integration_packages"
)
(
  cd "$vite_worktree"
  npm ci
  npm run build
  npm pack --ignore-scripts --pack-destination "$integration_packages"
)

RUNIC_VERIFICATION_FEED="$verification_feed" \
  bash "$repository_root/tests/RunicToolkit.TemplateAcceptance/Test-Templates.sh" \
    "$package_version" \
    "$package_directory" \
    "$package_directory/runic-artifex-application-bridge-$package_version.tgz" \
    "$package_directory/runic-artifex-angular-$package_version.tgz" \
    "$integration_packages/runic-artifex-svelte-$integration_version.tgz" \
    "$integration_packages/runic-artifex-vite-plugin-runic-$integration_version.tgz" \
    "$integration_packages/runic-artifex-desktop-$integration_version.tgz"
