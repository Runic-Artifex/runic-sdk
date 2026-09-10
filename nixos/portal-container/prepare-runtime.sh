#!/usr/bin/env bash
# Use the locked desktop-flatpak-tools package; no host app installation is used.
set -euo pipefail
export FLATPAK_USER_DIR
FLATPAK_USER_DIR=$(realpath -m "${1:?Pass a dedicated runtime cache directory}")
flatpak remote-add --user --if-not-exists flathub https://flathub.org/repo/flathub.flatpakrepo >&2
refs=(org.gnome.Platform/x86_64/50 org.freedesktop.Platform.GL.default/x86_64/25.08)
commits=(545da92354a265d2c3572c91c39ac14dd7e74f9d8f9b66744ad50f478d2497c5 bcfd828b0c4739753cadb31964f8c1b0f90c8d2bed79bda2c2760438eb48c4b3)
for i in "${!refs[@]}"; do
  ref=${refs[$i]}
  commit=${commits[$i]}
  if ! flatpak info --user "$ref" >/dev/null 2>&1; then
    flatpak install --user --assumeyes --no-related flathub "$ref" >&2
  fi
  if test "$(flatpak info --user --show-commit "$ref")" != "$commit"; then
    flatpak update --user --assumeyes --no-related --commit="$commit" "$ref" >&2
  fi
  test "$(flatpak info --user --show-commit "$ref")" = "$commit"
done
# Managed mountfsd can clone these immutable, world-readable artifacts. Sharing
# this installation read-only avoids a runtime download/copy for every desktop.
nix store add-path "$FLATPAK_USER_DIR"
