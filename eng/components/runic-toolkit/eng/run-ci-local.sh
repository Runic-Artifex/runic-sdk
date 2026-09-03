#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

usage() {
  cat <<'EOF'
Usage: ./eng/run-ci-local.sh [--dry-run] [--offline] [--verbose]

Runs the Linux verify job from .github/workflows/ci.yml in the pinned local
runner container. The wrapper always simulates a fork pull request, so
publication and registry round-trip steps cannot run.

Options:
  --dry-run  Validate workflow expansion without starting the job.
  --offline  Reuse cached actions and the cached runner image only.
  --verbose  Enable verbose act logging.
  -h, --help Show this help.
EOF
}

act_options=()
dry_run=false
offline=false
for argument in "$@"; do
  case "$argument" in
    --dry-run)
      act_options+=(--dryrun)
      dry_run=true
      ;;
    --offline)
      act_options+=(--action-offline-mode)
      offline=true
      ;;
    --verbose)
      act_options+=(--verbose)
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $argument" >&2
      usage >&2
      exit 2
      ;;
  esac
done

case "$(uname -m)" in
  x86_64)
    runner_base="ghcr.io/catthehacker/ubuntu@sha256:dff4ec57d90046a7283aafc314298380be82bfeccb9ad0f1b36c4ebe74aabe78"
    runner_platform="x86_64-dff4ec57d900"
    container_architecture="linux/amd64"
    ;;
  aarch64)
    runner_base="ghcr.io/catthehacker/ubuntu@sha256:f05db75c09fd27e3a7c36a48e3b91c6fe2633d386d37bfb099d1aedd23c9c86d"
    runner_platform="aarch64-f05db75c09fd"
    container_architecture="linux/arm64"
    ;;
  *)
    echo "Local CI supports x86_64 and aarch64 Linux hosts." >&2
    exit 2
    ;;
esac
runner_recipe="$(sha256sum "$repository_root/eng/act-runner.Containerfile" | cut -c1-12)"
runner_image="localhost/runic-artifex/act-ubuntu-24.04:$runner_platform-$runner_recipe"

runtime_directory="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
podman_socket="$runtime_directory/podman/podman.sock"
if [[ ! -S "$podman_socket" ]]; then
  echo "The rootless Podman API socket is not available at $podman_socket." >&2
  echo "Start it with: systemctl --user start podman.socket" >&2
  exit 2
fi
export DOCKER_HOST="unix://$podman_socket"

if [[ "$dry_run" == "true" ]]; then
  runner_image="$runner_base"
elif ! podman image exists "$runner_image"; then
  if [[ "$offline" == "true" ]]; then
    echo "The local runner image is not cached; run without --offline once." >&2
    exit 2
  fi
  image_start_seconds="$(date +%s)"
  podman build \
    --pull=always \
    --build-arg "RUNNER_IMAGE=$runner_base" \
    --tag "$runner_image" \
    --file "$repository_root/eng/act-runner.Containerfile" \
    "$repository_root/eng"
  image_elapsed_seconds="$(( $(date +%s) - image_start_seconds ))"
  printf 'Local runner image built (%dm %02ds)\n' \
    "$(( image_elapsed_seconds / 60 ))" \
    "$(( image_elapsed_seconds % 60 ))"
fi

if [[ -z "${GITHUB_TOKEN:-}" ]]; then
  if ! command -v gh >/dev/null 2>&1; then
    echo "Set GITHUB_TOKEN to a token with read access to Runic GitHub Packages." >&2
    exit 2
  fi
  GITHUB_TOKEN="$(gh auth token 2>/dev/null)" || {
    echo "Set GITHUB_TOKEN or authenticate the GitHub CLI with 'gh auth login'." >&2
    exit 2
  }
  export GITHUB_TOKEN
fi

act_options+=(--pull=false)

start_seconds="$(date +%s)"
set +e
nix shell --inputs-from "$repository_root" nixpkgs#act -c act \
  pull_request \
  --workflows "$repository_root/.github/workflows/ci.yml" \
  --job verify \
  --eventpath "$repository_root/.github/act.pull-request.json" \
  --platform "ubuntu-24.04=$runner_image" \
  --container-architecture "$container_architecture" \
  --secret GITHUB_TOKEN \
  "${act_options[@]}"
status=$?
set -e

elapsed_seconds="$(( $(date +%s) - start_seconds ))"
printf 'Local CI result: %s (%dm %02ds)\n' \
  "$([[ $status -eq 0 ]] && echo passed || echo failed)" \
  "$(( elapsed_seconds / 60 ))" \
  "$(( elapsed_seconds % 60 ))"
exit "$status"
